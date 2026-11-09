using System;
using System.Threading;
using Game;
using HarmonyLib;
using UI;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Requests one native local approach with a release boundary.</summary>
    public static class WaypointFlightService
    {
        private static FlightLease? active;
        private static bool enabled;

        public static bool TryBeginApproach(Spaceship ship, ParentEntity target, float releaseDistance,
            out IDisposable? handle)
        {
            handle = null;
            if (!float.IsFinite(releaseDistance) || releaseDistance < 100f)
                throw new ArgumentOutOfRangeException(nameof(releaseDistance), "Use a finite release distance of at least 100.");
            if (!enabled || !VehicleControlWorld.CanControl(ship) || ship.autoPilot == null)
                return false;
            if (active is { } previous && (previous.Ship == null || previous.IsDisposed))
                Release(previous);
            if (ship.autoPilot.autopilotEngaged || ship.autoPilot.LandingOrDockingRequested || active != null
                || ReferenceEquals(ship.entity, target) || !NavigationTargetService.TryReadTrackedTarget(target, out var data)
                || !VehicleAssistPolicy.ContinueApproach(ship.entity.DistanceFromRadius(data), releaseDistance))
                return false;
            ship.autoPilot.EngageAutoFlight(data);
            if (ship.autoPilot.mode != SpaceshipAutoPilot.Mode.autoFlight) return false;

            var lease = new FlightLease(ship, ship.autoPilot, target, data.transformId, releaseDistance);
            active = lease;
            handle = lease;
            return true;
        }

        internal static void Install(Harmony harmony)
        {
            var player = new[] { typeof(PlayerCommandManager) };
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Spaceship), nameof(Spaceship.Control), player),
                prefix: new HarmonyMethod(typeof(WaypointFlightService), nameof(BeforeControl)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Spaceship), nameof(Spaceship.OnPostExit), player),
                prefix: new HarmonyMethod(typeof(WaypointFlightService), nameof(BeforeExit)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SpaceshipAutoPilot), nameof(SpaceshipAutoPilot.EngageAutoFlight),
                new[] { typeof(TrackingData) }),
                postfix: new HarmonyMethod(typeof(WaypointFlightService), nameof(AfterNativeEngage)));
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            if (active is { } flight) Release(flight);
        }

        private static void BeforeControl(Spaceship __instance)
        {
            if (active is not { } flight || !ReferenceEquals(flight.Ship, __instance)) return;
            if (!ReferenceEquals(__instance.autoPilot, flight.Pilot) || flight.Pilot.mode != SpaceshipAutoPilot.Mode.autoFlight
                || flight.Pilot.LandingOrDockingRequested)
            {
                active = null;
                return;
            }
            if (flight.IsDisposed || !VehicleControlWorld.CanControl(__instance)
                || !NavigationTargetService.TryReadTrackedTarget(flight.Target, out var target)
                || target.transformId != flight.TransformId
                || !VehicleAssistPolicy.ContinueApproach(__instance.entity.DistanceFromRadius(target), flight.ReleaseDistance))
                Release(flight);
        }

        private static void BeforeExit(Spaceship __instance)
        {
            if (active is { } flight && ReferenceEquals(flight.Ship, __instance)) Release(flight);
        }

        private static void AfterNativeEngage(SpaceshipAutoPilot __instance)
        {
            // Another call to the native engagement path takes ownership from any existing Keel lease.
            if (active is { } flight && ReferenceEquals(flight.Pilot, __instance)) active = null;
        }

        private static void Release(FlightLease flight)
        {
            active = null;
            if (flight.Ship != null && ReferenceEquals(flight.Ship.autoPilot, flight.Pilot)
                && flight.Pilot.mode == SpaceshipAutoPilot.Mode.autoFlight && !flight.Pilot.LandingOrDockingRequested)
                flight.Pilot.CancelCurrentMode();
        }

        private sealed class FlightLease : IDisposable
        {
            private int disposed;

            internal FlightLease(Spaceship ship, SpaceshipAutoPilot pilot, ParentEntity target, int transformId, float releaseDistance)
            {
                Ship = ship;
                Pilot = pilot;
                Target = target;
                TransformId = transformId;
                ReleaseDistance = releaseDistance;
            }

            internal Spaceship Ship { get; }
            internal SpaceshipAutoPilot Pilot { get; }
            internal ParentEntity Target { get; }
            internal int TransformId { get; }
            internal float ReleaseDistance { get; }
            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        }
    }
}
