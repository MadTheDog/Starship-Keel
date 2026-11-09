using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using Game;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.SublightControlProbe
{
    public sealed partial class Probe
    {
        private const string Hotwing = "V27 Hotwing";
        private enum AutoStep { Pending, Spawn, Load, Occupied, Exited, Acquire, Pulse, Cleanup, Removed, Done }
        private bool automatic;
        private AutoStep autoStep;
        private string autoCharacter = "";
        private string autoName = "";
        private string autoKey = "";
        private VoxelEntityID autoGridId;
        private BrickCoord autoSeat, autoYoke, autoCore;
        private bool entered;
        private bool exitRequested;
        private bool motionPassed;
        private bool returnedPlayer;
        private WorldTransform originalPlayerPose;
        private StarData originalPlayerSystem;
        private float measuredDelta;

        private bool PrepareAutomatic()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_AUTOMATIC") != "1") return false;
            autoCharacter = Environment.GetEnvironmentVariable("SEVO_KEEL_SUBLIGHT_PROBE_CHARACTER") ?? "";
            Require(autoCharacter.EndsWith("-" + session.Substring(0, 8), StringComparison.Ordinal),
                "The disposable character must end with the first eight session characters.");
            autoName = "Keel Sublight Probe " + session.Substring(0, 8);
            automatic = true;
            return true;
        }

        private void UpdateAutomatic()
        {
            if (autoStep == AutoStep.Done) return;
            if (autoStep == AutoStep.Pending) AdvanceTo(AutoStep.Spawn, 180f);
            try { AdvanceAutomatic(); }
            catch (Exception exception)
            {
                Release();
                motionPassed = false;
                Logger.LogError($"{Prefix} AUTO_FAIL session={session} stage={autoStep} {exception.GetBaseException().Message}");
                if (grid != null && autoStep < AutoStep.Cleanup) AdvanceTo(AutoStep.Cleanup);
                else autoStep = AutoStep.Done;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AdvanceAutomatic()
        {
            float now = Time.unscaledTime;
            if (now > deadline) throw new TimeoutException(autoStep.ToString());
            var player = Dependency.PlayerManager;
            if (!AutoWorldReady(player)) return;

            if (autoStep == AutoStep.Spawn)
            {
                if (player.CommandActivator == null || player.CommandActivator.IsInEnterCommand || player.IsSeated
                    || GameIntroductionResources.instance?.Medbay?.inAnimation != false) return;
                if (!TrySpawn(player)) return;
                AdvanceTo(AutoStep.Load, 30f);
                return;
            }
            if (autoStep == AutoStep.Removed)
            {
                if (ReferenceEquals(autoGridId.Entity, grid)
                    || (autoKey.Length != 0 && BlockEntityManager.instance.GetEntityBySGUID(autoKey) != null)) return;
                if (motionPassed)
                    Logger.LogInfo($"{Prefix} AUTO_PASS session={session} grid={autoKey} delta={measuredDelta:F3}m/s native enter, exit arbitration, sublight and removal");
                autoStep = AutoStep.Done;
                return;
            }
            var target = grid;
            if (target == null || !ReferenceEquals(autoGridId.Entity, target))
                throw new InvalidOperationException("The exact fixture grid was replaced.");
            if (autoStep == AutoStep.Cleanup)
            {
                if (entered && (target.controlMode == BaseVehicle.VehicleControlMode.localPlayer
                    || (player.IsSeated && ReferenceEquals(player.LockedEntity?.Parent, target))))
                {
                    if (!exitRequested) RequestExit(player);
                    return;
                }
                if (!target.IsInitialized) return;
                if (target.IsLocalPlayerParent)
                {
                    if (!returnedPlayer && !player.IsSeated && !player.CommandActivator.IsInEnterCommand
                        && player.SkyTransform.CurrentSystem.Equals(originalPlayerSystem))
                    {
                        player.SkyTransform.SetTransform(originalPlayerPose.systemPosition, originalPlayerPose.rotation);
                        returnedPlayer = true;
                    }
                    return;
                }
                Require(target.EntityName == autoName && !target.saveInWorld && IsDetached(target)
                    && target.controlMode == BaseVehicle.VehicleControlMode.notControlled && target.EntityCrew.crews.Count == 0,
                    "Cleanup refused a changed, occupied or attached grid.");
                if (!target.WillBeUnspawned)
                    BlockEntityManager.instance.RemoveAndDestroy(target, removeFromLocationMapping: true);
                AdvanceTo(AutoStep.Removed);
                return;
            }
            if (!target.IsInitialized) return;
            if (autoKey.Length == 0) autoKey = target.SGUID ?? "";
            Require(autoKey.Length != 0 && target.SGUID == autoKey && target.EntityName == autoName && !target.saveInWorld
                && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(autoKey), target) && !target.WillBeUnspawned
                && !target.isDestroyed && IsDetached(target)
                && target.Type == EntityType.spaceship && target.Sync != null
                && target.Sync.instanceData.ownerPlayerNetID == player.NetworkSetup.netId
                && (target.Sync.instanceData.pilotId == 0 || ((autoStep == AutoStep.Occupied || autoStep == AutoStep.Exited
                        || autoStep == AutoStep.Acquire)
                    && target.Sync.instanceData.pilotId == player.NetworkSetup.netId)),
                "Loaded fixture identity, ownership or attachment changed.");
            var ship = target.GetComponent<Spaceship>();
            switch (autoStep)
            {
                case AutoStep.Load:
                    if (ship == null || ship.spaceshipCores.Count == 0 || target.Systems.IsPowerDepleted) return;
                    Require(ClearSpawn(target.TransformData.systemPosition, target.SkyTransform.CurrentSystem, target),
                        "The fixture site became obstructed while the blueprint was loading.");
                    Require(!ship.IsControlled && ship.spaceshipCores.Count == 1
                        && target.EntityCrew.crews.Count == 0 && target.EntityCrew.npcCharactersIds.Count == 0
                        && !ReferenceEquals(player.LastSpaceship, target),
                        $"Fixture requires one free stock flight computer. controlled={ship.IsControlled}"
                        + $" cores={ship.spaceshipCores.Count} crew={target.EntityCrew.crews.Count}"
                        + $" npc={target.EntityCrew.npcCharactersIds.Count} lastShip={ReferenceEquals(player.LastSpaceship, target)}.");
                    var core = ship.spaceshipCores[0];
                    var yoke = core.CommandYoke;
                    var seat = yoke?.Seat;
                    Require(seat != null && yoke != null && !core.IsRemoved && !yoke.IsRemoved && !seat.IsRemoved,
                        "The stock blueprint did not resolve its linked pilot seat and yoke.");
                    autoCore = core.brickCoord;
                    autoYoke = yoke!.brickCoord;
                    autoSeat = seat!.brickCoord;
                    Require(IsLiveCommand(target, autoCore, core) && IsLiveCommand(target, autoYoke, yoke)
                        && IsLiveCommand(target, autoSeat, seat) && autoYoke.brickData.brickId == TabLogic.YOKE.brickID
                        && autoSeat.brickData.brickId == TabInterior.SEAT_PILOT.brickID, "Stock flight controls changed.");
                    Reject(target, "before automated stock piloting");
                    player.CommandActivator.ServerEnter(autoSeat);
                    entered = true;
                    AdvanceTo(AutoStep.Occupied);
                    break;
                case AutoStep.Occupied:
                    if (target.controlMode != BaseVehicle.VehicleControlMode.localPlayer) return;
                    Require(player.IsSeated && ReferenceEquals(player.LockedEntity?.Parent, target)
                        && ReferenceEquals(player.LastSpaceship, target), "Stock control did not seat the local pilot on this grid.");
                    Require(target.Systems.ThrustForward > 0f, "The initialized stock ship has no forward thrust.");
                    Reject(target, "during automated stock piloting");
                    RequestExit(player);
                    AdvanceTo(AutoStep.Exited);
                    break;
                case AutoStep.Exited:
                    if (target.controlMode != BaseVehicle.VehicleControlMode.notControlled) return;
                    Reject(target, "during automated stock post-exit control");
                    AdvanceTo(AutoStep.Acquire, 35f);
                    break;
                case AutoStep.Acquire:
                    if (player.CommandActivator.IsInEnterCommand || player.IsSeated || player.IsExitingSeat
                        || !SublightControlService.TryAcquire(Id, target, out lease)) return;
                    Require(ship != null, "Stock drive disappeared before the pulse.");
                    forward = ship!.transform.rotation * ship.Orientation * Vector3.forward;
                    startVelocity = Vector3.Dot(target.Rigidbody.LinearVelocity, forward);
                    Require(float.IsFinite(startVelocity) && Mathf.Abs(startVelocity) < 1f,
                        "The fixture did not settle below 1 m/s before flight.");
                    AdvanceTo(AutoStep.Pulse, 1.25f);
                    Submit(new Vector3(0f, 0f, 0.35f));
                    break;
                case AutoStep.Pulse:
                    measuredDelta = Vector3.Dot(target.Rigidbody.LinearVelocity, forward) - startVelocity;
                    Require(float.IsFinite(measuredDelta)
                        && target.controlMode == BaseVehicle.VehicleControlMode.notControlled, "Pulse lost native flight eligibility.");
                    if (measuredDelta < VelocityThreshold)
                    {
                        Submit(new Vector3(0f, 0f, 0.35f));
                        return;
                    }
                    Release();
                    motionPassed = true;
                    Logger.LogInfo(FormattableString.Invariant(
                        $"{Prefix} AUTO_MOTION session={session} delta={measuredDelta:F3}m/s nativeMode={target.controlMode}"));
                    AdvanceTo(AutoStep.Cleanup);
                    break;
            }
        }

        private bool TrySpawn(PlayerCommandManager player)
        {
            var path = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "Starship EVO_Data", "StreamingAssets",
                "Default Blueprints", Hotwing, Hotwing + ".sevo"));
            Require(File.Exists(path), "The bundled V27 Hotwing blueprint is missing.");
            var meta = ParentEntity.GetMetaData(path);
            Require(meta.name == Hotwing && meta.type == EntityMetaType.spaceship && meta.powerProduced > 0
                && !meta.isWorldWreck && !meta.includesDockedEntities && meta.dockedEntityCount == 0
                && float.IsFinite(meta.boundSize.sqrMagnitude) && meta.boundSize.magnitude <= 100f,
                "The bundled blueprint metadata is no longer a single compact powered spaceship.");
            var system = player.SkyTransform.CurrentSystem;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData) || PlanetarySystem.SolarBodies.Count == 0)
                return false;
            var origin = player.TransformData.systemPosition;
            foreach (var offset in new[] { Vector3.up, Vector3.right, Vector3.forward })
            {
                var position = origin + new Vector3l(250f * offset);
                if (!ClearSpawn(position, system)) continue;
                var instance = new EntityInstanceData
                {
                    entityName = autoName,
                    blueprintFullPath = path,
                    ownerPlayerNetID = player.NetworkSetup.netId,
                    npcBehavior = NpcSpaceshipCommon.Behavior.playerControllable,
                    entityType = EntityType.spaceship,
                    isNewInstance = true,
                    buildPermission = BuildPermission.canBeBuilt,
                    saveInWorld = false
                };
                Require(instance.ownerPlayerNetID != 0, "Local player has no stock network identity.");
                originalPlayerPose = new WorldTransform(player.TransformData);
                originalPlayerSystem = system;
                grid = player.NetworkSetup.ServerLoadBlueprint(instance, new WorldTransform(position, Quaternion.identity), system);
                Require(grid != null, "The stock loader did not allocate the fixture.");
                autoGridId = grid!.ID;
                return true;
            }
            return false;
        }

        private static bool ClearSpawn(Vector3l position, StarData system, ParentEntity? ignore = null)
        {
            foreach (var body in PlanetarySystem.SolarBodies)
                if (body.IsShipTooClose(position)) return false;
            foreach (var other in SkyRigidbody.Entities)
                if (other != null && !ReferenceEquals(other, ignore) && !other.WillBeUnspawned && other.SkyTransform.CurrentSystem.Equals(system)
                    && (other.TransformData.systemPosition - position).Magnitude < 120f + other.ParentRadius)
                    return false;
            return true;
        }

        private static bool IsLiveCommand(ParentEntity target, BrickCoord coord, Command command)
            => ReferenceEquals(coord.Parent, target) && ReferenceEquals(coord.Command, command) && coord.brickData.HealthFactor > 0f;

        private static bool IsDetached(ParentEntity target)
            => target.Rigidbody != null && !target.Rigidbody.isParented && target.directDockedChildrens.Count == 0
                && target.Dock != null && !target.Dock.IsDockedOrLanded && !target.Dock.IsAutoDockingToPort;

        private bool AutoWorldReady(PlayerCommandManager player)
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && !PlayerState.InMenu
                && !PlayerState.SystemEntered && NetworkServer.active && NetworkServer.dontListen
                && player != null && player.IsLocal && !player.Health.IsRespawning
                && SaveManager.CharacterName == autoCharacter && BlockEntityManager.instance != null && SublightControlService.IsAvailable;

        private void RequestExit(PlayerCommandManager player)
        {
            var activator = player.CommandActivator;
            activator.ServerExit(autoCore);
            activator.ServerExit(autoYoke);
            activator.ServerExit(autoSeat);
            exitRequested = true;
        }

        private void AdvanceTo(AutoStep next, float timeout = 15f)
        {
            autoStep = next;
            deadline = Time.unscaledTime + timeout;
            Logger.LogInfo($"{Prefix} session={session} autoStage={next}");
        }
    }
}
