using System;
using System.Reflection;
using Game;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Requests stock Mag-Lock operations without altering native targeting, persistence or parenting.</summary>
    public static class GridAttachmentService
    {
        private const float NativeAcquisitionRange = 8f;
        private static FieldInfo? dataField;
        private static FieldInfo? lockedField;
        private static Action<string>? report;
        private static bool requesting;
        private static SystemMagLock? pendingMount;
        private static string? pendingGridId;
        private static bool pendingRelease;

        /// <summary>Returns the live grid under the mount's stock acquisition ray before eligibility checks.</summary>
        public static bool TryGetCandidate(BrickCoord mount, out ParentEntity? candidate)
        {
            candidate = null;
            return TryResolveMount(mount, out var command) && TryFindCandidate(command!, out candidate);
        }

        /// <summary>Runs native acquisition and reports the recorded grid ID. The stock activation chooses the target.</summary>
        public static bool TryRequestAttach(BrickCoord mount, out string? recordedGridId)
        {
            recordedGridId = null;
            if (!TryResolveMount(mount, out var command) || requesting || HasPendingRequest()
                || ReadData(command!).isLockingEntity || lockedField!.GetValue(command) != null
                || !TryFindCandidate(command!, out var candidate) || candidate!.Rigidbody.isParented)
                return false;
            return Request(command!, false, out recordedGridId);
        }

        /// <summary>Requests a native release. The stock command clears its saved attachment on acceptance.</summary>
        public static bool TryRequestRelease(BrickCoord mount)
        {
            if (!TryResolveMount(mount, out var command) || requesting || !ReadData(command!).isLockingEntity
                || (HasPendingRequest() && (pendingRelease || !ReferenceEquals(pendingMount, command))))
                return false;
            return Request(command!, true, out _);
        }

        /// <summary>Returns the attached grid after the native lock has been applied.</summary>
        public static bool TryGetAttachedGrid(BrickCoord mount, out ParentEntity? target)
        {
            target = null;
            if (!TryResolveMount(mount, out var command) || lockedField!.GetValue(command) is not ParentEntity entity
                || !IsLiveGrid(entity) || !entity.Rigidbody.isParented
                || !ReferenceEquals(entity.Rigidbody.parentedOnMagLock, command))
                return false;
            target = entity;
            return true;
        }

        internal static void Enable(Action<string> log)
        {
            var type = typeof(SystemMagLock);
            var data = RequireField(type, "magLockData", typeof(SystemMagLock.MagLockData));
            lockedField = RequireField(type, "entityLocked", typeof(ParentEntity));
            dataField = data;
            report = log;
        }

        internal static void Disable()
        {
            dataField = lockedField = null;
            requesting = false;
            report = null;
            ClearPendingRequest();
        }

        private static FieldInfo RequireField(Type type, string name, Type fieldType)
        {
            var field = type.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.FieldType == fieldType ? field
                : throw new InvalidOperationException("The native Mag-Lock field changed: " + name);
        }

        private static bool TryResolveMount(BrickCoord mount, out SystemMagLock? command)
        {
            command = null;
            if (dataField == null || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle)
                return false;

            var entity = mount.entity;
            if (entity == null || entity.Parent == null || !IsLiveGrid(entity.Parent) || entity.Bricks == null
                || mount.brickData.instanceId <= 0 || mount.brickData.instanceId >= entity.Bricks.brickIds.Length)
                return false;

            var data = entity.Bricks.GetBrickData(mount.brickData.instanceId);
            if (!data.IsValid || data != mount.brickData || data.HealthFactor <= 0f
                || new BrickCoord(mount.entityID, data).Command is not SystemMagLock native
                || native.GetType() != typeof(SystemMagLock) || native.IsRemoved)
                return false;

            command = native;
            return true;
        }

        private static bool IsLiveGrid(ParentEntity entity)
            => entity != null && entity.IsInitialized && !entity.WillBeUnspawned && !entity.isDestroyed
                && entity.Rigidbody != null && !string.IsNullOrEmpty(entity.SGUID)
                && BlockEntityManager.instance != null
                && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(entity.SGUID), entity);

        private static bool TryFindCandidate(SystemMagLock command, out ParentEntity? candidate)
        {
            candidate = null;
            var placement = command.brickCoord;
            var ray = new Ray(placement.WorldPosition, placement.WorldUp);
            if (!VoxelPhysics.BuildItem.Raycast(ray, out var hit, NativeAcquisitionRange, placement.Parent)
                || hit.Entity == null || hit.Entity.Parent == null || !IsLiveGrid(hit.Entity.Parent))
                return false;
            candidate = hit.Entity.Parent;
            return true;
        }

        private static bool Request(SystemMagLock command, bool release, out string? recordedGridId)
        {
            recordedGridId = null;
            var player = Dependency.PlayerManager;
            if (player == null || !CommandCommon.IsLocalPlayer(player)) return false;

            var previousGridId = release ? ReadData(command).entitySGUID : null;
            requesting = true;
            try
            {
                command.Activate(false, player, release ? 0f : 1f);
                var state = ReadData(command);
                if (release ? state.isLockingEntity
                    : !state.isLockingEntity || string.IsNullOrEmpty(state.entitySGUID))
                    return false;
                if (!release) recordedGridId = state.entitySGUID;
                pendingMount = command;
                pendingGridId = release ? previousGridId : recordedGridId;
                pendingRelease = release;
                return true;
            }
            catch (Exception exception)
            {
                report?.Invoke("Native Mag-Lock request failed: " + exception.GetBaseException().Message);
                return false;
            }
            finally
            {
                requesting = false;
            }
        }

        private static bool HasPendingRequest()
        {
            var command = pendingMount;
            if (command == null) return false;
            if (TryResolveMount(command.brickCoord, out var live) && ReferenceEquals(live, command)
                && !string.IsNullOrEmpty(pendingGridId)
                && BlockEntityManager.instance.GetEntityBySGUID(pendingGridId) is ParentEntity target
                && IsLiveGrid(target))
            {
                bool applied = target.Rigidbody.isParented && ReferenceEquals(target.Rigidbody.parentedOnMagLock, command);
                var state = ReadData(command);
                if (pendingRelease ? applied && !state.isLockingEntity
                    : !applied && state.isLockingEntity && string.Equals(state.entitySGUID, pendingGridId, StringComparison.Ordinal))
                    return true;
            }
            ClearPendingRequest();
            return false;
        }

        private static void ClearPendingRequest()
        {
            pendingMount = null;
            pendingGridId = null;
            pendingRelease = false;
        }

        private static SystemMagLock.MagLockData ReadData(SystemMagLock command)
            => (SystemMagLock.MagLockData)dataField!.GetValue(command)!;
    }
}
