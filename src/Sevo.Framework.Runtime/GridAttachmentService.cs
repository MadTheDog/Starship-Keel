using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Requests stock Mag-Lock operations without altering native targeting, persistence or parenting.</summary>
    public static class GridAttachmentService
    {
        private const float NativeAcquisitionRange = 8f;
        private static readonly Dictionary<SystemMagLock, string?> Pending = new Dictionary<SystemMagLock, string?>();
        private static FieldInfo? dataField;
        private static FieldInfo? lockedField;
        private static Action<string>? report;
        private static SystemMagLock? requesting;
        private static ParentEntity? expectedGrid;

        /// <summary>Returns the live grid under the mount's stock acquisition ray before eligibility checks.</summary>
        public static bool TryGetCandidate(BrickCoord mount, out ParentEntity? candidate)
        {
            candidate = null;
            return TryResolveMount(mount, out var command) && TryFindCandidate(command!, out candidate);
        }

        /// <summary>Returns true if the native command recorded a request. Query the live attachment after synchronization.</summary>
        public static bool TryRequestAttach(BrickCoord mount, ParentEntity target)
        {
            if (!TryResolveMount(mount, out var command) || requesting != null || !IsLiveGrid(target)
                || target.Rigidbody.isParented || Pending.ContainsKey(command!)
                || ReadData(command!).isLockingEntity || Pending.Values.Contains(target.SGUID, StringComparer.Ordinal)
                || !TryFindCandidate(command!, out var candidate) || !ReferenceEquals(candidate, target))
                return false;
            return Request(command!, target, target.SGUID);
        }

        /// <summary>Requests a native release. The stock command clears its saved attachment on acceptance.</summary>
        public static bool TryRequestRelease(BrickCoord mount)
        {
            if (!TryResolveMount(mount, out var command) || requesting != null || Pending.ContainsKey(command!)
                || !ReadData(command!).isLockingEntity)
                return false;
            return Request(command!, null, null);
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

        internal static void Install(Harmony harmony, Action<string> log)
        {
            var type = typeof(SystemMagLock);
            var data = RequireField(type, "magLockData", typeof(SystemMagLock.MagLockData));
            var attached = RequireField(type, "entityLocked", typeof(ParentEntity));
            harmony.Patch(AccessTools.Method(type, "CanLock", new[] { typeof(ParentEntity) }),
                prefix: new HarmonyMethod(typeof(GridAttachmentService), nameof(BeforeCanLock)));
            harmony.Patch(AccessTools.Method(type, "SetLockedEntity", new[] { typeof(bool) }),
                postfix: new HarmonyMethod(typeof(GridAttachmentService), nameof(AfterNativeCompletion)));
            harmony.Patch(AccessTools.Method(type, nameof(SystemMagLock.OnDestroy), new[] { typeof(BrickUnspawnMode) }),
                postfix: new HarmonyMethod(typeof(GridAttachmentService), nameof(AfterNativeCompletion)));
            report = log;
            lockedField = attached;
            dataField = data;
        }

        internal static void Disable()
        {
            dataField = lockedField = null;
            requesting = null;
            expectedGrid = null;
            report = null;
            Pending.Clear();
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

        private static bool Request(SystemMagLock command, ParentEntity? target, string? sguid)
        {
            var player = Dependency.PlayerManager;
            if (ReferenceEquals(player, null) || !CommandCommon.IsLocalPlayer(player)) return false;
            Pending.Add(command, sguid);
            requesting = command;
            expectedGrid = target;
            bool recorded = false;
            try
            {
                command.Activate(false, player, target == null ? 0f : 1f);
                var state = ReadData(command);
                recorded = target == null ? !state.isLockingEntity
                    : state.isLockingEntity && string.Equals(state.entitySGUID, sguid, StringComparison.Ordinal);
                return recorded;
            }
            catch (Exception exception)
            {
                report?.Invoke("Native Mag-Lock request failed: " + exception.GetBaseException().Message);
                return false;
            }
            finally
            {
                requesting = null;
                expectedGrid = null;
                if (!recorded) Pending.Remove(command);
            }
        }

        private static SystemMagLock.MagLockData ReadData(SystemMagLock command)
            => (SystemMagLock.MagLockData)dataField!.GetValue(command)!;

        private static bool BeforeCanLock(SystemMagLock __instance, ParentEntity __0, ref bool __result)
        {
            if (!ReferenceEquals(requesting, __instance)
                || (ReferenceEquals(expectedGrid, __0) && IsLiveGrid(__0)))
                return true;
            __result = false;
            return false;
        }

        private static void AfterNativeCompletion(SystemMagLock __instance) => Pending.Remove(__instance);
    }
}
