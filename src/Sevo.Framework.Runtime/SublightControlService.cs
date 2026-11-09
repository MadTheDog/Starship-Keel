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
    internal enum SublightControlMode { LastPiloted, Prepared, Unpiloted }

    /// <summary>One expiring analog input source for a locally owned, unoccupied stock spaceship.</summary>
    public sealed class SublightControlLease : IDisposable
    {
        internal readonly ParentEntity Grid;
        internal readonly string Owner;
        internal readonly SublightControlMode Mode;
        internal readonly UnpilotedSublightDrive? UnpilotedDrive;
        internal double ExpiresAt;
        internal Vector3 Translation;
        internal Vector3 Rotation;
        internal bool HasInput;
        private int disposed;

        internal SublightControlLease(ParentEntity grid, string owner, double expiry,
            SublightControlMode mode, UnpilotedSublightDrive? unpilotedDrive)
        {
            Grid = grid;
            Owner = owner;
            ExpiresAt = expiry;
            Mode = mode;
            UnpilotedDrive = unpilotedDrive;
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
        private const float RemoteShipRange = 250f;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? authority;
        private static AccessTools.StructFieldRef<SkyRigidbody.Data, bool>? kinematic;
        private static FieldInfo? postExitCommands;
        private static FieldInfo? enteringCommands;
        private static Action<string>? warn;
        private static SublightControlLease? active;
        private static bool registered;
        private static bool preparedShipsEnabled;
        private static bool unpilotedShipsEnabled;

        public static bool IsAvailable => warn != null && GridPhysicsStep.IsAvailable;
        public static bool PreparedShipsAvailable => IsAvailable && preparedShipsEnabled;
        public static bool UnpilotedShipsAvailable => IsAvailable && unpilotedShipsEnabled;

        /// <summary>Acquires the local player's last piloted ship after its stock exit routine ends.</summary>
        public static bool TryAcquire(string ownerId, ParentEntity grid, out SublightControlLease? lease)
            => TryAcquireCore(ownerId, grid, SublightControlMode.LastPiloted, out lease);

        /// <summary>Acquires a nearby owned ship whose stock flight controls remain initialized after piloting.</summary>
        public static bool TryAcquirePreparedShip(string ownerId, ParentEntity grid, out SublightControlLease? lease)
            => TryAcquireCore(ownerId, grid, SublightControlMode.Prepared, out lease);

        /// <summary>Acquires nearby owned stock flight controls without prior piloting.</summary>
        public static bool TryAcquireUnpilotedShip(string ownerId, ParentEntity grid, out SublightControlLease? lease)
            => TryAcquireCore(ownerId, grid, SublightControlMode.Unpiloted, out lease);

        private static bool TryAcquireCore(string ownerId, ParentEntity grid, SublightControlMode mode, out SublightControlLease? lease)
        {
            lease = null;
            if (!Safe() || (active is { Disposed: false } current && current.ExpiresAt > Time.unscaledTimeAsDouble)
                || !TryResolveShip(grid, mode, out var ship, out var core))
                return false;
            ContentAllocator.ValidateKey(ownerId + ":sublight");
            var unpilotedDrive = mode == SublightControlMode.Unpiloted
                ? new UnpilotedSublightDrive(ship!, grid.Rigidbody, core!) : null;
            active?.Dispose();
            active = lease = new SublightControlLease(grid, ownerId, Time.unscaledTimeAsDouble + InputLifetime, mode, unpilotedDrive);
            return true;
        }

        internal static void Enable(Action<string> log, bool enablePreparedShips, bool enableUnpilotedShips = false)
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
            preparedShipsEnabled = enablePreparedShips;
            unpilotedShipsEnabled = enableUnpilotedShips && UnpilotedSublightDrive.TryBind(log);
        }

        internal static void Disable()
        {
            active?.Dispose();
            active = null;
            preparedShipsEnabled = false;
            unpilotedShipsEnabled = false;
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
                    || !TryResolveShip(lease.Grid, lease.Mode, out var ship, out var core))
                {
                    EndLease(lease);
                    return;
                }

                if (!lease.HasInput) return;
                if (lease.UnpilotedDrive is { } unpiloted)
                {
                    if (!unpiloted.TryApply(ship!, core!, lease.Translation, lease.Rotation))
                    {
                        EndLease(lease);
                        return;
                    }
                }
                else ship!.Drive.UpdateShipTransform(new VehicleControlInput { translation = lease.Translation, orient = lease.Rotation });
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

        private static bool TryResolveShip(ParentEntity grid, SublightControlMode mode, out Spaceship? ship, out ComputerSpaceship? core)
        {
            ship = null;
            core = null;
            bool remote = mode != SublightControlMode.LastPiloted;
            var player = Dependency.PlayerManager;
            var network = Dependency.PlayerNetwork;
            if (grid == null || player == null || !player.IsLocal || network == null || network.netId == 0
                || (!remote && !ReferenceEquals(player.LastSpaceship, grid))
                || !grid.IsInitialized || grid.WillBeUnspawned || grid.isDestroyed
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

            ship = grid.GetComponent<Spaceship>();
            if (ship == null || !ReferenceEquals(ship.entity, grid) || ship.IsControlled || ship.CommandManager != null || ship.aiController != null
                || ship.autoPilot == null || ship.autoPilot.autopilotEngaged
                || ship.Drive == null || ship.Drive.Mode != SpaceshipDrive.DriveMode.standard)
                return false;

            if (mode == SublightControlMode.Unpiloted)
            {
                if (!UnpilotedShipsAvailable || ship.computerBaseVehicle != null || ship.ComputerSpaceship != null
                    || ship.spaceshipCores.Count != 1)
                    return false;
                core = ship.spaceshipCores[0];
            }
            else core = ship.computerBaseVehicle as ComputerSpaceship;
            if (core == null || core.IsRemoved)
                return false;
            var coord = core.brickCoord;
            if (!ReferenceEquals(coord.Parent, grid) || !ReferenceEquals(coord.Command, core) || coord.brickData.HealthFactor <= 0f
                || (mode != SublightControlMode.Unpiloted && !ValidOrientation(ship.Orientation)))
                return false;
            if (remote && !HasRemoteControls(player, grid, ship, core, mode)) return false;

            bool localPlayerRegistered = false;
            foreach (var controller in PlayerCommandManager.AllPlayers)
            {
                if (controller == null) continue;
                var activator = controller.CommandActivator;
                if (activator == null
                    || postExitCommands?.GetValue(activator) is not List<EnterCommand> exits
                    || enteringCommands?.GetValue(activator) is not List<BrickCoord> enters)
                    return false;
                if (ReferenceEquals(controller, player))
                {
                    localPlayerRegistered = true;
                    if (remote && (enters.Count != 0 || exits.Count != 0)) return false;
                }
                foreach (var command in exits)
                    if (command != null && ReferenceEquals(command.brickCoord.Parent, grid)) return false;
                foreach (var entry in enters)
                    if (ReferenceEquals(entry.Parent, grid)) return false;
            }
            return localPlayerRegistered;
        }

        private static bool HasRemoteControls(PlayerCommandManager player, ParentEntity grid, Spaceship ship,
            ComputerSpaceship core, SublightControlMode mode)
        {
            if (PlayerState.IsControllingVehicle || PlayerState.SystemEntered
                || player.IsSeated || player.IsExitingSeat || player.CommandActivator == null || player.CommandActivator.IsInEnterCommand
                || !grid.SkyTransform.CurrentSystem.Equals(player.SkyTransform.CurrentSystem)
                || core.CommandYoke is not { IsRemoved: false } yoke)
                return false;
            var yokeCoord = yoke.brickCoord;
            var offset = (grid.TransformData.systemPosition - player.TransformData.systemPosition).ToVector3();
            if (!(offset.sqrMagnitude <= RemoteShipRange * RemoteShipRange) || !ReferenceEquals(yokeCoord.Parent, grid)
                || !ReferenceEquals(yokeCoord.Command, yoke) || yokeCoord.brickData.HealthFactor <= 0f
                || !ValidOrientation(core.inputOrientation))
                return false;
            return mode == SublightControlMode.Prepared
                ? PreparedShipsAvailable && ReferenceEquals(ship.ComputerSpaceship, core)
                    && Mathf.Abs(Quaternion.Dot(ship.Orientation, core.inputOrientation)) >= 0.999f
                : Mathf.Abs(Quaternion.Dot(yokeCoord.brickData.Quaternion, core.inputOrientation)) >= 0.999f;
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
