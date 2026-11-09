using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Game;
using HarmonyLib;
using Manager;
using Network;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>One expiring analog input source for a locally owned, unoccupied stock spaceship.</summary>
    public sealed class SublightControlLease : IDisposable
    {
        internal readonly ParentEntity Grid;
        internal readonly string Owner;
        internal double ExpiresAt;
        internal Vector3 Translation;
        internal Vector3 Rotation;
        internal bool HasInput;
        private int disposed;

        internal SublightControlLease(ParentEntity grid, string owner, double expiry)
        {
            Grid = grid;
            Owner = owner;
            ExpiresAt = expiry;
        }

        /// <summary>Refreshes native control axes in [-1, 1] on the game thread.</summary>
        public bool TrySubmit(Vector3 translation, Vector3 rotation)
            => SublightControlService.TrySubmit(this, translation, rotation);

        internal bool Disposed => Volatile.Read(ref disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    /// <summary>Queues standard sublight input without entering or taking ownership of a ship.</summary>
    public static class SublightControlService
    {
        private const double InputLifetime = 0.35;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? authority;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? kinematic;
        private static FieldInfo? postExitCommands;
        private static FieldInfo? enteringCommands;
        private static Action<string>? warn;
        private static SublightControlLease? active;
        private static bool registered;

        public static bool IsAvailable => warn != null && GridPhysicsStep.IsAvailable;

        /// <summary>Acquires the local player's last piloted ship after its stock exit routine ends.</summary>
        public static bool TryAcquire(string ownerId, ParentEntity grid, out SublightControlLease? lease)
        {
            lease = null;
            if (!Safe() || (active is { Disposed: false } current && current.ExpiresAt > Time.unscaledTimeAsDouble)
                || !TryResolveDrive(grid, out _))
                return false;
            ContentAllocator.ValidateKey(ownerId + ":sublight");
            active?.Dispose();
            active = lease = new SublightControlLease(grid, ownerId, Time.unscaledTimeAsDouble + InputLifetime);
            return true;
        }

        internal static void Enable(Action<string> log)
        {
            if (!GridPhysicsStep.IsAvailable) throw new InvalidOperationException("Grid physics is unavailable.");
            postExitCommands = typeof(NetworkedCommandActivator).GetField("commandPostExit", BindingFlags.NonPublic | BindingFlags.Instance);
            enteringCommands = typeof(NetworkedCommandActivator).GetField("commandToEnter", BindingFlags.NonPublic | BindingFlags.Instance);
            if (postExitCommands?.FieldType != typeof(List<EnterCommand>) || enteringCommands?.FieldType != typeof(List<BrickCoord>))
                throw new InvalidOperationException("Stock command queues changed.");
            authority = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("hasLocalAuthority");
            kinematic = AccessTools.StructFieldRefAccess<SkyRigidbody.Data, bool>("isKinematic");
            if (!registered)
            {
                GridPhysicsStep.Register("keel.runtime", "sublight", Tick);
                registered = true;
            }
            warn = log;
        }

        internal static void Disable()
        {
            active?.Dispose();
            active = null;
            warn = null;
        }

        internal static bool TrySubmit(SublightControlLease lease, Vector3 translation, Vector3 rotation)
        {
            if (!Safe() || !ReferenceEquals(active, lease) || lease.Disposed
                || lease.ExpiresAt <= Time.unscaledTimeAsDouble
                || !SublightInputPolicy.Axis(translation.x, translation.y, translation.z)
                || !SublightInputPolicy.Axis(rotation.x, rotation.y, rotation.z))
                return false;
            lease.Translation = translation;
            lease.Rotation = rotation;
            lease.HasInput = true;
            lease.ExpiresAt = Time.unscaledTimeAsDouble + InputLifetime;
            return true;
        }

        private static bool Safe() => IsAvailable && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
            && !PlayerState.InMenu;

        private static void Tick(GridPhysicsContext context)
        {
            var lease = active;
            if (lease == null) return;
            try
            {
                if (lease.Disposed || lease.ExpiresAt <= Time.unscaledTimeAsDouble || !GridPhysicsStep.CanApply(context)
                    || !TryResolveDrive(lease.Grid, out var drive))
                {
                    EndLease(lease);
                    return;
                }

                if (!lease.HasInput) return;
                drive!.UpdateShipTransform(new VehicleControlInput { translation = lease.Translation, orient = lease.Rotation });
                GridPhysicsStep.ConsumeForce();
            }
            catch (Exception exception)
            {
                EndLease(lease);
                warn?.Invoke("Stopped sublight lease for " + lease.Owner + ": " + exception.GetBaseException().Message);
            }
        }

        private static void EndLease(SublightControlLease lease)
        {
            lease.Dispose();
            if (ReferenceEquals(active, lease)) active = null;
        }

        private static bool TryResolveDrive(ParentEntity grid, out SpaceshipDrive? drive)
        {
            drive = null;
            var player = Dependency.PlayerManager;
            var network = Dependency.PlayerNetwork;
            if (grid == null || player == null || !player.IsLocal || network == null || network.netId == 0
                || !ReferenceEquals(player.LastSpaceship, grid) || !grid.IsInitialized || grid.WillBeUnspawned || grid.isDestroyed
                || grid.Type != EntityType.spaceship || string.IsNullOrEmpty(grid.SGUID) || BlockEntityManager.instance == null
                || !ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(grid.SGUID), grid)
                || grid.controlMode != BaseVehicle.VehicleControlMode.notControlled
                || grid.Sync == null || grid.Sync.instanceData.ownerPlayerNetID != network.netId || grid.Sync.instanceData.pilotId != 0
                || grid.Dock == null || grid.Dock.IsDockedOrLanded || grid.Dock.IsAutoDockingToPort
                || grid.Systems == null || grid.Systems.IsPowerDepleted
                || grid.Rigidbody is not { } body || body.isParented)
                return false;

            var handle = body.OwnSkyRigidbody;
            if (!handle.IsValid || handle.id <= 0 || !SkyRigidbody.datas.IsCreated || handle.id >= SkyRigidbody.datas.Length
                || handle.id >= SkyRigidbody.Entities.Count || !ReferenceEquals(SkyRigidbody.Entities[handle.id], grid))
                return false;
            var data = handle.Data;
            if (data.isForcedStatic || data.forceSleeping || data.transformId != grid.transformId
                || !float.IsFinite(data.mass) || data.mass <= 0f
                || authority == null || !authority(ref data) || kinematic == null || kinematic(ref data))
                return false;

            var ship = grid.GetComponent<Spaceship>();
            if (ship == null || ship.IsControlled || ship.CommandManager != null || ship.aiController != null
                || ship.autoPilot == null || ship.autoPilot.autopilotEngaged
                || ship.Drive == null || ship.Drive.Mode != SpaceshipDrive.DriveMode.standard
                || ship.computerBaseVehicle is not ComputerSpaceship core || core.IsRemoved)
                return false;
            var coord = core.brickCoord;
            if (!ReferenceEquals(coord.Parent, grid) || !ReferenceEquals(coord.Command, core) || coord.brickData.HealthFactor <= 0f
                || !ValidOrientation(ship.Orientation))
                return false;

            bool localPlayerRegistered = false;
            foreach (var controller in PlayerCommandManager.AllPlayers)
            {
                if (controller == null) continue;
                var activator = controller.CommandActivator;
                if (activator == null
                    || postExitCommands?.GetValue(activator) is not List<EnterCommand> exits
                    || enteringCommands?.GetValue(activator) is not List<BrickCoord> enters)
                    return false;
                localPlayerRegistered |= ReferenceEquals(controller, player);
                foreach (var command in exits)
                    if (command != null && ReferenceEquals(command.brickCoord.Parent, grid)) return false;
                foreach (var entry in enters)
                    if (ReferenceEquals(entry.Parent, grid)) return false;
            }
            if (!localPlayerRegistered) return false;

            drive = ship.Drive;
            return true;
        }

        private static bool ValidOrientation(Quaternion orientation)
        {
            float length = Quaternion.Dot(orientation, orientation);
            return float.IsFinite(length) && Mathf.Abs(length - 1f) <= 0.01f;
        }
    }

    internal static class SublightInputPolicy
    {
        internal static bool Axis(float x, float y, float z)
            => Math.Abs(x) <= 1f && Math.Abs(y) <= 1f && Math.Abs(z) <= 1f;
    }
}
