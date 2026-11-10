using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Manager.Save;
using Sevo.Framework.Sdk;
using Manager;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Stores mod-owned waypoints in the current managed solo character's namespace.</summary>
    public static class PersistentWaypoints
    {
        private static ProfileSession? session;
        internal static long Revision { get; private set; }
        public static bool IsAvailable => session != null;

        internal static void Enable(ProfileSession profile)
        {
            session = profile;
            Revision++;
        }

        internal static void Disable()
        {
            session = null;
            Revision++;
        }

        public static bool TryUpsert(PersistentWaypoint waypoint)
        {
            if (waypoint == null) throw new ArgumentNullException(nameof(waypoint));
            var profile = session;
            if (profile == null || !TryScope(out var character, out var seed, out _)) return false;
            using var gate = profile.AcquireWaypointStorage();
            var state = profile.ReadWaypoints();
            state.Upsert(character, seed, waypoint);
            profile.WriteWaypoints(state);
            Revision++;
            return true;
        }

        public static bool TryDelete(string ownerId, string localName)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            var profile = session;
            if (profile == null || !TryScope(out var character, out var seed, out _)) return false;
            using var gate = profile.AcquireWaypointStorage();
            var state = profile.ReadWaypoints();
            if (!state.Delete(character, seed, key)) return false;
            profile.WriteWaypoints(state);
            Revision++;
            return true;
        }

        public static IReadOnlyList<PersistentWaypoint> ReadCurrentSystem()
        {
            var profile = session;
            if (profile == null || !TryScope(out var character, out var seed, out var star))
                return Array.Empty<PersistentWaypoint>();
            using var gate = profile.AcquireWaypointStorage();
            var current = new List<PersistentWaypoint>();
            foreach (var waypoint in profile.ReadWaypoints().List(character, seed))
                if (waypoint.StarSeed == star) current.Add(waypoint);
            return current.AsReadOnly();
        }

        /// <summary>Resolves an anchor in the loaded system without substituting another body or grid.</summary>
        public static bool TryResolve(PersistentWaypoint waypoint, out Vector3l position)
        {
            position = default;
            return waypoint != null && session != null && TryScope(out _, out _, out var star)
                && star == waypoint.StarSeed && TryResolveNative(waypoint, out position);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryResolveNative(PersistentWaypoint waypoint, out Vector3l position)
        {
            position = default;
            if (waypoint.AnchorKind == WaypointAnchorKind.System)
            {
                position = new Vector3l(waypoint.SystemX, waypoint.SystemY, waypoint.SystemZ);
                return true;
            }

            int transform;
            if (waypoint.AnchorKind == WaypointAnchorKind.Body)
            {
                var body = LoadedRockyBody.Find(waypoint.StarSeed, waypoint.BodySeed);
                if (body == null || body.planetGenerationId == 0) return false;
                transform = body.transformId;
            }
            else
            {
                var grid = BlockEntityManager.instance.GetEntityBySGUID(waypoint.GridId);
                if (grid == null || grid.WillBeUnspawned || !string.Equals(grid.SGUID, waypoint.GridId, StringComparison.OrdinalIgnoreCase)
                    || !grid.SkyTransform.CurrentSystem.Equals(PlanetarySystem.CurrentStarData)
                    || !NavigationWorld.TryReadTransform(grid, out transform))
                    return false;
            }

            if (!SkyTransformData.datas.IsCreated || transform <= 0 || transform >= SkyTransformData.datas.Length)
                return false;
            var local = waypoint.LocalPosition;
            position = SkyTransformData.datas[transform].LocalToSystem(new Vector3(local.X, local.Y, local.Z));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryScope(out string character, out ulong seed, out ulong star)
        {
            character = "";
            seed = star = 0;
            if (session == null || !NavigationWorld.CanRead || PlanetarySystem.InSystemTransition || PlayerSave_mp.Load_Legacy
                || SaveManager.playerSave is not { seed: > 0 } save
                || string.IsNullOrWhiteSpace(SaveManager.CharacterName) || PlanetarySystem.CurrentStarData.seed == 0)
                return false;
            character = SaveManager.CharacterName;
            seed = save.seed;
            star = PlanetarySystem.CurrentStarData.seed;
            return true;
        }
    }
}
