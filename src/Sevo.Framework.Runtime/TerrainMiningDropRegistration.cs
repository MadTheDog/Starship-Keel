using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Common.RNG;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using Unity.Mathematics;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Supplies a registered item for successful stock drops from a registered terrain material.</summary>
    public static class TerrainMiningDropRegistration
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Rule> Declarations = new Dictionary<string, Rule>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedUnavailable = new HashSet<string>(StringComparer.Ordinal);
        private static IReadOnlyDictionary<string, Rule>? rules;
        private static Action<string>? warning;

        public static bool IsAvailable { get; private set; }

        /// <summary>Declares a replacement for an admitted material and item by their complete namespaced keys.</summary>
        public static void Register(string ownerId, string localName, string materialKey, string itemKey, float quantity = 1f)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            ContentAllocator.ValidateKey(materialKey);
            ContentAllocator.ValidateKey(itemKey);
            if (quantity <= 0f || !ItemInstanceCommon.IsValidStackQuantity(quantity))
                throw new ArgumentOutOfRangeException(nameof(quantity));
            lock (Gate)
            {
                if (rules != null) throw new InvalidOperationException("Terrain mining drop declarations are closed for this process.");
                if (Declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate terrain mining drop declaration: " + key);
                Declarations.Add(key, new Rule(key, materialKey, itemKey, quantity));
            }
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var drop = AccessTools.DeclaredMethod(typeof(OctreeNodeResources), "CheckIfDropRock",
                new[] { typeof(int), typeof(ImpactData), typeof(VoxelNodeData), typeof(bool), typeof(OctreeNodeData), typeof(int3) })
                ?? throw new MissingMethodException("OctreeNodeResources.CheckIfDropRock");
            warning = reportWarning;
            harmony.Patch(drop, transpiler: new HarmonyMethod(typeof(TerrainMiningDropRegistration), nameof(BridgeStockSpawn))
                { priority = Priority.Last });
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static IReadOnlyDictionary<string, Rule> Seal()
        {
            lock (Gate)
            {
                if (rules != null) return rules;
                var result = new Dictionary<string, Rule>(StringComparer.Ordinal);
                foreach (var group in Declarations.Values.OrderBy(rule => rule.Key, StringComparer.Ordinal)
                    .GroupBy(rule => rule.MaterialKey, StringComparer.Ordinal))
                {
                    var candidates = group.ToArray();
                    if (candidates.Length == 1) result.Add(group.Key, candidates[0]);
                    else warning?.Invoke("Terrain mining retains stock drops for " + group.Key + " due to conflicting declarations: "
                        + string.Join(", ", candidates.Select(rule => rule.Key)));
                }
                return rules = result;
            }
        }

        private static IEnumerable<CodeInstruction> BridgeStockSpawn(IEnumerable<CodeInstruction> instructions)
        {
            var spawn = AccessTools.DeclaredMethod(typeof(ItemInstanceData), nameof(ItemInstanceData.SpawnRandomItem),
                new[] { typeof(ItemInstanceData).MakeByRefType(), typeof(Vector3l), typeof(float2), typeof(WorldPropCommon.WorldPropId[]) });
            var impulse = AccessTools.DeclaredMethod(typeof(ItemInstanceData), nameof(ItemInstanceData.ApplyImpulse));
            var bridge = AccessTools.DeclaredMethod(typeof(TerrainMiningDropRegistration), nameof(Spawn));
            var code = instructions.ToList();
            var call = code.SingleOrDefault(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, spawn));
            if (call == null || call.blocks.Count != 0)
                throw new InvalidOperationException("Terrain mining spawn is missing, ambiguous, or protected by a new exception boundary.");
            int index = code.IndexOf(call);
            int impulseAt = code.FindIndex(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, impulse));
            if (index < 4 || index + 2 >= code.Count || impulseAt <= index + 2 || code[index + 1].opcode != OpCodes.Pop
                || (code[index - 4].opcode != OpCodes.Ldloca && code[index - 4].opcode != OpCodes.Ldloca_S)
                || code[index - 4].operand is not LocalBuilder spawned || spawned.LocalType != typeof(ItemInstanceData)
                || !LoadsLocal(code[index + 2], spawned))
                throw new InvalidOperationException("The live terrain mining impulse no longer uses the native spawn output.");
            call.opcode = OpCodes.Ldarg_2;
            call.operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, bridge));
            return code;
        }

        private static bool LoadsLocal(CodeInstruction instruction, LocalBuilder expected)
        {
            if (instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S)
                return instruction.operand is LocalBuilder local && local.LocalIndex == expected.LocalIndex;
            return (instruction.opcode == OpCodes.Ldloc_0 && expected.LocalIndex == 0)
                || (instruction.opcode == OpCodes.Ldloc_1 && expected.LocalIndex == 1)
                || (instruction.opcode == OpCodes.Ldloc_2 && expected.LocalIndex == 2)
                || (instruction.opcode == OpCodes.Ldloc_3 && expected.LocalIndex == 3);
        }

        private static int Spawn(ref ItemInstanceData stock, Vector3l position, float2 scale, WorldPropCommon.WorldPropId[] prefabs,
            VoxelNodeData voxel)
        {
            if (IsAvailable && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
                && TerrainMaterialRegistration.TryGetKey(voxel.MainTexId, out var materialKey)
                && (rules ?? Seal()).TryGetValue(materialKey!, out var rule)
                && TrySpawnReplacement(rule, stock, position, out var replacement))
            {
                // The stock method reads this same local to apply its normal impulse.
                stock = replacement;
                return replacement.id;
            }
            return ItemInstanceData.SpawnRandomItem(ref stock, position, scale, prefabs);
        }

        private static bool TrySpawnReplacement(Rule rule, ItemInstanceData stock, Vector3l position, out ItemInstanceData replacement)
        {
            replacement = default;
            try
            {
                if (!ItemRegistration.TryCreateItemData(rule.ItemKey, rule.Quantity, out replacement) || !replacement.CanRenderInWorld())
                {
                    if (ReportedUnavailable.Add(rule.Key)) warning?.Invoke("Terrain mining retains the stock drop because item "
                        + rule.ItemKey + " is unavailable for " + rule.Key + ".");
                    return false;
                }
                replacement.seed = stock.seed;
                replacement.disposeTime = stock.disposeTime;
                var hash = new SkeetoHash(stock.seed);
                replacement.Register();
                replacement.AllocateAllComponents(new SkyTransformData
                {
                    systemPosition = position,
                    rotation = hash.GetRotation()
                });
                if (replacement.id == 0 || replacement.rigidbodyId == 0 || replacement.worldColliderId == 0 || replacement.transformId == 0)
                    throw new InvalidOperationException("The replacement item did not finish native component allocation.");
                return true;
            }
            catch (Exception exception)
            {
                Disable();
                try
                {
                    if (replacement.id != 0) replacement.ReleaseComponentsAndItemId(unlinkTerrain: false);
                }
                catch (Exception cleanup)
                {
                    throw new InvalidOperationException("Terrain mining replacement was disabled after native rollback failed.",
                        new AggregateException(exception, cleanup));
                }
                warning?.Invoke("Terrain mining replacements disabled. Restoring the stock drop after: " + exception.Message);
                return false;
            }
        }

        private sealed class Rule
        {
            internal Rule(string key, string materialKey, string itemKey, float quantity)
            {
                Key = key;
                MaterialKey = materialKey;
                ItemKey = itemKey;
                Quantity = quantity;
            }

            internal string Key { get; }
            internal string MaterialKey { get; }
            internal string ItemKey { get; }
            internal float Quantity { get; }
        }
    }
}
