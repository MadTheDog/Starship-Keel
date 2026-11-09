using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
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
        private static int mainThread;
        private static TerrainPlacementRule[]? declarations;
        private static NativeArray<TerrainMaterialData> materialPublication;
        private static bool inspectedMaterialPublication;
        private static bool ownsRegisteredPublication;
        private static Dictionary<string, byte>? stockNames;

        public static bool IsAvailable => report != null;

        public static void Register(string ownerId, string localName, TerrainPlacementScope scope,
            TerrainMaterialReference source, TerrainMaterialReference destination, ulong? seed = null)
        {
            var rule = new TerrainPlacementRule(ownerId + ":" + localName, scope, source, destination, seed);
            lock (Gate)
            {
                if (declarations != null) throw new InvalidOperationException("Terrain placement registration is closed for this process.");
                if (Rules.ContainsKey(rule.Key)) throw new InvalidOperationException("Duplicate terrain placement rule: " + rule.Key);
                Rules.Add(rule.Key, rule);
            }
        }

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
            if (ActiveOnMainThread() && Seal().Length > 0 && !__0.hasError)
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
                        var seed = Seal().Any(rule => (rule.Scope & scope) != 0 && rule.BodySeed.HasValue)
                            ? FindSeed(node) : null;
                        var plan = GetPlan(scope, seed);
                        if (plan?.HasChanges == true)
                        {
                            for (int index = 0; index < values.Length; index++)
                            {
                                var original = values[index];
                                var updated = Remap(original, plan);
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

        private static void AfterSynchronousReadback(AsteroidData __1, VoxelNodeData[] __2)
        {
            if (!ActiveOnMainThread() || __2.Length != VoxelCount) return;
            try
            {
                var plan = GetPlan(TerrainPlacementScope.Asteroids, __1.seed);
                if (plan?.HasChanges != true) return;
                for (int index = 0; index < __2.Length; index++)
                    __2[index] = Remap(__2[index], plan);
            }
            catch (Exception exception)
            {
                WarnOnce("Baked asteroid placement failed: " + exception.Message);
            }
        }

        private static VoxelNodeData Remap(VoxelNodeData value, TerrainPlacementPlan plan)
        {
            value.texId0 = plan.Map(value.texId0);
            value.texId1 = plan.Map(value.texId1);
            return value;
        }

        private static ulong? FindSeed(OctreeNodeData node)
        {
            if (node.planetGenerationId != 0)
            {
                foreach (var body in PlanetarySystem.SolarBodies)
                    if (body.planetGenerationId == node.planetGenerationId) return body.Seed;
                return null;
            }
            var asteroids = AsteroidResources.datas;
            return asteroids.IsCreated && node.asteroidId > 0 && node.asteroidId < asteroids.Length
                ? asteroids[node.asteroidId].seed
                : (ulong?)null;
        }

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
                plan = TerrainPlacementPlan.Build(rules, scope, planSeed, ResolveMaterial, WarnOnce);
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
