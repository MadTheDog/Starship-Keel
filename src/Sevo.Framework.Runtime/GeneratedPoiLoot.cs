using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Stocks registered items in newly spawned Keel points with a native storage unit.</summary>
    public static class GeneratedPoiLoot
    {
        private static readonly SortedDictionary<string, (string ItemKey, int Quantity, Func<ParentEntity, bool> Matches)> Entries =
            new SortedDictionary<string, (string, int, Func<ParentEntity, bool>)>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<ParentEntity, object> Scheduled = new ConditionalWeakTable<ParentEntity, object>();
        private static readonly object Marker = new object();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool sealedRegistration;
        private static Action<string>? warning;

        public static bool IsAvailable { get; private set; }

        public static void Register(string ownerId, string localName, string itemKey, int quantity,
            Func<ParentEntity, bool> matches)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            ContentAllocator.ValidateKey(itemKey);
            if (sealedRegistration) throw new InvalidOperationException("Generated loot registration is closed.");
            if (quantity <= 0 || !ItemInstanceCommon.IsValidStackQuantity(quantity))
                throw new ArgumentOutOfRangeException(nameof(quantity));
            Entries.Add(key, (itemKey, quantity, matches ?? throw new ArgumentNullException(nameof(matches))));
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var initialize = AccessTools.DeclaredMethod(typeof(ParentEntity), nameof(ParentEntity.SetInitialized));
            harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(GeneratedPoiLoot), nameof(BeforeInitialize)));
            warning = reportWarning;
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static void BeforeInitialize(ParentEntity __instance)
        {
            if (!IsAvailable || Entries.Count == 0 || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || !__instance.isNewInstance || __instance.SGUID == null
                || !(__instance.SGUID.StartsWith("keel-orbit-", StringComparison.Ordinal)
                    || __instance.SGUID.StartsWith("keel-surface-", StringComparison.Ordinal))
                || Scheduled.TryGetValue(__instance, out _))
                return;
            sealedRegistration = true;
            Scheduled.Add(__instance, Marker);
            __instance.OnEntityInitialized += Fill;
        }

        private static void Fill(ParentEntity entity)
        {
            entity.OnEntityInitialized -= Fill;
            if (!IsAvailable || entity == null || entity.WillBeUnspawned) return;
            var storages = entity.Systems.GetCommandsFor(CommandType.storageUnit)
                .Where(coord => coord.Command is ICommandDataStorage)
                .Select(coord => ((ICommandDataStorage)coord.Command).StorageId)
                .Distinct().OrderBy(id => id).ToArray();
            foreach (var entry in Entries)
            {
                try
                {
                    if (!entry.Value.Matches(entity)) continue;
                    if (!ItemRegistration.TryCreateItemData(entry.Value.ItemKey, entry.Value.Quantity, out var item))
                        throw new InvalidOperationException("The registered item is unavailable.");
                    if (storages.Length == 0 || !StorageCommon.InsertUnregisteredItemOrMerge(storages[0], item, StorageCommon.CONTEXT, 0))
                        throw new InvalidOperationException("The first native storage unit cannot accept the item.");
                    entity.SetDirty();
                }
                catch (Exception exception)
                {
                    if (Reported.Add(entry.Key)) warning?.Invoke($"POI loot {entry.Key} skipped: {exception.Message}");
                }
            }
        }
    }
}
