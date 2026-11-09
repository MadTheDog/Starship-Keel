using System;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.NavigationProbe
{
    public static class NavigationExamples
    {
        public static bool MarkSurveyBeacon(VoxelEntity beaconGrid, Vector3 antennaPosition, out IDisposable? marker)
            => NavigationMarkerProvider.TryAdd("example.navigation", "survey", "Survey beacon", beaconGrid, antennaPosition,
                new Color32(50, 200, 255, 255), true, out marker);

        public static bool SelectSalvageContact(ParentEntity wreck)
            => NavigationTargetService.TrySelectEntity(wreck);

        public static bool BindManualAssist(Spaceship ship, out VehicleControlLease? handle)
            => VehicleControlProvider.TryBind(ship, out handle);

        public static bool BeginLocalSurveyApproach(Spaceship ship, ParentEntity beacon, out IDisposable? handle)
            => WaypointFlightService.TryBeginApproach(ship, beacon, 500f, out handle);
    }
}
