using System;
using System.IO;
using BepInEx;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using UI;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.SublightControlProbe
{
    public sealed partial class Probe
    {
        private const float WaypointMaximumSpeed = 35f;
        private const float WaypointMinimumClearance = 105f;
        private const float WaypointBoundaryTravel = 6f;
        private float waypointReleaseDistance;
        private const string ContactBlueprint = "Exo-1 Ant";
        private enum WaypointStep { Spawn, Track, Dispose, Released, Approach, Brake }
        private bool waypointMode;
        private bool waypointPassed;
        private WaypointStep waypointStep;
        private ParentEntity? waypointGrid;
        private VoxelEntityID waypointId;
        private string waypointKey = "";
        private IDisposable? waypointLease;

        private bool AdvanceWaypoint(Spaceship? ship, ParentEntity source, PlayerCommandManager player)
        {
            Require(ship != null && ship.IsControlledByLocalPlayer && !source.Systems.IsPowerDepleted,
                "Waypoint fixture lost its powered stock pilot.");
            if (waypointStep == WaypointStep.Spawn)
            {
                SpawnContact(source, ship!, player);
                waypointStep = WaypointStep.Track;
                return false;
            }

            var contact = waypointGrid;
            if (contact == null || !ReferenceEquals(waypointId.Entity, contact) || contact.isDestroyed || contact.WillBeUnspawned)
                throw new InvalidOperationException("The exact waypoint contact was defeated, unloaded or replaced.");
            if (!contact.IsInitialized) return false;
            if (waypointKey.Length == 0) waypointKey = contact.SGUID ?? "";
            Require(waypointKey.Length != 0 && contact.SGUID == waypointKey && IsOwnedContact(contact, player)
                && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(waypointKey), contact), "Waypoint contact identity changed.");
            Require(ClearApproach(source, contact.TransformData.systemPosition, contact),
                "The waypoint approach corridor is occupied or outside the active star.");
            float speed = source.Rigidbody.LinearVelocity.magnitude;
            if (!TryReadContact(contact, out var tracking))
            {
                Require(waypointStep == WaypointStep.Track, "The native tracking contact became hidden or stale.");
                return false;
            }
            float distance = source.DistanceFromRadius(tracking);
            Require(float.IsFinite(distance) && distance > WaypointMinimumClearance,
                "Waypoint fixture came inside its safety buffer.");
            Require(float.IsFinite(speed) && speed < WaypointMaximumSpeed, FormattableString.Invariant(
                $"Waypoint fixture exceeded its {WaypointMaximumSpeed:R} m/s test limit at distance={distance:R}, speed={speed:R}."));

            switch (waypointStep)
            {
                case WaypointStep.Track:
                    if (!BeginWaypoint(ship!, contact, distance, 15f, speed)) return false;
                    waypointStep = WaypointStep.Dispose;
                    break;
                case WaypointStep.Dispose:
                    Require(Outcome() == WaypointApproachOutcome.Running && ship!.autoPilot.mode == SpaceshipAutoPilot.Mode.autoFlight,
                        "Native approach did not remain active before disposal.");
                    waypointLease!.Dispose();
                    waypointStep = WaypointStep.Released;
                    break;
                case WaypointStep.Released:
                    if (Outcome() == WaypointApproachOutcome.Running) return false;
                    Require(Outcome() == WaypointApproachOutcome.Released && !ship!.autoPilot.autopilotEngaged,
                        "Native approach did not clear after explicit disposal.");
                    if (speed > 4f) return false;
                    waypointLease = null;
                    Logger.LogInfo($"{Prefix} WAYPOINT_DISPOSED session={session} target={waypointKey}");
                    Require(BeginWaypoint(ship!, contact, distance, WaypointBoundaryTravel, speed),
                        "A fresh approach was rejected after disposal.");
                    waypointStep = WaypointStep.Approach;
                    break;
                case WaypointStep.Approach:
                    if (Outcome() == WaypointApproachOutcome.Running) return false;
                    Require(Outcome() == WaypointApproachOutcome.ReachedReleaseBoundary && !ship!.autoPilot.autopilotEngaged
                        && distance <= waypointReleaseDistance + 1f, "Stock approach did not release at the distance boundary.");
                    waypointLease!.Dispose();
                    waypointLease = null;
                    Logger.LogInfo(FormattableString.Invariant(
                        $"{Prefix} WAYPOINT_BOUNDARY session={session} target={waypointKey} distance={distance:F2}m speed={speed:F2}m/s"));
                    waypointStep = WaypointStep.Brake;
                    break;
                case WaypointStep.Brake:
                    if (speed >= 1f) return false;
                    waypointPassed = true;
                    return true;
            }
            return false;
        }

        private bool BeginWaypoint(Spaceship ship, ParentEntity target, float distance, float travel, float speed)
        {
            waypointReleaseDistance = distance - travel;
            Require(waypointReleaseDistance > WaypointMinimumClearance + 20f,
                "Waypoint fixture cannot reserve clearance beyond its release boundary.");
            if (!WaypointFlightService.TryBeginApproach(ship, target, waypointReleaseDistance, out waypointLease)) return false;
            Require(Outcome() == WaypointApproachOutcome.Running && ship.autoPilot.mode == SpaceshipAutoPilot.Mode.autoFlight,
                "Keel did not start stock auto-flight.");
            Logger.LogInfo(FormattableString.Invariant(
                $"{Prefix} WAYPOINT_STARTED session={session} target={waypointKey} distance={distance:F2}m release={waypointReleaseDistance:F2}m speed={speed:F2}m/s"));
            return true;
        }

        private WaypointApproachOutcome Outcome()
        {
            if (waypointLease == null || !WaypointFlightService.TryGetOutcome(waypointLease, out var outcome))
                throw new InvalidOperationException("Missing Keel approach outcome.");
            return outcome;
        }

        private void SpawnContact(ParentEntity source, Spaceship ship, PlayerCommandManager player)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "Starship EVO_Data", "StreamingAssets",
                "Default Blueprints", ContactBlueprint, ContactBlueprint + ".sevo"));
            Require(File.Exists(path), "The bundled passive blueprint is missing.");
            var meta = ParentEntity.GetMetaData(path);
            Require(meta.name == ContactBlueprint && meta.type == EntityMetaType.spaceship && !meta.isWorldWreck
                && !meta.includesDockedEntities && meta.dockedEntityCount == 0 && meta.boundSize.magnitude <= 10f,
                "The bundled waypoint contact is no longer compact and undocked.");
            var system = source.SkyTransform.CurrentSystem;
            var axis = ship.transform.rotation * ship.Orientation * Vector3.forward;
            var position = source.TransformData.systemPosition + new Vector3l(225f * axis);
            Require(ClearApproach(source, position, null), "No clear space ahead for the fixture contact.");
            var instance = new EntityInstanceData
            {
                entityName = autoName + " Target",
                blueprintFullPath = path,
                ownerPlayerNetID = player.NetworkSetup.netId,
                npcBehavior = NpcSpaceshipCommon.Behavior.playerControllable,
                entityType = EntityType.spaceship,
                isNewInstance = true,
                saveInWorld = false
            };
            waypointGrid = player.NetworkSetup.ServerLoadBlueprint(instance, new WorldTransform(position, Quaternion.identity), system);
            if (waypointGrid == null) throw new InvalidOperationException("Stock loader did not create the separate contact.");
            waypointId = waypointGrid.ID;
        }

        private static bool ClearApproach(ParentEntity source, Vector3l destination, ParentEntity? contact)
        {
            var system = source.SkyTransform.CurrentSystem;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData)) return false;
            var origin = source.TransformData.systemPosition;
            var segment = (Vector3)(destination - origin).ToFloat3();
            if (!float.IsFinite(segment.sqrMagnitude) || segment.sqrMagnitude < 1f) return false;
            foreach (var body in PlanetarySystem.SolarBodies)
                for (int i = 0; i <= 4; i++)
                    if (body.IsShipTooClose(origin + new Vector3l(segment * (i * 0.25f)))) return false;
            foreach (var other in SkyRigidbody.Entities)
            {
                if (other == null || ReferenceEquals(other, source) || ReferenceEquals(other, contact) || other.WillBeUnspawned
                    || !other.SkyTransform.CurrentSystem.Equals(system)) continue;
                var offset = (Vector3)(other.TransformData.systemPosition - origin).ToFloat3();
                float along = Mathf.Clamp01(Vector3.Dot(offset, segment) / segment.sqrMagnitude);
                if ((offset - along * segment).magnitude < 65f + other.ParentRadius) return false;
            }
            return true;
        }

        private static bool TryReadContact(ParentEntity target, out TrackingData tracking)
        {
            tracking = default;
            int id = target.TrackingDataId;
            if (!HUD_TrackingWidgetResources.datas.IsCreated || id <= 0 || id >= HUD_TrackingWidgetResources.datas.Length)
                return false;
            tracking = HUD_TrackingWidgetResources.datas[id];
            return tracking.IsValid && !tracking.IsHidden && tracking.IsEntity && ReferenceEquals(tracking.GetEntity(), target);
        }

        private bool IsOwnedContact(ParentEntity target, PlayerCommandManager player)
            => target.EntityName == autoName + " Target" && !target.saveInWorld && IsDetached(target)
                && target.Sync != null && target.Sync.instanceData.ownerPlayerNetID == player.NetworkSetup.netId
                && target.Sync.instanceData.pilotId == 0 && target.controlMode == BaseVehicle.VehicleControlMode.notControlled
                && target.EntityCrew.crews.Count == 0 && !target.IsLocalPlayerParent;

        private bool CleanupWaypoint(PlayerCommandManager player)
        {
            if (waypointLease != null)
            {
                waypointLease.Dispose();
                if (Outcome() == WaypointApproachOutcome.Running) return false;
                waypointLease = null;
            }
            var contact = waypointGrid;
            if (contact == null || !ReferenceEquals(waypointId.Entity, contact))
                return waypointKey.Length == 0 || BlockEntityManager.instance.GetEntityBySGUID(waypointKey) == null;
            if (!contact.IsInitialized) return false;
            Require(IsOwnedContact(contact, player), "Cleanup refused a changed or occupied waypoint contact.");
            if (!contact.WillBeUnspawned) BlockEntityManager.instance.RemoveAndDestroy(contact, removeFromLocationMapping: true);
            return false;
        }
    }
}
