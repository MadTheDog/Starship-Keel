using System;
using Game;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.SublightControlProbe
{
    public sealed partial class Probe
    {
        private static readonly Vector3 CombinedCameraOffset = new Vector3(0f, 8f, -18f);
        private enum CombinedStep { Acquire, Pulse, Restore, Reenter }
        private CombinedStep combinedStep;
        private IDisposable? combinedCamera;
        private Transform? combinedProxy;
        private bool combinedReentryPassed;
        private Transform? combinedAnchor;
        private VoxelEntity? combinedPhysicalParent;
        private Vector3l combinedPhysicalPosition;
        private Vector3l combinedTargetPosition;
        private float combinedPulseDeadline;
        private int combinedRestoreFrame = -1;

        private static bool CombinedCameraRequested
            => Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_COMBINED_CAMERA") == "1";

        private bool AdvanceCombinedCamera(Spaceship? ship, ParentEntity target, PlayerCommandManager player)
        {
            Require(ship != null && ReferenceEquals(player.LastSpaceship, target),
                "The combined view and drive must use the same stock last-piloted grid.");

            switch (combinedStep)
            {
                case CombinedStep.Acquire:
                    if (player.CommandActivator.IsInEnterCommand || player.IsSeated || player.IsExitingSeat
                        || !HasStockPlayerAnchor(player) || !SublightControlService.TryAcquire(Id, target, out lease))
                        return false;
                    if (!CameraViewService.TryFollow(target, CombinedCameraOffset, Quaternion.identity, out combinedCamera)
                        || combinedCamera == null)
                    {
                        Release();
                        return false;
                    }

                    combinedAnchor = player.CameraTransform.parent;
                    combinedProxy = combinedAnchor?.parent;
                    combinedPhysicalParent = player.LockedEntity;
                    combinedPhysicalPosition = player.SkyTransform.SystemPosition;
                    combinedTargetPosition = target.SkyTransform.SystemPosition;
                    forward = ship!.transform.rotation * ship.Orientation * Vector3.forward;
                    startVelocity = Vector3.Dot(target.Rigidbody.LinearVelocity, forward);
                    Require(float.IsFinite(startVelocity) && Mathf.Abs(startVelocity) < 1f,
                        "The combined fixture did not settle below 1 m/s before flight.");
                    VerifyCombinedView(target, player);
                    combinedPulseDeadline = Time.unscaledTime + 1.25f;
                    combinedStep = CombinedStep.Pulse;
                    Submit(new Vector3(0f, 0f, 0.35f));
                    Logger.LogInfo($"{Prefix} COMBINED_LEASES_STARTED session={session} grid={autoKey}");
                    break;

                case CombinedStep.Pulse:
                    Require(Time.unscaledTime <= combinedPulseDeadline, "Combined sublight pulse timed out.");
                    VerifyCombinedView(target, player);
                    measuredDelta = Vector3.Dot(target.Rigidbody.LinearVelocity, forward) - startVelocity;
                    float distance = ((Vector3)(target.SkyTransform.SystemPosition - combinedTargetPosition).ToFloat3()).magnitude;
                    Require(float.IsFinite(measuredDelta) && float.IsFinite(distance) && distance < 5f
                        && target.controlMode == BaseVehicle.VehicleControlMode.notControlled
                        && target.Sync.instanceData.pilotId == 0, "Combined pulse lost unoccupied native flight eligibility.");
                    if (measuredDelta < VelocityThreshold || distance < 0.12f)
                    {
                        Submit(new Vector3(0f, 0f, 0.35f));
                        return false;
                    }

                    var disposedDrive = lease;
                    Release();
                    ReleaseCombinedCamera();
                    Require(disposedDrive != null && !disposedDrive.TrySubmit(Vector3.zero, Vector3.zero),
                        "Disposed sublight lease still accepted input.");
                    Logger.LogInfo(FormattableString.Invariant(
                        $"{Prefix} COMBINED_LEASES_PASS session={session} grid={autoKey} velocityDelta={measuredDelta:F3}m/s travel={distance:F3}m"));
                    combinedStep = CombinedStep.Restore;
                    break;

                case CombinedStep.Restore:
                    if (!HasStockPlayerAnchor(player) || player.CommandActivator.IsInEnterCommand || player.IsSeated
                        || player.IsExitingSeat || target.Rigidbody.LinearVelocity.magnitude >= 1f)
                        return false;
                    if (combinedRestoreFrame < 0)
                    {
                        combinedRestoreFrame = Time.frameCount;
                        return false;
                    }
                    if (Time.frameCount == combinedRestoreFrame) return false;
                    Require(autoSeat.Command is Command seat && IsLiveCommand(target, autoSeat, seat)
                        && autoSeat.brickData.brickId == TabInterior.SEAT_PILOT.brickID,
                        "The original live pilot seat is unavailable for stock reentry.");
                    exitRequested = false;
                    player.CommandActivator.ServerEnter(autoSeat);
                    combinedStep = CombinedStep.Reenter;
                    break;

                case CombinedStep.Reenter:
                    if (target.controlMode != BaseVehicle.VehicleControlMode.localPlayer) return false;
                    Require(ship!.IsControlledByLocalPlayer && player.IsSeated
                        && ReferenceEquals(player.LockedEntity?.Parent, target) && ReferenceEquals(player.LastSpaceship, target)
                        && ReferenceEquals(player.CameraTransform.parent, combinedAnchor) && combinedAnchor != null
                        && combinedAnchor.parent != null && !ReferenceEquals(combinedAnchor.parent, combinedProxy),
                        "Stock reentry did not restore the original pilot and player view.");
                    Reject(target, "during stock reentry after both combined leases released");
                    combinedReentryPassed = true;
                    Logger.LogInfo($"{Prefix} COMBINED_REENTRY_PASS session={session} grid={autoKey} stockPilot=True stockView=True");
                    return true;
            }
            return false;
        }

        private void VerifyCombinedView(ParentEntity target, PlayerCommandManager player)
        {
            var anchor = player.CameraTransform.parent;
            Require(anchor != null && ReferenceEquals(anchor, combinedAnchor) && anchor.parent != player.transform
                && Vector3.Distance(anchor.position, target.TransformData.LocalToWorld(CombinedCameraOffset)) < 0.5f,
                "The leased camera did not follow the same moving grid.");
            Require(ReferenceEquals(player.LockedEntity, combinedPhysicalParent)
                && ((Vector3)(player.SkyTransform.SystemPosition - combinedPhysicalPosition).ToFloat3()).magnitude < 2f
                && !player.IsSeated && !player.CommandActivator.IsInEnterCommand,
                "The combined lease moved or reseated the physical player.");
        }

        private static bool HasStockPlayerAnchor(PlayerCommandManager player)
        {
            var anchor = player.CameraTransform.parent;
            return anchor != null && anchor.parent == player.transform && anchor.localPosition == Vector3.zero
                && Quaternion.Angle(anchor.localRotation, Quaternion.identity) < 0.001f;
        }

        private void ReleaseCombinedCamera()
        {
            combinedCamera?.Dispose();
            combinedCamera = null;
        }
    }
}
