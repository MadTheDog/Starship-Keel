using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Game;
using HarmonyLib;
using UI;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Leases optional analog assistance for the presently piloted ship.</summary>
    public static class VehicleControlProvider
    {
        private static VehicleControlLease? active;
        private static bool enabled;

        public static bool TryBind(Spaceship ship, out VehicleControlLease? handle)
        {
            handle = null;
            if (!enabled || !VehicleControlWorld.CanControl(ship)) return false;
            if (active != null && !active.IsDisposed && ReferenceEquals(active.Ship, ship)) return false;
            active?.Dispose();
            handle = new VehicleControlLease(ship);
            active = handle;
            return true;
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Spaceship), nameof(Spaceship.Control),
                new[] { typeof(PlayerCommandManager) }),
                transpiler: new HarmonyMethod(typeof(VehicleControlProvider), nameof(InjectAfterManualInput)));
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            active?.Dispose();
            active = null;
        }

        internal static bool Owns(VehicleControlLease lease) => enabled && ReferenceEquals(active, lease);

        private static IEnumerable<CodeInstruction> InjectAfterManualInput(IEnumerable<CodeInstruction> source)
        {
            var instructions = source.ToList();
            var manual = AccessTools.DeclaredMethod(typeof(VehicleControlInput), nameof(VehicleControlInput.FromPlayerInput));
            var inject = AccessTools.DeclaredMethod(typeof(VehicleControlProvider), nameof(Apply));
            int index = instructions.FindIndex(instruction => instruction.Calls(manual));
            if (index < 0 || instructions.Count(instruction => instruction.Calls(manual)) != 1 || inject == null)
                throw new InvalidOperationException("Native local spaceship input call changed.");
            instructions.Insert(index + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0));
            instructions.Insert(index + 2, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, inject));
            return instructions;
        }

        private static VehicleControlInput Apply(VehicleControlInput input, Spaceship ship)
        {
            var lease = active;
            if (!enabled || lease == null || !ReferenceEquals(lease.Ship, ship) || !VehicleControlWorld.CanControl(ship))
                return input;
            bool stockHasInput = Nonzero(input.translation) || Nonzero(input.orient)
                || input.boostKeyDown || input.lightCruiseKey || input.warpJumpKey;
            if (lease.TryRead(stockHasInput, out var translation, out var orientation))
            {
                input.translation = translation;
                input.orient = orientation;
            }
            return input;
        }

        private static bool Nonzero(Vector3 value) => value.x != 0f || value.y != 0f || value.z != 0f;
    }

    /// <summary>Renew axes each frame. Dispose to release assistance without changing native vehicle control.</summary>
    public sealed class VehicleControlLease : IDisposable
    {
        private int disposed;
        private bool hasRequest;
        private int frame;
        private Vector3 translation;
        private Vector3 orientation;
        internal Spaceship Ship { get; }
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

        internal VehicleControlLease(Spaceship ship) => Ship = ship;

        public bool TrySetAxes(Vector3 translation, Vector3 orientation)
        {
            if (IsDisposed || !VehicleControlProvider.Owns(this) || !VehicleControlWorld.CanControl(Ship))
                return false;
            if (!Valid(translation) || !Valid(orientation))
            {
                hasRequest = false;
                return false;
            }
            this.translation = translation;
            this.orientation = orientation;
            frame = Time.frameCount;
            hasRequest = true;
            return true;
        }

        internal bool TryRead(bool stockHasInput, out Vector3 move, out Vector3 rotate)
        {
            move = translation;
            rotate = orientation;
            return !IsDisposed && hasRequest && VehicleAssistPolicy.CanApply(stockHasInput, frame, Time.frameCount);
        }

        private static bool Valid(Vector3 vector)
            => VehicleAssistPolicy.IsAxis(vector.x) && VehicleAssistPolicy.IsAxis(vector.y) && VehicleAssistPolicy.IsAxis(vector.z);

        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    internal static class VehicleControlWorld
    {
        internal static bool CanControl(Spaceship ship)
            => NavigationWorld.CanRead && ship != null && ReferenceEquals(HUD_TargetManager.Spaceship, ship)
                && ship.entity != null && ship.IsControlledByLocalPlayer && !ship.entity.isDestroyed
                && NavigationWorld.TryReadTransform(ship.entity, out _) && ship.Drive != null
                && ship.Drive.Mode == SpaceshipDrive.DriveMode.standard && !ship.entity.Dock.IsDockedToShipyard
                && !ship.entity.Dock.IsDockedToPort && !PlayerState.InMenu && !PlayerPhotoMode.IsInFreeCamera
                && !HUD_TargetMenu.IsMenuOpened.Value;
    }
}
