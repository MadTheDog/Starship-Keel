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
    }
}
