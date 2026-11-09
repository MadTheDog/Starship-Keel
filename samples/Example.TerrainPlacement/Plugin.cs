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
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
    public sealed class TerrainPlacementProbe : BaseUnityPlugin
    {
        private const string Id = "example.terrain-placement";
        private const string Iron = "RAW_IRON_A";
        private const string CustomIron = "example.terrainmaterial:iron";
        private const ulong StartingPlanetSeed = 90;
        private static readonly TerrainRadialBand ProbeBand = new TerrainRadialBand(-2000, 0);
        private static readonly TerrainAuthoredSurfaceDepthBand AuthoredProbeBand = new TerrainAuthoredSurfaceDepthBand(0, 2000);
        private string[] sources = { "TEX_ROCK_A", "TEX_ROCK_B", "LARGE_ROCK_A", "LARGE_ROCK_B", "DIRTY_ROCK_A", "DIRTY_ROCK_B" };
        private static TerrainPlacementProbe? instance;
        private Harmony? hook;
        private readonly HashSet<byte> sourceIds = new HashSet<byte>();
        private List<string>? materialNames;
        private byte destination;
        private bool useCustom;
        private bool radial;
        private bool authored;
        private bool stockCapture;
        private StockTerrainSurfaceSurvey? survey;
        private string? surveyPath;
        private int captureSpacing = 16;
        private bool canonicalDensityVerified;
        private readonly Dictionary<(int Level, float Spacing), int> surveyLattices =
            new Dictionary<(int, float), int>();
        private int surveyGateRejects;
        private int surveyInvalidJobs;
        private int surveyLatticeRejects;
        private int surveySourceRejects;
        private int surveyCrossingRejects;
        private int surveyCoverageRejects;
        private int surveyMaximumSources;
        private int surveyMaximumCoverage;
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
        private ulong pendingSystemSeed;
        private int insideBand;
        private int outsideBand;
        private int differsFromNominal;
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
            string? stockMode = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_STOCK_SURVEY");
            stockCapture = stockMode == "capture";
            if (!string.IsNullOrEmpty(stockMode))
            {
                if (!stockCapture && stockMode != "replay")
                    throw new InvalidOperationException("Stock terrain survey mode must be capture or replay.");
                var profile = OpenLabProfile();
                surveyPath = Path.Combine(profile.DirectoryPath, StockTerrainSurfaceSurvey.FileName);
                if (stockCapture)
                {
                    var requestedSpacing = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_STOCK_SPACING");
                    if (requestedSpacing != null && (!int.TryParse(requestedSpacing, out captureSpacing)
                        || captureSpacing <= 0 || captureSpacing > 2048 || (captureSpacing & (captureSpacing - 1)) != 0))
                        throw new InvalidOperationException("Stock terrain survey requires a canonical power-of-two spacing from 1 to 2048 metres.");
                    if (File.Exists(surveyPath)) throw new IOException("The stock terrain survey already exists. Use a new managed state name.");
                    Logger.LogInfo("TERRAIN STOCK SURFACE SURVEY capture armed at " + captureSpacing + " m spacing.");
                }
                else
                {
                    survey = StockTerrainSurfaceSurvey.Read(surveyPath);
                    Logger.LogInfo("TERRAIN STOCK SURFACE SURVEY loaded canonical LOD " + survey.Level
                        + " with " + survey.Spacing + " m spacing.");
                }
            }
            authored = survey != null || Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_AUTHORED") == "1";
            radial = stockCapture || authored || Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_RADIAL") == "1";
            diagnostics = radial && Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_DIAGNOSTICS") == "1";
            var requested = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES");
            if (!string.IsNullOrWhiteSpace(requested))
                sources = requested!.Split(';').Select(name => name.Trim()).Where(name => name.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray();
            var target = useCustom ? TerrainMaterialReference.Registered(CustomIron) : TerrainMaterialReference.Stock(Iron);
            for (int index = 0; !stockCapture && index < sources.Length; index++)
            {
                var source = TerrainMaterialReference.Stock(sources[index]);
                if (authored)
                    Placement.RegisterPlanetAuthoredSurfaceDepthBand(Id, "rock-" + index, source, target,
                        survey?.BodySeed ?? StartingPlanetSeed, ProbeDepthBand(), survey != null ? survey.Radius : ProbeSurfaceRadius);
                else if (radial)
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

        // An analytic test field independent of stock terrain. A real author supplies the field their own terrain uses.
        private static double? ProbeSurfaceRadius(TerrainAuthoredSurfaceSample sample)
            => sample.NominalRadius + 500 * sample.DirectionX;

        private TerrainAuthoredSurfaceDepthBand ProbeDepthBand() => survey == null ? AuthoredProbeBand
            : new TerrainAuthoredSurfaceDepthBand(0.75 * survey.Spacing, 4 * survey.Spacing);

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
            if (stockCapture || survey != null)
                Logger.LogInfo("TERRAIN STOCK SURFACE SURVEY DIAGNOSTICS "
                    + $"placement={Placement.IsAvailable} authored={Placement.AreAuthoredSurfaceBandsAvailable} "
                    + $"nativeGateRejects={surveyGateRejects} invalidJobs={surveyInvalidJobs} latticeRejects={surveyLatticeRejects} "
                    + $"sourceRejects={surveySourceRejects} crossingRejects={surveyCrossingRejects} coverageRejects={surveyCoverageRejects} "
                    + $"maxSources={surveyMaximumSources} maxCovered={surveyMaximumCoverage} canonicalReplay={canonicalDensityVerified} "
                    + "observedLattices=" + string.Join(", ", surveyLattices.OrderBy(pair => pair.Key.Level)
                        .ThenBy(pair => pair.Key.Spacing).Select(pair => $"L{pair.Key.Level}/{pair.Key.Spacing}m={pair.Value}")));
        }

        private static void ObserveDispatch()
        {
            var self = instance;
            if (self != null) Interlocked.Increment(ref self.dispatches);
        }

        private static Sevo.Framework.Runtime.ProfileSession OpenLabProfile()
        {
            var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            var game = Environment.GetEnvironmentVariable("SEVO_KEEL_LAB_TERRAIN_GAME");
            if (Application.companyName != "SEVO Keel Gen Lab 0110" || Application.productName != "SEVO WG Test"
                || Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE_DRAIN_STALLED_FRAME") != "1"
                || !Guid.TryParseExact(session, "N", out _) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(game)
                || !string.Equals(Path.GetFullPath(game), Path.GetFullPath(Paths.GameRootPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Terrain survey or buffer submission requires the exact isolated Keel generation lab.");
            return Sevo.Framework.Runtime.ProfileProtocol.Open(state, Paths.GameRootPath, session!);
        }

        private static void InstallLabSubmission(Harmony harmony)
        {
            OpenLabProfile();
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

        private static void ObserveRaw(AsyncGPUReadbackRequest __0, NativeArray<VoxelNodeData> ___voxelNodeDatas,
            int ___nodeId, DensityComputeJob.JobNodeData ___jobNodeData)
        {
            var self = instance;
            if (self?.diagnostics == true)
            {
                Interlocked.Increment(ref self.readbacks);
                if (__0.hasError) Interlocked.Increment(ref self.readbackErrors);
            }
            if (self == null || self.tested) return;
            bool stockSurvey = self.stockCapture || self.survey != null;
            if ((!self.stockCapture && !Placement.IsAvailable) || __0.hasError
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || !___voxelNodeDatas.IsCreated
                || (stockSurvey && !GameloopManager.IsMainThread))
            {
                if (stockSurvey) Interlocked.Increment(ref self.surveyGateRejects);
                return;
            }
            try
            {
                OctreeNodeData radialNode = default;
                float radialRadius = 0;
                ulong systemSeed = 0;
                if (self.radial)
                {
                    if ((!self.stockCapture && !(self.authored ? Placement.AreAuthoredSurfaceBandsAvailable : Placement.AreRadialBandsAvailable))
                        || ___nodeId <= 0 || ___nodeId >= OctreeNodeResources.OctreeNodeDatas.Length)
                    {
                        if (stockSurvey) self.surveyGateRejects++;
                        return;
                    }
                    var node = OctreeNodeResources.OctreeNodeDatas[___nodeId];
                    if (node.planetGenerationId == 0 || node.isDensityFromSave || !___jobNodeData.IsValid(node))
                    {
                        if (stockSurvey) self.surveyInvalidJobs++;
                        return;
                    }
                    if (self.diagnostics) Interlocked.Increment(ref self.planetReadbacks);
                    var body = PlanetarySystem.SolarBodies.FirstOrDefault(value => value.planetGenerationId == node.planetGenerationId);
                    if (body == null || body.radius <= 0 || ((self.authored || self.stockCapture)
                        && body.Seed != (self.survey?.BodySeed ?? StartingPlanetSeed)))
                    {
                        if (self.diagnostics) Interlocked.Increment(ref self.unresolvedReadbacks);
                        return;
                    }
                    radialNode = node;
                    radialRadius = body.radius;
                    systemSeed = PlanetarySystem.CurrentStarData.seed;
                    if (self.stockCapture)
                    {
                        self.CaptureSurvey(node, body, systemSeed, ___voxelNodeDatas);
                        return;
                    }
                    if (self.survey != null)
                    {
                        if (systemSeed != self.survey.SystemSeed || radialRadius != self.survey.NominalRadius) return;
                        self.VerifyCanonicalDensity(node, ___voxelNodeDatas);
                    }
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
                    self.pendingSystemSeed = systemSeed;
                    return;
                }
            }
            catch (Exception exception)
            {
                self.tested = true;
                self.Logger.LogError("TERRAIN PLACEMENT PROBE FAIL: raw readback inspection failed. " + exception.Message);
            }
        }

        private void CaptureSurvey(OctreeNodeData node, SolarBody body, ulong systemSeed, NativeArray<VoxelNodeData> voxels)
        {
            var lattice = (node.level, node.VoxelSize);
            surveyLattices.TryGetValue(lattice, out int observed);
            surveyLattices[lattice] = observed + 1;
            if (node.VoxelSize != captureSpacing || voxels.Length != StockTerrainSurfaceSurvey.Count
                || systemSeed == 0 || node.VoxelSize * StockTerrainSurfaceSurvey.Width > body.radius)
            {
                surveyLatticeRejects++;
                return;
            }
            if (sourceIds.Count == 0 && !ResolveFixtureIds())
            {
                surveySourceRejects++;
                return;
            }
            double x = node.position.x, y = node.position.y, z = node.position.z;
            double radius = Math.Sqrt(x * x + y * y + z * z);
            if (radius <= 0 || !double.IsFinite(radius))
            {
                surveyCrossingRejects++;
                return;
            }
            var density = new short[voxels.Length];
            for (int i = 0; i < density.Length; i++) density[i] = voxels[i].density;
            var candidate = new StockTerrainSurfaceSurvey(node.level, node.VoxelSize, systemSeed, body.Seed, body.radius,
                node.position.x, node.position.y, node.position.z, density);
            double? ground = candidate.Radius(x / radius, y / radius, z / radius);
            if (!ground.HasValue || Math.Abs(ground.Value - radius) >= 4 * node.VoxelSize)
            {
                surveyCrossingRejects++;
                return;
            }
            int covered = 0;
            int solidSources = 0;
            for (int i = 0; i < voxels.Length && covered < 32; i++)
            {
                var value = voxels[i];
                if (value.density >= 0 || (!sourceIds.Contains(value.texId0) && !sourceIds.Contains(value.texId1))) continue;
                solidSources++;
                var local = SamplePosition(node, i);
                double sampleRadius = Math.Sqrt(local.X * local.X + local.Y * local.Y + local.Z * local.Z);
                if (sampleRadius <= 0) continue;
                double? sampleGround = candidate.Radius(local.X / sampleRadius, local.Y / sampleRadius, local.Z / sampleRadius);
                double? depth = sampleGround - sampleRadius;
                if (depth >= 0.75 * candidate.Spacing && depth < 4 * candidate.Spacing) covered++;
            }
            surveyMaximumSources = Math.Max(surveyMaximumSources, solidSources);
            surveyMaximumCoverage = Math.Max(surveyMaximumCoverage, covered);
            if (covered < 32)
            {
                surveyCoverageRejects++;
                return;
            }
            candidate.WriteNew(surveyPath!);
            tested = true;
            Logger.LogInfo("TERRAIN STOCK SURFACE SURVEY CAPTURE PASS: LOD " + node.level + ", spacing " + node.VoxelSize
                + " m, stock outer crossing at radius " + ground.Value + " m. " + surveyPath);
        }

        private void VerifyCanonicalDensity(OctreeNodeData node, NativeArray<VoxelNodeData> voxels)
        {
            if (canonicalDensityVerified || node.level != survey!.Level || node.VoxelSize != survey.Spacing
                || node.position.x != survey.X || node.position.y != survey.Y || node.position.z != survey.Z) return;
            if (voxels.Length != StockTerrainSurfaceSurvey.Count)
                throw new InvalidOperationException("Replayed stock survey density length changed.");
            for (int i = 0; i < voxels.Length; i++)
                if (voxels[i].density != survey.DensityAt(i))
                    throw new InvalidOperationException("Replayed stock survey density no longer matches the capture.");
            canonicalDensityVerified = true;
            Logger.LogInfo("TERRAIN STOCK SURFACE SURVEY canonical density matched the captured readback.");
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
                self.CheckSpatial(before, __1);
                return;
            }
            self.tested = true;
            self.Check(before, index => __1[index], "TERRAIN PLACEMENT PROBE", "native octree copy");
        }

        private void CheckSpatial(VoxelNodeData[] before, NativeArray<VoxelNodeData> after)
        {
            string label = survey != null ? "TERRAIN STOCK SURFACE SURVEY REPLAY"
                : authored ? "TERRAIN AUTHORED SURFACE PLACEMENT PROBE" : "TERRAIN RADIAL PLACEMENT PROBE";
            var band = ProbeDepthBand();
            for (int index = 0; index < before.Length; index++)
            {
                var stock = before[index];
                var current = after[index];
                var (x, y, z) = SamplePosition(pendingNode, index);
                double radius = Math.Sqrt(x * x + y * y + z * z);
                double offset = radius - pendingRadius;
                bool inNominalBand = offset >= ProbeBand.MinimumOffset && offset < ProbeBand.MaximumOffset;
                bool solidSource = stock.density < 0 && (sourceIds.Contains(stock.texId0) || sourceIds.Contains(stock.texId1));
                double? ground = null;
                if (authored && solidSource && radius > 0)
                    ground = survey == null ? pendingRadius + 500 * (x / radius)
                        : pendingSystemSeed == survey.SystemSeed ? survey.Radius(x / radius, y / radius, z / radius) : null;
                double depth = ground.HasValue ? ground.Value - radius : double.NaN;
                bool inBand = authored
                    ? stock.density < 0 && depth >= band.MinimumDepth && depth < band.MaximumDepth
                    : inNominalBand;
                byte Expected(byte id) => inBand && sourceIds.Contains(id) ? destination : id;
                if (current.density != stock.density || current.color != stock.color
                    || current.texId0 != Expected(stock.texId0) || current.texId1 != Expected(stock.texId1))
                {
                    tested = true;
                    Logger.LogError(label + " FAIL: native sample diverged at nominal offset " + offset
                        + (authored ? ", authored depth " + depth : ""));
                    return;
                }
                if (!solidSource) continue;
                if (authored && inBand != inNominalBand) differsFromNominal++;
                if (inBand)
                {
                    insideBand++;
                    remappedLevels.Add(pendingNode.level);
                }
                else
                    outsideBand++;
            }
            if (remappedLevels.Count < 2 || outsideBand == 0 || (authored && differsFromNominal == 0)
                || (survey != null && !canonicalDensityVerified)) return;
            tested = true;
            Logger.LogInfo(label + " PASS: " + insideBand + " solid samples in band; "
                + outsideBand + " outside; remapped LODs " + string.Join(", ", remappedLevels.OrderBy(level => level))
                + (authored ? "; " + differsFromNominal + " classifications differed from nominal" : "")
                + "; density and packed color matched.");
        }

        private static (double X, double Y, double Z) SamplePosition(OctreeNodeData node, int index)
        {
            const int width = OctreeNodeData.DENSITY_WIDTH;
            var offset = node.GetPosition(index % width, index / width % width, index / (width * width));
            return ((double)node.position.x + offset.x, (double)node.position.y + offset.y,
                (double)node.position.z + offset.z);
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
