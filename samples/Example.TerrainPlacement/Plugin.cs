using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine.Rendering;
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
        private string[] sources = { "TEX_ROCK_A", "TEX_ROCK_B", "LARGE_ROCK_A", "LARGE_ROCK_B", "DIRTY_ROCK_A", "DIRTY_ROCK_B" };
        private static TerrainPlacementProbe? instance;
        private Harmony? hook;
        private readonly HashSet<byte> sourceIds = new HashSet<byte>();
        private List<string>? materialNames;
        private byte destination;
        private bool useCustom;
        private bool tested;
        private bool testedBaked;
        private bool sawRaw;
        private bool sawBakedRaw;
        private NativeArray<VoxelNodeData> pending;
        private VoxelNodeData[]? original;
        private VoxelNodeData[]? bakedOriginal;

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
            var requested = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES");
            if (!string.IsNullOrWhiteSpace(requested))
                sources = requested!.Split(';').Select(name => name.Trim()).Where(name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray();
            var target = useCustom ? TerrainMaterialReference.Registered(CustomIron) : TerrainMaterialReference.Stock(Iron);
            for (int index = 0; index < sources.Length; index++)
                Placement.Register(Id, "rock-" + index, TerrainPlacementScope.All,
                    TerrainMaterialReference.Stock(sources[index]), target);
            hook = new Harmony(Id);
            hook.Patch(AccessTools.DeclaredMethod(typeof(DensityComputeJob), "OnReadbackFinished"),
                postfix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveRaw)));
            hook.Patch(AccessTools.DeclaredMethod(typeof(OctreeNodeResources), nameof(OctreeNodeResources.SetVoxelData)),
                prefix: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObservePublication)));
            hook.Patch(AccessTools.DeclaredMethod(typeof(DensityComputeJob), nameof(DensityComputeJob.DispatchImmediate)),
                transpiler: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveSynchronousReadback)));
            hook.Patch(AccessTools.DeclaredMethod(typeof(AsteroidFieldResources), "BakeAsteroidLOD"),
                transpiler: new HarmonyMethod(typeof(TerrainPlacementProbe), nameof(ObserveSynchronousMeshInput)));
        }

        private void OnDestroy()
        {
            hook?.UnpatchSelf();
            if (ReferenceEquals(instance, this)) instance = null;
        }

        private static void ObserveRaw(AsyncGPUReadbackRequest __0, NativeArray<VoxelNodeData> ___voxelNodeDatas)
        {
            var self = instance;
            if (self == null || self.tested || !Placement.IsAvailable || __0.hasError
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || !___voxelNodeDatas.IsCreated) return;
            try
            {
                if (self.sourceIds.Count == 0 && !self.ResolveFixtureIds()) return;
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
            self.tested = true;
            var before = self.original;
            self.Check(before, index => __1[index], "TERRAIN PLACEMENT PROBE", "native octree copy");
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
