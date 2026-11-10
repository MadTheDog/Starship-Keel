using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Extends the blueprint pool of native station patrols.</summary>
    public static class StationTraffic
    {
        private static readonly SortedDictionary<string, (string Faction, string Tag, bool Installed, Func<ParentEntity, bool> Matches)> Pools =
            new SortedDictionary<string, (string, string, bool, Func<ParentEntity, bool>)>(StringComparer.Ordinal);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static int gameThread;
        private static bool sealedRegistration;

        public static bool IsAvailable { get; private set; }

        public static void RegisterPatrolPool(string ownerId, string localName, string factionName, string blueprintTag,
            bool installed, Func<ParentEntity, bool> matchesStation)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (sealedRegistration) throw new InvalidOperationException("Station patrol registration is closed.");
            if (string.IsNullOrWhiteSpace(blueprintTag)) throw new ArgumentException("Supply a blueprint tag.", nameof(blueprintTag));
            Pools.Add(key, (factionName ?? "", blueprintTag, installed,
                matchesStation ?? throw new ArgumentNullException(nameof(matchesStation))));
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var spawn = AccessTools.DeclaredMethod(typeof(NpcSpacePolice), "OnNewStationGenerated");
            harmony.Patch(spawn, transpiler: new HarmonyMethod(typeof(StationTraffic), nameof(ReplacePoolCall)));
            warning = reportWarning;
            gameThread = Environment.CurrentManagedThreadId;
            IsAvailable = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static IEnumerable<CodeInstruction> ReplacePoolCall(IEnumerable<CodeInstruction> instructions)
        {
            var native = AccessTools.DeclaredMethod(typeof(NPCSpawnCommon), nameof(NPCSpawnCommon.GetBlueprints));
            var code = instructions.ToList();
            var call = code.Single(instruction => instruction.Calls(native));
            call.opcode = OpCodes.Ldarg_0;
            call.operand = null;
            code.Insert(code.IndexOf(call) + 1,
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(StationTraffic), nameof(ExtendPool))));
            return code;
        }

        private static List<MetaDataPath> ExtendPool(string faction, string tag, bool installed, ParentEntity station)
        {
            var stock = NPCSpawnCommon.GetBlueprints(faction, tag, installed);
            if (!IsAvailable || Pools.Count == 0 || Environment.CurrentManagedThreadId != gameThread
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || station == null)
                return stock;
            sealedRegistration = true;
            List<MetaDataPath>? extended = null;
            var paths = new HashSet<string>(stock.Select(entry => entry.blueprintFullName), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Pools)
            {
                try
                {
                    var pool = entry.Value;
                    if (!pool.Matches(station)) continue;
                    var candidates = NPCSpawnCommon.GetBlueprints(pool.Faction, pool.Tag, pool.Installed);
                    if (candidates.Count == 0) throw new InvalidOperationException("No eligible blueprint matches the tag.");
                    foreach (var candidate in candidates.OrderBy(value => value.blueprintFullName, StringComparer.Ordinal))
                    {
                        if (string.IsNullOrEmpty(candidate.blueprintFullName) || !paths.Add(candidate.blueprintFullName)) continue;
                        (extended ??= new List<MetaDataPath>(stock)).Add(candidate);
                    }
                }
                catch (Exception exception)
                {
                    if (Reported.Add(entry.Key)) warning?.Invoke($"Station patrol pool {entry.Key} skipped: {exception.Message}");
                }
            }
            return extended ?? stock;
        }
    }
}
