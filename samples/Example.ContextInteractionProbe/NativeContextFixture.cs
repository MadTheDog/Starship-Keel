#nullable enable
using System;
using System.IO;
using System.Linq;
using BepInEx;
using Game;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UI;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.ContextInteractionProbe
{
    public sealed partial class Probe
    {
        private enum Stage { WaitingForClearRay, Targeting, Opening, Closing, Done }
        private static Probe? currentFixture;
        private string session = "";
        private Stage fixtureStage;
        private float deadline;
        private float readyAt;
        private ParentEntity? cargoGrid;
        private BrickInstanceData cargoData;
        private ContextAction? previousAction;
        private int previousFrame;
        private int expectedStorage;
        private int dispatched;
        private bool observedStockUpdate;
        private bool relocated;
        private int relocationFrame;
        private Vector3l relocationPoint;
        private string readiness = "";
        private float nextReadinessLog;

        private void PrepareFixture()
        {
            const string flag = "SEVO_KEEL_CONTEXT_CARGO_FIXTURE";
            if (Environment.GetEnvironmentVariable(flag) != "1") return;
            try
            {
                session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? game = Environment.GetEnvironmentVariable(flag + "_GAME");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && Path.IsPathFullyQualified(game)
                    && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test"
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "The fixture requires the exact managed disposable connector lab.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_CONTEXT_ACTIONS") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_INVENTORY_VIEWS") == "1",
                    "Enable the context action and inventory view experimental capabilities.");
                currentFixture = this;
                Record("armed", "awaiting clear stock camera ray in disposable solo creative");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Update()
        {
            if (!ReferenceEquals(currentFixture, this) || fixtureStage == Stage.Done) return;
            try
            {
                if (deadline == 0f) deadline = Time.realtimeSinceStartup + 100f;
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(fixtureStage + " " + readiness);
                if (!observedStockUpdate && PlayerState.sessionMode == PlayerState.SessionMode.solo && !PlayerState.InLoadingScreen)
                    ReportReadiness("waiting for stock player actions");
                if (fixtureStage == Stage.Opening && UI_CommandGeneric.IsStorageActive.Value)
                {
                    Require(dispatched == 1 && UI_GenericStorage.ActiveStorageId == expectedStorage,
                        "Native cargo UI opened an unexpected storage ID.");
                    Record("native-ui-open", $"storage={expectedStorage}");
                    UI_CommandGeneric.CloseWindow();
                    fixtureStage = Stage.Closing;
                }
                else if (fixtureStage == Stage.Closing && !UI_CommandGeneric.IsStorageActive.Value && UI_GenericStorage.ActiveStorageId == 0)
                {
                    Record("pass", $"storage={expectedStorage};dispatches=1;closed=True");
                    fixtureStage = Stage.Done;
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void ObservePlayerActions(PlayerActions actions)
        {
            if (fixtureStage is not (Stage.WaitingForClearRay or Stage.Targeting)) return;
            try
            {
                var player = Dependency.PlayerManager;
                if (player == null || !player.IsLocal || !ReferenceEquals(actions, player.PlayerActions)) return;
                observedStockUpdate = true;
                string? blocked = GetReadinessBlock(player);
                if (blocked != null)
                {
                    ReportReadiness(blocked);
                    return;
                }

                if (fixtureStage == Stage.WaitingForClearRay)
                {
                    if (player!.TransformData.systemPosition.Magnitude < 100f
                        || GameIntroductionResources.instance?.Medbay?.inAnimation != false)
                    {
                        readyAt = 0f;
                        ReportReadiness("waiting for stock spawn and medbay release");
                        return;
                    }
                    if (readyAt == 0f) readyAt = Time.realtimeSinceStartup + 0.75f;
                    if (Time.realtimeSinceStartup < readyAt) return;
                    if (relocated && (Time.frameCount <= relocationFrame
                        || Vector3l.Distance(player.SkyTransform.SystemPosition, relocationPoint) > 2f))
                    {
                        ReportReadiness("waiting for camera after safe-site relocation");
                        return;
                    }
                    if (actions.hit.isHit && actions.hit.distance < 4f)
                    {
                        ReportReadiness(FormattableString.Invariant($"native camera ray blocked at {actions.hit.distance:F2}m"));
                        if (!relocated && TryFindClearSite(player, out relocationPoint))
                        {
                            relocated = true;
                            relocationFrame = Time.frameCount;
                            player.newSystemPosition = relocationPoint;
                            player.newPositionOffset = Vector3.zero;
                            player.SkyTransform.SetTransform(relocationPoint, player.TransformData.rotation);
                            Record("relocated", "disposable local player moved to a vacant nearby site");
                        }
                        return;
                    }
                    var ray = actions.ActionRay;
                    var target = ray.GetPoint(2.75f);
                    if (!float.IsFinite(target.x) || !float.IsFinite(target.y) || !float.IsFinite(target.z)) return;
                    cargoData = TabStorage.STORAGE_UNIT.PreviewBrickData;
                    cargoData.scale = 0;
                    var position = new WorldTransform(player!.TransformData);
                    var bounds = cargoData.GetAabb(useGeoBounds: true);
                    var localCenter = ((Vector3)bounds.Min + (Vector3)bounds.Max) * 0.5f;
                    position.systemPosition = PositionManager.ToSystemPosition(target - position.rotation * localCenter);
                    cargoGrid = player.NetworkSetup.ServerCreateEntity(cargoData, position, EntityType.spaceship);
                    Require(cargoGrid != null, "Native cargo placement was rejected.");
                    deadline = Time.realtimeSinceStartup + 20f;
                    fixtureStage = Stage.Targeting;
                    Record("native-build", "stock cargo placed on the clear camera ray");
                    return;
                }

                var hit = actions.hit;
                Require(cargoGrid != null && !cargoGrid.WillBeUnspawned && !cargoGrid.isDestroyed,
                    "The native cargo fixture left the active world before targeting.");
                if (!hit.isHitBrick || cargoGrid == null || !cargoGrid.IsInitialized || !ReferenceEquals(hit.Entity, cargoGrid)
                    || hit.brickData.brickId != cargoData.brickId || hit.brickData.gridPosition != cargoData.gridPosition
                    || ActionActivate.CommandOver.Value is not StorageCommandData stock || stock.StorageId <= 0
                    || !stock.isCargo || stock.brickCoord != hit.brickCoord)
                {
                    ReportReadiness(FormattableString.Invariant(
                        $"waiting for owned cargo ray and stock ActionActivate; hit={hit.isHitBrick};distance={hit.distance:F2}"));
                    return;
                }
                var action = ContextActionProvider.GetAvailableActions().SingleOrDefault(candidate => candidate.Key == Id + ":cargo");
                if (action == null) { ReportReadiness("stock cargo hit has no current Keel cargo action"); return; }
                Require(action.Context.Placement == hit.brickCoord && InventoryViewService.CanOpen(action.Context),
                    "The context action did not match the native ray target.");
                if (previousAction == null)
                {
                    previousAction = action;
                    previousFrame = Time.frameCount;
                    expectedStorage = stock.StorageId;
                    Record("native-target", $"storage={expectedStorage};frame={previousFrame}");
                    return;
                }
                if (Time.frameCount == previousFrame) return;
                Require(stock.StorageId == expectedStorage && action.Context.Placement == previousAction.Context.Placement,
                    "The native target changed before the stale-action check.");
                Require(!ContextActionProvider.TryInvoke(previousAction) && !InventoryViewService.TryOpen(previousAction.Context)
                    && dispatched == 0 && !UI_CommandGeneric.IsStorageActive.Value && UI_GenericStorage.ActiveStorageId == 0,
                    "A stale context action opened a window or reached its callback.");
                Record("stale-refused", $"storage={expectedStorage};oldFrame={previousFrame};newFrame={Time.frameCount}");
                fixtureStage = Stage.Opening;
                deadline = Time.realtimeSinceStartup + 8f;
                Require(ContextActionProvider.TryInvoke(action) && dispatched == 1,
                    "The fresh stock target did not dispatch its cargo action.");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private string? GetReadinessBlock(PlayerCommandManager player)
        {
            if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe || GameloopManager.jobStarted)
                return "native frame boundary busy";
            if (PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.gameMode != PlayerState.GameMode.creative
                || !NetworkServer.active || !NetworkServer.dontListen)
                return "waiting for isolated solo creative authority";
            if (SaveManager.CharacterName != "ConnectorProbe-" + session.Substring(0, 8))
                return "waiting for session-owned character";
            if (PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle || player.Health == null || player.Health.IsRespawning)
                return "waiting for stock loading or respawn";
            if (PlayerState.InMenu || UI_CommandGeneric.IsStorageActive.Value)
                return "waiting for stock menu close";
            if (PlayerState.BlockActivate || PlayerState.SystemEntered)
                return "waiting for stock command and activation release";
            return null;
        }

        private static bool TryFindClearSite(PlayerCommandManager player, out Vector3l point)
        {
            point = default;
            var system = player.SkyTransform.CurrentSystem;
            var origin = player.TransformData.systemPosition;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData)
                || origin.Equals(Vector3l.zero) || PlanetarySystem.SolarBodies.Count == 0)
                return false;
            foreach (var direction in new[] { Vector3.up, Vector3.right, Vector3.forward })
            {
                var candidate = origin + new Vector3l(direction * 250f);
                if (PlanetarySystem.SolarBodies.Exists(body => body.IsShipTooClose(candidate))) continue;
                bool occupied = false;
                foreach (var other in SkyRigidbody.Entities)
                    if (other != null && !other.WillBeUnspawned && other.SkyTransform.CurrentSystem.Equals(system)
                        && Vector3l.Distance(other.TransformData.systemPosition, candidate) < 25f + other.ParentRadius)
                    {
                        occupied = true;
                        break;
                    }
                if (occupied) continue;
                point = candidate;
                return true;
            }
            return false;
        }

        private void ReportReadiness(string detail)
        {
            if (readiness == detail) return;
            readiness = detail;
            float now = Time.realtimeSinceStartup;
            if (now < nextReadinessLog) return;
            nextReadinessLog = now + 0.5f;
            Record("readiness", detail);
        }

        private void RecordFixtureDispatch(ContextActionContext context, bool opened)
        {
            if (!ReferenceEquals(currentFixture, this) || fixtureStage != Stage.Opening
                || !ReferenceEquals(context.Placement.entity, cargoGrid)) return;
            Require(opened, "The fresh action did not reach the native cargo UI.");
            dispatched++;
        }

        private void Fail(Exception exception)
        {
            var stage = fixtureStage;
            fixtureStage = Stage.Done;
            Record("fail", stage + " " + exception.Message);
        }

        private void Record(string stage, string detail)
            => Logger.LogInfo($"KEEL_CONTEXT_CARGO|{session}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
