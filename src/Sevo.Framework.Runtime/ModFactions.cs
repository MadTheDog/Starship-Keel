using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    /// <summary>Publishes mod-owned faction definitions to the native territory loader.</summary>
    public static class ModFactions
    {
        private static readonly SortedDictionary<string, NpcFactionData> Definitions =
            new SortedDictionary<string, NpcFactionData>(StringComparer.Ordinal);
        private static bool sealedRegistration;
        private static int gameThread;
        private static Action<string>? warning;

        public static bool IsAvailable { get; private set; }

        /// <summary>Returns the stable native faction SGUID. Keep the declared faction name and key stable.</summary>
        public static string Register(string ownerId, string localName, NpcFactionData definition)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (sealedRegistration) throw new InvalidOperationException("Faction registration is closed.");
            if (string.IsNullOrWhiteSpace(definition.factionName))
                throw new ArgumentException("A faction name is required.", nameof(definition));
            if (definition.factionType != FactionType.faction)
                throw new ArgumentException("The territory hook accepts standard factions.", nameof(definition));
            var territory = definition.territoryData;
            if (!ValidWeight(territory.core) || !ValidWeight(territory.arm) || !ValidWeight(territory.outerRim)
                || !ValidWeight(territory.outerFactor) || !ValidWeight(territory.spawnStrength))
                throw new ArgumentException("Territory weights must be finite and nonnegative.", nameof(definition));
            definition.sguid = key;
            definition.source = FactionSource.mod;
            Definitions.Add(key, definition);
            return definition.FullSGUID;
        }

        /// <summary>Adjusts local player reputation for a registered, active faction.</summary>
        public static bool TryAddReputation(string factionSguid, int points)
        {
            if (!CanUse() || points == 0 || points is < -10000 or > 10000
                || !Definitions.Values.Any(faction => faction.FullSGUID == factionSguid)
                || !FactionTerritoryMap.TryGetFactionBySGUID(factionSguid, out var faction)
                || faction.FullSGUID != factionSguid || Dependency.PlayerManager == null
                || Dependency.PlayerManager.Progression == null)
                return false;
            PlayerProgression.AddPoints(ReputationType.faction, points, 0, factionSguid);
            return true;
        }

        private static bool ValidWeight(float value) => value >= 0 && !float.IsInfinity(value);

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var load = AccessTools.DeclaredMethod(typeof(FactionTerritoryMap), nameof(FactionTerritoryMap.LoadFromDisk),
                new[] { typeof(string), typeof(ulong) });
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(ModFactions), nameof(BeforeLoad)),
                finalizer: new HarmonyMethod(typeof(ModFactions), nameof(AfterLoad)));
            warning = reportWarning;
            gameThread = Environment.CurrentManagedThreadId;
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static bool CanUse() => IsAvailable && Environment.CurrentManagedThreadId == gameThread
            && PlayerState.sessionMode == PlayerState.SessionMode.solo;

        private static void BeforeLoad(List<NpcFactionData> ___factionsFromMods, out HashSet<string>? __state)
        {
            __state = null;
            if (!CanUse() || Definitions.Count == 0) return;
            sealedRegistration = true;
            __state = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in Definitions.Values)
            {
                string id = definition.FullSGUID;
                if (___factionsFromMods.Any(existing => existing.FullSGUID == id))
                {
                    warning?.Invoke($"Faction {id} skipped because its identity is already registered.");
                    continue;
                }
                ___factionsFromMods.Add(definition);
                __state.Add(id);
            }
        }

        private static void AfterLoad(List<NpcFactionData> ___factionsFromMods, HashSet<string>? __state)
        {
            if (__state != null) ___factionsFromMods.RemoveAll(faction => __state.Contains(faction.FullSGUID));
        }
    }
}
