using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers mod-owned native missions and their persistent type tags.</summary>
    public static class StationMissions
    {
        private static readonly SortedDictionary<string, Declaration> Declarations =
            new SortedDictionary<string, Declaration>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, string> Published = new Dictionary<Type, string>();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool sealedRegistration;
        private static int gameThread;
        private static Action<string>? warning;

        public static bool IsAvailable { get; private set; }

        public static void Register<TMission>(string ownerId, string localName,
            Func<ulong, string, StationType, bool> matchesStation) where TMission : MissionBase
        {
            string tag = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(tag);
            if (sealedRegistration) throw new InvalidOperationException("Mission registration is closed.");
            if (matchesStation == null) throw new ArgumentNullException(nameof(matchesStation));
            var type = typeof(TMission);
            var constructor = type.GetConstructor(new[] { typeof(BrickEntityPOIData), typeof(ulong) });
            if (!type.IsVisible || type.IsAbstract || constructor == null)
                throw new ArgumentException("A public concrete mission must provide (BrickEntityPOIData, ulong).");
            if (Declarations.Values.Any(declaration => declaration.Constructor.DeclaringType == type))
                throw new InvalidOperationException("The mission type is already registered.");
            Declarations.Add(tag, new Declaration(constructor, matchesStation));
        }

        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var spawn = AccessTools.DeclaredMethod(typeof(WorldEntities), "SpawnMission", new[] { typeof(EntityStation) });
            var load = AccessTools.DeclaredMethod(typeof(PlayerMissions), "LoadMissionSaveDatas", new[] { typeof(List<MissionSaveData>) });
            harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(StationMissions), nameof(AfterSpawn)));
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(StationMissions), nameof(PublishTags)));
            warning = reportWarning;
            gameThread = Environment.CurrentManagedThreadId;
            IsAvailable = true;
        }

        internal static void Disable()
        {
            IsAvailable = false;
            foreach (var entry in Published)
                if (MissionSaveData.TagByType.TryGetValue(entry.Key, out var tag) && tag == entry.Value)
                    MissionSaveData.TagByType.Remove(entry.Key);
            Published.Clear();
        }

        private static bool CanUse() => IsAvailable && Environment.CurrentManagedThreadId == gameThread
            && PlayerState.sessionMode == PlayerState.SessionMode.solo;

        private static void PublishTags()
        {
            if (!CanUse() || sealedRegistration) return;
            sealedRegistration = true;
            foreach (var entry in Declarations)
            {
                var type = entry.Value.Constructor.DeclaringType!;
                if (MissionSaveData.TagByType.ContainsKey(type) || MissionSaveData.TagByType.ContainsValue(entry.Key))
                {
                    warning?.Invoke($"Mission {entry.Key} skipped because its type or tag is occupied.");
                    continue;
                }
                MissionSaveData.TagByType.Add(type, entry.Key);
                Published.Add(type, entry.Key);
            }
        }

        private static void AfterSpawn(EntityStation __0)
        {
            if (!CanUse() || Declarations.Count == 0 || __0 == null || __0.entity == null) return;
            PublishTags();
            var station = __0.entity;
            if (string.IsNullOrEmpty(station.SGUID)) return;
            var system = PlanetarySystem.CurrentStarData;
            foreach (var entry in Declarations)
            {
                var declaration = entry.Value;
                var type = declaration.Constructor.DeclaringType!;
                if (!Published.ContainsKey(type) || __0.Missions.Any(mission => mission.GetType() == type)) continue;
                try
                {
                    if (!declaration.MatchesStation(system.seed, station.SGUID, __0.StationType)) continue;
                    ulong seed = station.Seed;
                    foreach (char character in entry.Key) seed = unchecked((seed ^ character) * 1099511628211UL);
                    var mission = (MissionBase)declaration.Constructor.Invoke(new object[] { new BrickEntityPOIData(station), seed });
                    if (!(mission.Weight > 0) || float.IsInfinity(mission.Weight)) continue;
                    __0.Missions.Add(mission);
                    try { mission.OnIsAddedToMissionBoard(system); }
                    catch
                    {
                        __0.Missions.Remove(mission);
                        throw;
                    }
                }
                catch (Exception exception)
                {
                    if (Reported.Add(entry.Key))
                        warning?.Invoke($"Mission {entry.Key} skipped: {exception.GetBaseException().Message}");
                }
            }
        }

        private sealed class Declaration
        {
            internal readonly ConstructorInfo Constructor;
            internal readonly Func<ulong, string, StationType, bool> MatchesStation;

            internal Declaration(ConstructorInfo constructor, Func<ulong, string, StationType, bool> matchesStation)
            {
                Constructor = constructor;
                MatchesStation = matchesStation;
            }
        }
    }
}
