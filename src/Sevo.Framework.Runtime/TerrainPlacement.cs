using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Applies deterministic named material substitutions to newly generated terrain samples.</summary>
    public static class TerrainPlacement
    {
        internal const int VoxelCount = 8000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, TerrainPlacementRule> Rules = new Dictionary<string, TerrainPlacementRule>(StringComparer.Ordinal);
        private static readonly Dictionary<(TerrainPlacementScope Scope, ulong? Seed), TerrainPlacementPlan> Plans =
            new Dictionary<(TerrainPlacementScope, ulong?), TerrainPlacementPlan>();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<DensityComputeJob, object> SuccessfulReadbacks =
            new ConditionalWeakTable<DensityComputeJob, object>();
        private static readonly object Completed = new object();
        private static Action<string>? report;
        private static bool radialBands;
        private static CapabilityResult? asteroidDepositCoordinates;
        private static bool authoredSurfaceBands;
        private static int mainThread;
        private static TerrainPlacementRule[]? declarations;
        private static NativeArray<TerrainMaterialData> materialPublication;
        private static bool inspectedMaterialPublication;
        private static bool ownsRegisteredPublication;
        private static Dictionary<string, byte>? stockNames;

        public static bool IsAvailable => report != null;
        public static bool AreRadialBandsAvailable => IsAvailable && radialBands;
        public static bool AreAuthoredSurfaceBandsAvailable => IsAvailable && authoredSurfaceBands;

        public static void Register(string ownerId, string localName, TerrainPlacementScope scope,
            TerrainMaterialReference source, TerrainMaterialReference destination, ulong? seed = null)
            => AddRule(new TerrainPlacementRule(ownerId + ":" + localName, scope, source, destination, seed));

        /// <summary>Replaces a source material in a planet or moon's nominal elevation band.</summary>
        public static void RegisterPlanetRadialBand(string ownerId, string localName,
            TerrainMaterialReference source, TerrainMaterialReference destination, TerrainRadialBand band, ulong? seed = null)
            => AddRule(new TerrainPlacementRule(ownerId + ":" + localName, TerrainPlacementScope.Planets,
                source, destination, seed, band));

        /// <summary>Replaces solid samples at an inward depth below a surface supplied by a mod.</summary>
        /// <param name="surfaceRadius">Pure body-local surface radius in metres, or null outside the authored field.</param>
        /// <remarks>Use the same immutable field as the terrain generator or survey. This provider runs on the
        /// game thread for eligible generated samples. Keep it independent of resident chunks, LOD and time.</remarks>
        public static void RegisterPlanetAuthoredSurfaceDepthBand(string ownerId, string localName,
            TerrainMaterialReference source, TerrainMaterialReference destination, ulong seed,
            TerrainAuthoredSurfaceDepthBand band, Func<TerrainAuthoredSurfaceSample, double?> surfaceRadius)
            => AddRule(new TerrainPlacementRule(ownerId + ":" + localName, TerrainPlacementScope.Planets,
                source, destination, seed, depth: band, surfaceRadius: surfaceRadius));

        private static void AddRule(TerrainPlacementRule rule)
        {
            lock (Gate)
            {
                if (declarations != null) throw new InvalidOperationException("Terrain placement registration is closed for this process.");
                if (Rules.ContainsKey(rule.Key)) throw new InvalidOperationException("Duplicate terrain placement rule: " + rule.Key);
                Rules.Add(rule.Key, rule);
            }
        }

        internal static void EnableRadialBands() => radialBands = true;
        internal static void SetAsteroidDepositCoordinates(CapabilityResult result) => asteroidDepositCoordinates = result;
        internal static void EnableAuthoredSurfaceBands() => authoredSurfaceBands = true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> warning)
        {
            var compute = typeof(DensityComputeJob);
            var patch = typeof(TerrainPlacement);
            var readback = AccessTools.DeclaredMethod(compute, "OnReadbackFinished", new[] { typeof(AsyncGPUReadbackRequest) })
                ?? throw new MissingMethodException("OnReadbackFinished");
            var publish = AccessTools.DeclaredMethod(compute, nameof(DensityComputeJob.CheckForFinishedJobs), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(DensityComputeJob.CheckForFinishedJobs));
            var immediate = AccessTools.DeclaredMethod(compute, nameof(DensityComputeJob.DispatchImmediate),
                new[] { typeof(OctreeNodeData), typeof(AsteroidData), typeof(VoxelNodeData[]) })
                ?? throw new MissingMethodException(nameof(DensityComputeJob.DispatchImmediate));
            harmony.Patch(readback, prefix: new HarmonyMethod(patch, nameof(RecordSuccessfulReadback)));
            harmony.Patch(publish, transpiler: new HarmonyMethod(patch, nameof(BridgeGeneratedCopy)) { priority = Priority.Last });
            harmony.Patch(immediate, postfix: new HarmonyMethod(patch, nameof(AfterSynchronousReadback)));
            mainThread = Environment.CurrentManagedThreadId;
            report = warning;
        }

        internal static void Disable()
        {
            report = null;
            radialBands = false;
            authoredSurfaceBands = false;
            Plans.Clear();
            stockNames = null;
            materialPublication = default;
            inspectedMaterialPublication = false;
            ownsRegisteredPublication = false;
        }

        private static TerrainPlacementRule[] Seal()
        {
            lock (Gate) return declarations ??= Rules.Values.ToArray();
        }

        private static bool ActiveOnMainThread()
        {
            if (report == null || PlayerState.sessionMode != PlayerState.SessionMode.solo) return false;
            if (Environment.CurrentManagedThreadId == mainThread) return true;
            WarnOnce("Terrain placement skipped because the native readback callback moved off the game thread.");
            return false;
        }

        private static void RecordSuccessfulReadback(DensityComputeJob __instance, AsyncGPUReadbackRequest __0)
        {
            SuccessfulReadbacks.Remove(__instance);
            if (ActiveOnMainThread() && !__0.hasError && (Seal().Length > 0
                || (OreDepositRegistration.IsAvailable && OreDepositRegistration.Seal().Length > 0)))
                SuccessfulReadbacks.Add(__instance, Completed);
        }

        private static IEnumerable<CodeInstruction> BridgeGeneratedCopy(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var copy = AccessTools.DeclaredMethod(typeof(OctreeNodeResources), nameof(OctreeNodeResources.SetVoxelData),
                new[] { typeof(int).MakeByRefType(), typeof(NativeArray<VoxelNodeData>) });
            var bridge = AccessTools.DeclaredMethod(typeof(TerrainPlacement), nameof(CopyGeneratedTerrain));
            var index = code.FindIndex(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, copy));
            if (copy == null || bridge == null || index < 4 || code[index].blocks.Count != 0
                || code.Count(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, copy)) != 1
                || code[index - 2].opcode != OpCodes.Ldloc_1 || code[index - 4].opcode != OpCodes.Ldloca_S)
                throw new InvalidOperationException("Live terrain density copy pattern changed.");

            var node = new CodeInstruction(code[index - 4].opcode, code[index - 4].operand);
            code[index].opcode = OpCodes.Ldloc_1;
            code[index].operand = null;
            code.Insert(index + 1, node);
            code.Insert(index + 2, new CodeInstruction(OpCodes.Call, bridge));
            return code;
        }

        private static void CopyGeneratedTerrain(ref int id, NativeArray<VoxelNodeData> values,
            DensityComputeJob job, ref OctreeNodeData node)
        {
            if (SuccessfulReadbacks.Remove(job) && ActiveOnMainThread())
            {
                try
                {
                    if (values.IsCreated && values.Length == VoxelCount)
                    {
                        var scope = node.planetGenerationId != 0 ? TerrainPlacementScope.Planets : TerrainPlacementScope.Asteroids;
                        bool needsBody = OreDepositRegistration.IsAvailable && OreDepositRegistration.Seal().Length != 0 || Seal().Any(rule => (rule.Scope & scope) != 0
                            && (rule.BodySeed.HasValue || (radialBands && rule.Band.HasValue)));
                        float radius = 0f;
                        var seed = needsBody ? FindSeed(node, out radius) : null;
                        var plan = GetPlan(scope, seed);
                        var deposits = ResolveDeposits(scope, seed);
                        if (plan?.HasChanges == true || deposits.Length != 0)
                        {
                            bool mapSpatial = plan != null && (plan.HasRadialBands || plan.HasAuthoredSurfaceBands)
                                && seed.HasValue && CanMapRadial(node, radius);
                            ulong systemSeed = plan?.HasAuthoredSurfaceBands == true ? GetCurrentSystemSeed() : 0;
                            for (int index = 0; index < values.Length; index++)
                            {
                                var original = values[index];
                                var updated = TryDeposit(original, deposits, node, radius, index, out var deposit) ? deposit
                                    : plan == null ? original : mapSpatial
                                    ? RemapSpatial(original, plan, node, systemSeed, seed.GetValueOrDefault(), radius, index)
                                    : Remap(original, plan);
                                if (updated.texId0 != original.texId0 || updated.texId1 != original.texId1)
                                    values[index] = updated;
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    WarnOnce("Generated terrain placement failed: " + exception.Message);
                }
            }
            OctreeNodeResources.SetVoxelData(ref id, values);
        }

        private static void AfterSynchronousReadback(OctreeNodeData __0, AsteroidData __1, VoxelNodeData[] __2)
        {
            if (!ActiveOnMainThread() || __2.Length != VoxelCount) return;
            try
            {
                var plan = GetPlan(TerrainPlacementScope.Asteroids, __1.seed);
                var deposits = ResolveDeposits(TerrainPlacementScope.Asteroids, __1.seed);
                if (plan?.HasChanges != true && deposits.Length == 0) return;
                for (int index = 0; index < __2.Length; index++)
                {
                    var original = __2[index];
                    __2[index] = TryDeposit(original, deposits, __0, 0, index, out var deposit)
                        ? deposit : plan == null ? original : Remap(original, plan);
                }
            }
            catch (Exception exception)
            {
                WarnOnce("Baked asteroid placement failed: " + exception.Message);
            }
        }

        private static (OreDepositField Field, byte Host, byte Material, double? Depth)[] ResolveDeposits(
            TerrainPlacementScope scope, ulong? seed)
        {
            var empty = Array.Empty<(OreDepositField, byte, byte, double?)>();
            if (!OreDepositRegistration.IsAvailable) return empty;
            var declarations = OreDepositRegistration.Seal();
            if (!declarations.Any(rule => (rule.Scope & scope) != 0)) return empty;
            if (scope == TerrainPlacementScope.Asteroids && asteroidDepositCoordinates is not { Available: true })
            {
                WarnOnce("Asteroid ore deposits skipped: " + (asteroidDepositCoordinates?.Reason ?? "coordinate audit unavailable."));
                return empty;
            }
            if (!seed.HasValue || SaveManager.playerSave is not { seed: > 0 } save)
            {
                WarnOnce("Ore deposits skipped because a native world or body seed is unavailable.");
                return empty;
            }
            if (!TerrainMaterialRegistration.TryGetStockCount(out int stockCount))
            {
                WarnOnce("Ore deposits skipped because the registered terrain material table is unavailable.");
                return empty;
            }
            var live = WorldTerrainResources.TerrainMaterialDatas;
            if (stockNames == null || !live.Equals(materialPublication) || !ownsRegisteredPublication)
            {
                stockNames = ReadStockNames(stockCount);
                materialPublication = live;
                inspectedMaterialPublication = true;
                ownsRegisteredPublication = true;
                Plans.Clear();
            }
            var active = new List<(OreDepositField, byte, byte, double?)>();
            foreach (var rule in declarations)
            {
                if ((rule.Scope & scope) == 0) continue;
                if (stockNames == null || !stockNames.TryGetValue(rule.Host, out byte host)
                    || !TerrainMaterialRegistration.TryGetId(rule.Material, out byte material))
                {
                    WarnOnce($"Ore deposit '{rule.Key}' skipped because its host or registered material is unavailable.");
                    continue;
                }
                active.Add((new OreDepositField(rule.Key, save.seed, seed.Value, rule.Spacing, rule.Radius, rule.Chance),
                    host, material, rule.MaximumNominalDepth));
            }
            return active.ToArray();
        }

        private static bool TryDeposit(VoxelNodeData value,
            (OreDepositField Field, byte Host, byte Material, double? Depth)[] deposits,
            in OctreeNodeData node, float radius, int index, out VoxelNodeData result)
        {
            result = value;
            if (deposits.Length == 0 || value.density >= 0 || value.texId0 != value.texId1) return false;
            const int width = OctreeNodeData.DENSITY_WIDTH;
            var centre = node.GetPosition(index % width, index / width % width, index / (width * width));
            double x = (double)node.position.x + centre.x;
            double y = (double)node.position.y + centre.y;
            double z = (double)node.position.z + centre.z;
            foreach (var deposit in deposits)
            {
                if (value.texId0 != deposit.Host) continue;
                if (deposit.Depth is double limit)
                {
                    double depth = radius - Math.Sqrt(x * x + y * y + z * z);
                    if (depth < 0 || depth >= limit) continue;
                }
                if (!deposit.Field.Contains(x, y, z)) continue;
                result.texId0 = result.texId1 = deposit.Material;
                return true;
            }
            return false;
        }

        private static VoxelNodeData Remap(VoxelNodeData value, TerrainPlacementPlan plan)
        {
            value.texId0 = plan.Map(value.texId0);
            value.texId1 = plan.Map(value.texId1);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CanMapRadial(in OctreeNodeData node, float radius)
            => radius > 0f && float.IsFinite(radius) && node.VoxelSize > 0f && float.IsFinite(node.VoxelSize);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static VoxelNodeData RemapSpatial(VoxelNodeData value, TerrainPlacementPlan plan,
            in OctreeNodeData node, ulong systemSeed, ulong seed, float radius, int index)
        {
            bool hasRadial = plan.NeedsRadialOffset(value.texId0) || plan.NeedsRadialOffset(value.texId1);
            bool solid = value.density < 0;
            bool hasAuthoredSurface = solid && systemSeed != 0
                && (plan.NeedsAuthoredSurface(value.texId0) || plan.NeedsAuthoredSurface(value.texId1));
            if (!hasRadial && !hasAuthoredSurface)
                return Remap(value, plan);

            const int width = OctreeNodeData.DENSITY_WIDTH;
            var centre = node.GetPosition(index % width, index / width % width, index / (width * width));
            double x = (double)node.position.x + centre.x;
            double y = (double)node.position.y + centre.y;
            double z = (double)node.position.z + centre.z;
            var first = value.texId0;
            var second = value.texId1;
            if (hasAuthoredSurface && TerrainAuthoredSurfaceSample.TryCreate(seed, radius, x, y, z, out var sample, systemSeed))
            {
                double offset = sample.Radius - radius;
                value.texId0 = plan.NeedsAuthoredSurface(first)
                    ? plan.MapAuthoredSurface(first, sample, solid) : plan.Map(first, offset);
                value.texId1 = first == second ? value.texId0 : plan.NeedsAuthoredSurface(second)
                    ? plan.MapAuthoredSurface(second, sample, solid) : plan.Map(second, offset);
            }
            else
            {
                double offset = Math.Sqrt(x * x + y * y + z * z) - radius;
                value.texId0 = plan.Map(first, offset);
                value.texId1 = plan.Map(second, offset);
            }
            return value;
        }

        private static ulong? FindSeed(OctreeNodeData node, out float radius)
        {
            radius = 0f;
            if (node.planetGenerationId != 0)
            {
                foreach (var body in PlanetarySystem.SolarBodies)
                {
                    if (body.planetGenerationId != node.planetGenerationId) continue;
                    if (radialBands || authoredSurfaceBands) radius = GetNominalRadius(body);
                    return body.Seed;
                }
                return null;
            }
            var asteroids = AsteroidResources.datas;
            return asteroids.IsCreated && node.asteroidId > 0 && node.asteroidId < asteroids.Length
                ? asteroids[node.asteroidId].seed
                : (ulong?)null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetNominalRadius(SolarBody body) => body.radius;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong GetCurrentSystemSeed() => PlanetarySystem.CurrentStarData.seed;

        private static TerrainPlacementPlan? GetPlan(TerrainPlacementScope scope, ulong? seed)
        {
            var rules = Seal();
            if (!rules.Any(rule => (rule.Scope & scope) != 0 && (!rule.BodySeed.HasValue || rule.BodySeed == seed)))
                return null;
            var live = WorldTerrainResources.TerrainMaterialDatas;
            if (!live.IsCreated || live.Length == 0 || live.Length > 256) return null;
            bool ownsRegistered = TerrainMaterialRegistration.TryGetStockCount(out int registeredPrefix)
                && TerrainMaterialRegistration.OwnsTable(live);
            if (!inspectedMaterialPublication || !live.Equals(materialPublication) || ownsRegistered != ownsRegisteredPublication)
            {
                materialPublication = live;
                inspectedMaterialPublication = true;
                ownsRegisteredPublication = ownsRegistered;
                Plans.Clear();
                int stockCount = ownsRegistered ? registeredPrefix : live.Length;
                stockNames = ReadStockNames(stockCount);
            }
            if (stockNames == null) return null;

            ulong? planSeed = rules.Any(rule => (rule.Scope & scope) != 0 && rule.BodySeed == seed) ? seed : null;
            var key = (scope, planSeed);
            if (!Plans.TryGetValue(key, out var plan))
            {
                plan = TerrainPlacementPlan.Build(rules, scope, planSeed, ResolveMaterial, WarnOnce, radialBands,
                    authoredSurfaceBands);
                Plans.Add(key, plan);
            }
            return plan;
        }

        private static byte? ResolveMaterial(TerrainMaterialReference reference)
        {
            if (reference.Kind == TerrainMaterialReferenceKind.Registered)
                return ownsRegisteredPublication && TerrainMaterialRegistration.TryGetId(reference.Value, out byte registered)
                    ? registered : (byte?)null;
            return stockNames!.TryGetValue(reference.Value, out byte stock) ? stock : (byte?)null;
        }

        private static Dictionary<string, byte>? ReadStockNames(int stockRows)
        {
            var assets = Resources.FindObjectsOfTypeAll<WorldTerrainResources>()
                .Where(asset => asset.terrainTextures.Count == stockRows).ToArray();
            if (assets.Length != 1)
            {
                WarnOnce("Terrain placement is unavailable for an ambiguous or non-stock native material table.");
                return null;
            }
            return assets[0].terrainTextures.Select((row, id) => (row.ShaderName, Id: (byte)id))
                .Where(row => !string.IsNullOrWhiteSpace(row.ShaderName))
                .GroupBy(row => row.ShaderName, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Id, StringComparer.Ordinal);
        }

        private static void WarnOnce(string reason)
        {
            lock (Gate)
                if (!Reported.Add(reason)) return;
            report?.Invoke(reason);
        }
    }
}
