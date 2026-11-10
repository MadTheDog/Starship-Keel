using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Manager.Save;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>A live binding to a stored terrain contact. The contact is revalidated before every suspension force.</summary>
    public sealed class TerrainAnchorAttachment
    {
        internal TerrainAnchorAttachment(string key, string gridId, ParentEntity grid, TerrainAnchorPose pose)
            => (Key, PersistentGridId, Grid, Pose) = (key, gridId, grid, pose);

        public string Key { get; }
        public string PersistentGridId { get; }
        public ParentEntity Grid { get; }
        public TerrainAnchorPose Pose { get; }

        public bool TryApplySuspension(in GridPhysicsContext context, Vector3 gridLocalPoint, Vector3 pointRelativeVelocity,
            float restLength, float stiffness, float damping)
            => TerrainAnchorAttachments.IsCurrent(this) && GridForceService.TryApplyTerrainSuspension(
                context, Grid, Pose, gridLocalPoint, pointRelativeVelocity, restLength, stiffness, damping);
    }

    /// <summary>Persists namespaced body-relative contacts for stock-saved player grids in managed solo profiles.</summary>
    public static class TerrainAnchorAttachments
    {
        private static readonly Dictionary<(string Character, ulong World, string Key), WeakReference<TerrainAnchorAttachment>> Live = new();
        private static ProfileSession? session;

        public static bool IsAvailable => session != null && TerrainAnchor.IsAvailable;
        internal static void Enable(ProfileSession profile) => session = profile;
        internal static void Disable()
        {
            session = null;
            Live.Clear();
        }

        public static bool TryAttach(string ownerId, string localName, ParentEntity grid, in TerrainAnchorPose pose,
            out TerrainAnchorAttachment? attachment)
        {
            attachment = null;
            var key = Key(ownerId, localName);
            if (!IsAvailable || !TryScope(out var character, out var seed, out var star)
                || pose.Body.StarSeed != star || !TryGrid(grid, out var gridId)
                || TerrainAnchor.Resolve(pose, out _, out _, grid) != TerrainAnchorResult.Resolved)
                return false;
            var profile = session!;
            using var gate = profile.AcquireAnchorStorage();
            var state = profile.ReadAnchorState();
            var record = new TerrainAnchorRecord(gridId, pose.Body,
                new System.Numerics.Vector3(pose.LocalPosition.x, pose.LocalPosition.y, pose.LocalPosition.z),
                new System.Numerics.Vector3(pose.LocalNormal.x, pose.LocalNormal.y, pose.LocalNormal.z));
            if (!state.TryAdd(character, seed, key, record)) return false;
            profile.WriteAnchorState(state);
            attachment = new TerrainAnchorAttachment(key, gridId, grid, pose);
            Live[(character, seed, key)] = new WeakReference<TerrainAnchorAttachment>(attachment);
            return true;
        }

        public static bool TryRestore(string ownerId, string localName, ParentEntity grid, out TerrainAnchorAttachment? attachment)
        {
            attachment = null;
            var key = Key(ownerId, localName);
            if (!IsAvailable || !TryScope(out var character, out var seed, out var star) || !TryGrid(grid, out var gridId))
                return false;
            var profile = session!;
            using var gate = profile.AcquireAnchorStorage();
            if (!profile.ReadAnchorState().TryGet(character, seed, key, out var record)
                || record.Body.StarSeed != star || !StringComparer.OrdinalIgnoreCase.Equals(record.Grid, gridId))
                return false;
            var pose = new TerrainAnchorPose(record.Body, new Vector3(record.Position.X, record.Position.Y, record.Position.Z),
                new Vector3(record.Normal.X, record.Normal.Y, record.Normal.Z));
            if (TerrainAnchor.Resolve(pose, out _, out _, grid) != TerrainAnchorResult.Resolved) return false;
            attachment = new TerrainAnchorAttachment(key, record.Grid, grid, pose);
            Live[(character, seed, key)] = new WeakReference<TerrainAnchorAttachment>(attachment);
            return true;
        }

        /// <summary>Releases a stored anchor even when its grid or contact is unloaded.</summary>
        public static bool TryRelease(string ownerId, string localName, string persistentGridId)
        {
            var key = Key(ownerId, localName);
            if (!IsAvailable || !TryScope(out var character, out var seed, out _)) return false;
            var profile = session!;
            using var gate = profile.AcquireAnchorStorage();
            var state = profile.ReadAnchorState();
            if (!state.Delete(character, seed, key, persistentGridId)) return false;
            profile.WriteAnchorState(state);
            Live.Remove((character, seed, key));
            return true;
        }

        internal static bool IsCurrent(TerrainAnchorAttachment attachment)
            => IsAvailable && TryScope(out var character, out var seed, out var star) && star == attachment.Pose.Body.StarSeed
                && Live.TryGetValue((character, seed, attachment.Key), out var lease)
                && lease.TryGetTarget(out var current) && ReferenceEquals(current, attachment)
                && TryGrid(attachment.Grid, out var gridId)
                && StringComparer.OrdinalIgnoreCase.Equals(attachment.PersistentGridId, gridId);

        private static string Key(string ownerId, string localName)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            return key;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryScope(out string character, out ulong seed, out ulong star)
        {
            character = "";
            seed = star = 0;
            if (!WorldSurfaceQuery.IsSafePhase || PlanetarySystem.InSystemTransition || PlayerSave_mp.Load_Legacy
                || SaveManager.playerSave is not { seed: > 0 } save
                || string.IsNullOrWhiteSpace(SaveManager.CharacterName) || PlanetarySystem.CurrentStarData.seed == 0)
                return false;
            character = SaveManager.CharacterName;
            seed = save.seed;
            star = PlanetarySystem.CurrentStarData.seed;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryGrid(ParentEntity grid, out string identity)
        {
            identity = "";
            var manager = BlockEntityManager.instance;
            if (grid == null || manager == null || !grid.IsInitialized || grid.WillBeUnspawned || !grid.saveInWorld
                || !grid.Identity.IsPlayerOwned || !WreckClaimJournal.IsIdentity(grid.SGUID)
                || !ReferenceEquals(manager.GetEntityBySGUID(grid.SGUID), grid)
                || !grid.SkyTransform.CurrentSystem.Equals(PlanetarySystem.CurrentStarData))
                return false;
            identity = grid.SGUID;
            return true;
        }
    }
}
