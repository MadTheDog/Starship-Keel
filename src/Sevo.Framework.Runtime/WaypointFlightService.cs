using System;
using System.Threading;
using Game;
using HarmonyLib;
using UI;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public enum WaypointApproachOutcome { Running, ReachedReleaseBoundary, Released, Interrupted }

    /// <summary>Requests one native local approach with a release boundary.</summary>
    public static class WaypointFlightService
    {
        private static FlightLease? active;
        private static bool enabled;

        /// <summary>Reports why a Keel approach ended. Reaching the boundary does not imply the ship is stopped.</summary>
        public static bool TryGetOutcome(IDisposable handle, out WaypointApproachOutcome outcome)
        {
            outcome = WaypointApproachOutcome.Interrupted;
            if (handle is not FlightLease lease) return false;
            outcome = lease.Outcome;
            return true;
        }

        public static bool TryBeginApproach(Spaceship ship, ParentEntity target, float releaseDistance,
            out IDisposable? handle)
        {
            handle = null;
            if (!float.IsFinite(releaseDistance) || releaseDistance < 100f)
                throw new ArgumentOutOfRangeException(nameof(releaseDistance), "Use a finite release distance of at least 100.");
            if (!enabled || !VehicleControlWorld.CanControl(ship) || ship.autoPilot == null)
                return false;
            if (active is { } previous && (previous.Ship == null || previous.IsDisposed))
                Release(previous, previous.IsDisposed ? WaypointApproachOutcome.Released : WaypointApproachOutcome.Interrupted);
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
            if (active is { } flight) Release(flight, WaypointApproachOutcome.Interrupted);
        }

        private static void BeforeControl(Spaceship __instance)
        {
            if (active is not { } flight || !ReferenceEquals(flight.Ship, __instance)) return;
            if (!ReferenceEquals(__instance.autoPilot, flight.Pilot) || flight.Pilot.mode != SpaceshipAutoPilot.Mode.autoFlight
                || flight.Pilot.LandingOrDockingRequested)
            {
                active = null;
                flight.Finish(WaypointApproachOutcome.Interrupted);
                return;
            }
            if (flight.IsDisposed)
            {
                Release(flight, WaypointApproachOutcome.Released);
                return;
            }
            if (!VehicleControlWorld.CanControl(__instance)
                || !NavigationTargetService.TryReadTrackedTarget(flight.Target, out var target)
                || target.transformId != flight.TransformId)
            {
                Release(flight, WaypointApproachOutcome.Interrupted);
                return;
            }
            var outcome = VehicleAssistPolicy.ClassifyApproachDistance(__instance.entity.DistanceFromRadius(target), flight.ReleaseDistance);
            if (outcome != WaypointApproachOutcome.Running) Release(flight, outcome);
        }

        private static void BeforeExit(Spaceship __instance)
        {
            if (active is { } flight && ReferenceEquals(flight.Ship, __instance))
                Release(flight, WaypointApproachOutcome.Interrupted);
        }

        private static void AfterNativeEngage(SpaceshipAutoPilot __instance)
        {
            // Another call to the native engagement path takes ownership from any existing Keel lease.
            if (active is { } flight && ReferenceEquals(flight.Pilot, __instance))
            {
                active = null;
                flight.Finish(WaypointApproachOutcome.Interrupted);
            }
        }

        private static void Release(FlightLease flight, WaypointApproachOutcome outcome)
        {
            active = null;
            flight.Finish(outcome);
            if (flight.Ship != null && ReferenceEquals(flight.Ship.autoPilot, flight.Pilot)
                && flight.Pilot.mode == SpaceshipAutoPilot.Mode.autoFlight && !flight.Pilot.LandingOrDockingRequested)
                flight.Pilot.CancelCurrentMode();
        }

        private sealed class FlightLease : IDisposable
        {
            private int disposed;
            private int outcome;

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
            internal WaypointApproachOutcome Outcome => (WaypointApproachOutcome)Volatile.Read(ref outcome);
            internal void Finish(WaypointApproachOutcome result) => Volatile.Write(ref outcome, (int)result);

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        }
    }
}
