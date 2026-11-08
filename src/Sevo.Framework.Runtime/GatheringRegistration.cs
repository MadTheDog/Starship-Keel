using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Adds registered item drops when selected world props break and emit native loot.</summary>
    public static class GatheringRegistration
    {
        private sealed class DropRule
        {
            public string OutputKey { get; }
            public int Quantity { get; }

            public DropRule(string outputKey, int quantity)
            {
                OutputKey = outputKey;
                Quantity = quantity;
            }
        }

        private static readonly object Gate = new object();
        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<(WorldItemType, int), List<DropRule>> Rules =
            new Dictionary<(WorldItemType, int), List<DropRule>>();
        private static readonly HashSet<string> MissingOutputs = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<(DropRule Rule, SkyTransformData Transform, Vector3 Normal, uint Seed, int Frame)> Pending =
            new Queue<(DropRule, SkyTransformData, Vector3, uint, int)>();
        private static Action<string>? warn;
        private static bool sealedCatalog;
        private static bool enabled;
        private static bool dispatching;

        public static bool IsAvailable => enabled && ItemRegistration.IsAvailable;

        /// <summary>Declare from the dependent plugin's Awake. Use the exact native source type and prefab ID.</summary>
        public static void RegisterDrop(string ownerId, string localName, WorldItemType sourceType, int sourcePrefabId,
            string outputItemKey, int quantity)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            ContentAllocator.ValidateKey(outputItemKey);
            if (sourceType == WorldItemType.brick || !ItemInstanceCommon.IsMineableWorldProp(sourceType))
                throw new ArgumentOutOfRangeException(nameof(sourceType));
            if (sourcePrefabId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourcePrefabId));
            if (quantity <= 0 || !ItemInstanceCommon.IsValidStackQuantity(quantity))
                throw new ArgumentOutOfRangeException(nameof(quantity));

            lock (Gate)
            {
                if (sealedCatalog || !Keys.Add(key))
                    throw new InvalidOperationException("Late or duplicate gathering registration: " + key);
                var source = (sourceType, sourcePrefabId);
                if (!Rules.TryGetValue(source, out var rules))
                {
                    rules = new List<DropRule>();
                    Rules.Add(source, rules);
                }
                rules.Add(new DropRule(outputItemKey, quantity));
            }
        }

        internal static void Install(Harmony harmony, Assembly game, Action<string> warning)
        {
            const BindingFlags targets = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo Target(string type, string name, params Type[] arguments) =>
                game.GetType(type, true)!.GetMethod(name, targets, null, arguments, null)
                ?? throw new InvalidOperationException("Missing live gathering hook: " + type + "." + name);
            HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(GatheringRegistration).GetMethod(name, targets));

            var drop = Target("Game.ItemWorldLogic", "DropItem", typeof(ItemInstanceData), typeof(ImpactData),
                typeof(PrefabData), typeof(ItemWorldLogic.ItemWorldInteractionData), typeof(int), typeof(uint),
                typeof(SkyTransformData));
            var dispatch = Target("Manager.GameloopManager", "ExecuteCommandsJob");
            harmony.Patch(drop, postfix: Hook(nameof(AfterNativeDrop)));
            harmony.Patch(dispatch, postfix: Hook(nameof(AfterCommands)));
            warn = warning;
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            Pending.Clear();
        }

        private static bool InSoloWorld() =>
            FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle;

        private static void AfterNativeDrop(ItemInstanceData __0, ImpactData __1, int __4, uint __5, SkyTransformData __6,
            bool __runOriginal)
        {
            if (!enabled || !__runOriginal || __4 <= 0 || __5 != 0 || dispatching)
                return;

            try
            {
                if (!GameloopManager.IsMainThread || !InSoloWorld())
                    return;
                lock (Gate)
                {
                    sealedCatalog = true;
                    if (!Rules.TryGetValue((__0.itemType, __0.prefabId), out var rules))
                        return;
                    foreach (var rule in rules)
                    {
                        if (Pending.Count == 256)
                            break;
                        Pending.Enqueue((rule, __6, __1.localNormal, __0.seed ^ __1.seed, Time.frameCount));
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private static void AfterCommands()
        {
            if (!enabled || dispatching)
                return;

            try
            {
                if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe)
                    return;
                if (!InSoloWorld())
                {
                    Pending.Clear();
                    return;
                }
                lock (Gate)
                    sealedCatalog = true;
                if (Pending.Count == 0)
                    return;

                dispatching = true;
                var frame = Time.frameCount;
                while (Pending.Count > 0)
                {
                    var entry = Pending.Dequeue();
                    if (unchecked((uint)(frame - entry.Frame)) > 1)
                        continue;
                    if (!ItemRegistration.TryCreateItemData(entry.Rule.OutputKey, entry.Rule.Quantity, out var item)
                        || !item.CanRenderInWorld())
                    {
                        if (MissingOutputs.Add(entry.Rule.OutputKey))
                            warn?.Invoke("Gathering output unavailable: " + entry.Rule.OutputKey);
                        continue;
                    }
                    item.seed = entry.Seed;
                    item.disposeTime = Time.timeSinceLevelLoad + 120f;
                    try
                    {
                        item.Register();
                        item.AllocateAllComponents(entry.Transform);
                        ItemInstanceData.ApplyImpulse(item, 2f, entry.Normal);
                    }
                    catch
                    {
                        if (item.id != 0)
                            item.ReleaseComponentsAndItemId(unlinkTerrain: false);
                        throw;
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            finally
            {
                dispatching = false;
            }
        }

        private static void Fail(Exception exception)
        {
            Disable();
            warn?.Invoke("Gathering drops disabled: " + exception);
        }
    }
}
