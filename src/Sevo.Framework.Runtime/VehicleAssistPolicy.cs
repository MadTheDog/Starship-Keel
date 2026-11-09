namespace Sevo.Framework.Runtime
{
    internal static class VehicleAssistPolicy
    {
        internal static bool IsAxis(float value) => float.IsFinite(value) && value >= -1f && value <= 1f;

        internal static bool CanApply(bool stockHasInput, int requestFrame, int currentFrame)
        {
            int age = unchecked(currentFrame - requestFrame);
            return !stockHasInput && age >= 0 && age <= 1;
        }

        internal static bool ContinueApproach(float distance, float stopDistance)
            => ClassifyApproachDistance(distance, stopDistance) == WaypointApproachOutcome.Running;

        internal static WaypointApproachOutcome ClassifyApproachDistance(float distance, float stopDistance)
        {
            if (!float.IsFinite(distance)) return WaypointApproachOutcome.Interrupted;
            return distance <= stopDistance ? WaypointApproachOutcome.ReachedReleaseBoundary : WaypointApproachOutcome.Running;
        }
    }
}
