using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;
using World;
using World.Terrain;
using Placement = Sevo.Framework.Runtime.TerrainPlacement;

namespace Sevo.Example.TerrainPlacement
{
    [BepInPlugin(Id, "Terrain Placement Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class TerrainPlacementProbe : BaseUnityPlugin
    {
        private const string Id = "example.terrain-placement";
        private const string Iron = "RAW_IRON_A";
        private const string CustomIron = "example.terrainmaterial:iron";
        private static readonly TerrainRadialBand ProbeBand = new TerrainRadialBand(-2000, 0);
        private string[] sources = { "TEX_ROCK_A", "TEX_ROCK_B", "LARGE_ROCK_A", "LARGE_ROCK_B", "DIRTY_ROCK_A", "DIRTY_ROCK_B" };
        private static TerrainPlacementProbe? instance;
        private Harmony? hook;
        private readonly HashSet<byte> sourceIds = new HashSet<byte>();
        private List<string>? materialNames;
        private byte destination;
        private bool useCustom;
        private bool radial;
        private bool diagnostics;
        private int diagnosticReports;
        private float nextDiagnosticReport;
        private int dispatches;
        private int readbacks;
        private int readbackErrors;
        private int planetReadbacks;
        private int unresolvedReadbacks;
        private int observedCopies;
        private int submittedDispatches;
        private bool tested;
        private bool testedBaked;
        private bool sawRaw;
        private bool sawBakedRaw;
        private NativeArray<VoxelNodeData> pending;
        private VoxelNodeData[]? original;
        private VoxelNodeData[]? bakedOriginal;
        private OctreeNodeData pendingNode;
        private float pendingRadius;
        private int insideBand;
        private int outsideBand;
        private readonly HashSet<int> remappedLevels = new HashSet<int>();

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE") != "1") return;
            InstallProbe();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InstallProbe()
        {
            instance = this;
            useCustom = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_CUSTOM") == "1";
            radial = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_RADIAL") == "1";
            diagnostics = radial && Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_DIAGNOSTICS") == "1";
            var requested = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES");
            if (!string.IsNullOrWhiteSpace(requested))
                sources = requested!.Split(';').Select(name => name.Trim()).Where(name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray();
            var target = useCustom ? TerrainMaterialReference.Registered(CustomIron) : TerrainMaterialReference.Stock(Iron);
            for (int index = 0; index < sources.Length; index++)
            {
                var source = TerrainMaterialReference.Stock(sources[index]);
                if (radial)
                    Placement.RegisterPlanetRadialBand(Id, "rock-" + index, source, target, ProbeBand);
                else
                    Placement.Register(Id, "rock-" + index, TerrainPlacementScope.All, source, target);
            }
            hook = new Harmony(Id);
            hook.Patch(AccessTools.DeclaredMethod(typeof(DensityComputeJob), "OnReadbackFinished"),
                postfix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveRaw)));
            hook.Patch(AccessTools.DeclaredMethod(typeof(OctreeNodeResources), nameof(OctreeNodeResources.SetVoxelData)),
                prefix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObservePublication)));
            if (diagnostics)
                hook.Patch(AccessTools.DeclaredMethod(typeof(DensityComputeJob), "Dispatch"),
                    prefix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveDispatch)));
            if (diagnostics && Environment.GetEnvironmentVariable("SEVO_KEEL_LAB_SUBMIT_TERRAIN_BUFFER") == "1")
                InstallLabSubmission(hook);
            if (!radial)
            {
                hook.Patch(AccessTools.DeclaredMethod(typeof(DensityComputeJob), nameof(DensityComputeJob.DispatchImmediate)),
                    transpiler: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveSynchronousReadback)));
                hook.Patch(AccessTools.DeclaredMethod(typeof(AsteroidFieldResources), "BakeAsteroidLOD"),
                    transpiler: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveSynchronousMeshInput)));
            }
        }

        private void OnDestroy()
        {
            hook?.UnpatchSelf();
            if (ReferenceEquals(instance, this)) instance = null;
        }

        private void Update()
        {
            if (!diagnostics || tested || diagnosticReports == 8 || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || Time.unscaledTime < nextDiagnosticReport) return;
            nextDiagnosticReport = Time.unscaledTime + 10f;
            diagnosticReports++;
            Logger.LogInfo("TERRAIN RADIAL PLACEMENT PROBE DIAGNOSTICS "
                + $"dispatches={Volatile.Read(ref dispatches)} readbacks={Volatile.Read(ref readbacks)} "
                + $"readbackErrors={Volatile.Read(ref readbackErrors)} planetReadbacks={Volatile.Read(ref planetReadbacks)} "
                + $"unresolvedReadbacks={Volatile.Read(ref unresolvedReadbacks)} comparedCopies={observedCopies} "
                + $"inside={insideBand} outside={outsideBand} levels={remappedLevels.Count}");
        }

        private static void ObserveDispatch()
        {
            var self = instance;
            if (self != null) Interlocked.Increment(ref self.dispatches);
        }

        private static void InstallLabSubmission(Harmony harmony)
        {
            var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            var game = Environment.GetEnvironmentVariable("SEVO_KEEL_LAB_TERRAIN_GAME");
            if (Application.companyName != "SEVO Keel Gen Lab 0110" || Application.productName != "SEVO WG Test"
                || Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE_DRAIN_STALLED_FRAME") != "1"
                || !Guid.TryParseExact(session, "N", out _) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(game)
                || !string.Equals(Path.GetFullPath(game), Path.GetFullPath(Paths.GameRootPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Terrain buffer submission requires the exact isolated Keel generation lab.");
            Sevo.Framework.Runtime.ProfileProtocol.Open(state, Paths.GameRootPath, session!);
            var start = AccessTools.DeclaredMethod(typeof(DensityComputeJob), nameof(DensityComputeJob.CheckForJobStart));
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(SubmitLabBuffer)));
        }

        private static void SubmitLabBuffer()
        {
            var self = instance;
            if (self == null || !GameloopManager.IsMainThread || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            var buffer = VoxelRenderingManager.TerrainCommandBuffer;
            int queued = Volatile.Read(ref self.dispatches);
            if (queued == self.submittedDispatches || buffer == null || buffer.sizeInBytes == 0) return;
            Graphics.ExecuteCommandBuffer(buffer);
            buffer.Clear();
            if (self.submittedDispatches == 0) self.Logger.LogInfo("TERRAIN RADIAL PLACEMENT PROBE manually submitted the lab's native terrain buffer.");
            self.submittedDispatches = queued;
        }

        private static void ObserveRaw(AsyncGPUReadbackRequest __0, NativeArray<VoxelNodeData> ___voxelNodeDatas, int ___nodeId)
        {
            var self = instance;
            if (self?.diagnostics == true)
            {
                Interlocked.Increment(ref self.readbacks);
                if (__0.hasError) Interlocked.Increment(ref self.readbackErrors);
            }
            if (self == null || self.tested || !Placement.IsAvailable || __0.hasError
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || !___voxelNodeDatas.IsCreated) return;
            try
            {
                OctreeNodeData radialNode = default;
                float radialRadius = 0;
                if (self.radial)
                {
                    if (!Placement.AreRadialBandsAvailable || ___nodeId <= 0) return;
                    var node = OctreeNodeResources.OctreeNodeDatas[___nodeId];
                    if (node.planetGenerationId == 0) return;
                    if (self.diagnostics) Interlocked.Increment(ref self.planetReadbacks);
                    var body = PlanetarySystem.SolarBodies.FirstOrDefault(value => value.planetGenerationId == node.planetGenerationId);
                    if (body == null || body.radius <= 0)
                    {
                        if (self.diagnostics) Interlocked.Increment(ref self.unresolvedReadbacks);
                        return;
                    }
                    radialNode = node;
                    radialRadius = body.radius;
                }
                if (self.sourceIds.Count == 0 && !self.ResolveFixtureIds())
                {
                    if (self.diagnostics) Interlocked.Increment(ref self.unresolvedReadbacks);
                    return;
                }
                if (!self.sawRaw)
                {
                    self.sawRaw = true;
                    self.ReportSourceMix(___voxelNodeDatas, "TERRAIN PLACEMENT PROBE");
                }
                for (int index = 0; index < ___voxelNodeDatas.Length; index++)
                {
                    var sample = ___voxelNodeDatas[index];
                    if (sample.density >= 0 || (!self.sourceIds.Contains(sample.texId0) && !self.sourceIds.Contains(sample.texId1))) continue;
                    self.pending = ___voxelNodeDatas;
                    self.original = ___voxelNodeDatas.ToArray();
                    self.pendingNode = radialNode;
                    self.pendingRadius = radialRadius;
                    return;
                }
            }
            catch (Exception exception)
            {
                self.tested = true;
                self.Logger.LogError("TERRAIN PLACEMENT PROBE FAIL: raw readback inspection failed. " + exception.Message);
            }
        }

        private bool ResolveFixtureIds()
        {
            var rows = WorldTerrainResources.TerrainMaterialDatas;
            if (!rows.IsCreated) return false;
            var assets = UnityEngine.Resources.FindObjectsOfTypeAll<WorldTerrainResources>()
                .Where(asset => asset.terrainTextures.Count <= rows.Length).ToArray();
            if (assets.Length != 1) return false;
            var names = assets[0].terrainTextures.Select(texture => texture.ShaderName).ToList();
            materialNames = names;
            int iron;
            if (useCustom)
            {
                if (!Sevo.Framework.Runtime.TerrainMaterialRegistration.TryGetId(CustomIron, out byte customId)) return false;
                iron = customId;
            }
            else
                iron = names.IndexOf(Iron);
            if (iron < 0 || iron > byte.MaxValue || !rows[iron].IsRawOre) return false;
            destination = (byte)iron;
            foreach (var rock in sources)
            {
                int id = names.IndexOf(rock);
                if (id >= 0 && id <= byte.MaxValue) sourceIds.Add((byte)id);
            }
            return sourceIds.Count > 0;
        }

        private static void ObservePublication(NativeArray<VoxelNodeData> __1)
        {
            var self = instance;
            if (self == null || self.tested || self.original == null || !__1.Equals(self.pending)) return;
            var before = self.original;
            self.original = null;
            if (self.radial)
            {
                self.observedCopies++;
                self.CheckRadial(before, __1);
                return;
            }
            self.tested = true;
            self.Check(before, index => __1[index], "TERRAIN PLACEMENT PROBE", "native octree copy");
        }

        private void CheckRadial(VoxelNodeData[] before, NativeArray<VoxelNodeData> after)
        {
            const int width = OctreeNodeData.DENSITY_WIDTH;
            for (int index = 0; index < before.Length; index++)
            {
                var stock = before[index];
                var current = after[index];
                var local = pendingNode.GetPosition(index % width, index / width % width, index / (width * width));
                double x = (double)pendingNode.position.x + local.x;
                double y = (double)pendingNode.position.y + local.y;
                double z = (double)pendingNode.position.z + local.z;
                double offset = Math.Sqrt(x * x + y * y + z * z) - pendingRadius;
                bool inBand = offset >= ProbeBand.MinimumOffset && offset < ProbeBand.MaximumOffset;
                byte Expected(byte id) => inBand && sourceIds.Contains(id) ? destination : id;
                if (current.density != stock.density || current.color != stock.color
                    || current.texId0 != Expected(stock.texId0) || current.texId1 != Expected(stock.texId1))
                {
                    tested = true;
                    Logger.LogError("TERRAIN RADIAL PLACEMENT PROBE FAIL: native sample diverged at offset " + offset);
                    return;
                }
                if (stock.density >= 0 || (!sourceIds.Contains(stock.texId0) && !sourceIds.Contains(stock.texId1))) continue;
                if (inBand)
                {
                    insideBand++;
                    remappedLevels.Add(pendingNode.level);
                }
                else
                    outsideBand++;
            }
            if (remappedLevels.Count < 2 || outsideBand == 0) return;
            tested = true;
            Logger.LogInfo("TERRAIN RADIAL PLACEMENT PROBE PASS: " + insideBand + " solid samples in band; "
                + outsideBand + " outside; remapped LODs " + string.Join(", ", remappedLevels.OrderBy(level => level))
                + "; density and packed color matched.");
        }

        private static IEnumerable<CodeInstruction> ObserveSynchronousReadback(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var getData = code.Single(instruction => instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method
                && method.DeclaringType == typeof(UnityEngine.ComputeBuffer) && method.Name == "GetData");
            code.InsertRange(code.IndexOf(getData) + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(TerrainPlacementProbe), nameof(ObserveBakedRaw)))
            });
            return code;
        }

        private static IEnumerable<CodeInstruction> ObserveSynchronousMeshInput(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var dispatch = AccessTools.DeclaredMethod(typeof(DensityComputeJob), nameof(DensityComputeJob.DispatchImmediate));
            var call = code.Single(instruction => (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && Equals(instruction.operand, dispatch));
            code.InsertRange(code.IndexOf(call) + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(AsteroidFieldResources), "voxelDatas")),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(TerrainPlacementProbe), nameof(ObserveBakedPublication)))
            });
            return code;
        }

        private static void ObserveBakedRaw(VoxelNodeData[] raw)
        {
            var self = instance;
            if (self == null || self.testedBaked || !Placement.IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo)
                return;
            try
            {
                if (self.sourceIds.Count == 0 && !self.ResolveFixtureIds()) return;
                if (!self.sawBakedRaw)
                {
                    self.sawBakedRaw = true;
                    self.ReportSourceMix(raw, "BAKED TERRAIN PLACEMENT PROBE");
                }
                if (raw.Any(value => value.density < 0 && (self.sourceIds.Contains(value.texId0) || self.sourceIds.Contains(value.texId1))))
                    self.bakedOriginal = (VoxelNodeData[])raw.Clone();
            }
            catch (Exception exception)
            {
                self.testedBaked = true;
                self.Logger.LogError("BAKED TERRAIN PLACEMENT PROBE FAIL: " + exception.Message);
            }
        }

        private static void ObserveBakedPublication(VoxelNodeData[] values)
        {
            var self = instance;
            if (self == null || self.testedBaked || self.bakedOriginal == null) return;
            self.testedBaked = true;
            self.Check(self.bakedOriginal, index => values[index], "BAKED TERRAIN PLACEMENT PROBE", "native mesh copy");
        }

        private void ReportSourceMix(IEnumerable<VoxelNodeData> raw, string label)
        {
            var seen = new SortedSet<byte>();
            var solid = new SortedSet<byte>();
            int solidCount = 0;
            foreach (var sample in raw)
            {
                seen.Add(sample.texId0);
                seen.Add(sample.texId1);
                if (sample.density >= 0) continue;
                solidCount++;
                solid.Add(sample.texId0);
                solid.Add(sample.texId1);
            }
            string Name(byte id) => id + "=" + (id < materialNames!.Count ? materialNames[id] : "outside-stock");
            Logger.LogInfo(label + ": GPU readback IDs [" + string.Join(", ", seen.Select(Name))
                + "]; solid samples " + solidCount + " IDs [" + string.Join(", ", solid.Select(Name))
                + "]; fixture sources [" + string.Join(", ", sourceIds.OrderBy(id => id).Select(Name)) + "]; target " + Name(destination) + ".");
        }

        private void Check(VoxelNodeData[] before, Func<int, VoxelNodeData> read, string label, string boundary)
        {
            int changes = 0;
            for (int index = 0; index < before.Length; index++)
            {
                var current = read(index);
                var stock = before[index];
                byte Expected(byte id) => sourceIds.Contains(id) ? destination : id;
                if (current.color != stock.color || current.density != stock.density
                    || current.texId0 != Expected(stock.texId0) || current.texId1 != Expected(stock.texId1))
                {
                    Logger.LogError(label + " FAIL: material or protected sample data diverged before " + boundary + ".");
                    return;
                }
                if (stock.texId0 != current.texId0 || stock.texId1 != current.texId1) changes++;
            }
            Logger.LogInfo(label + " PASS: " + (useCustom ? CustomIron : Iron) + " remapped " + changes
                + " samples before " + boundary + "; density and packed color matched.");
        }
    }
}
