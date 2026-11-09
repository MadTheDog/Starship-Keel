using System;
using System.IO;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.GridAttachmentProbe
{
    public sealed partial class Probe
    {
        private enum FixtureStep { Off, SpawnMount, BuildLock, SpawnTarget, Acquire, Attached, Released, Cleanup, Done }
        private FixtureStep fixtureStep;
        private ParentEntity? mountGrid;
        private ParentEntity? targetGrid;
        private BrickInstanceData lockData;
        private float fixtureDeadline;
        private bool behaviorPassed;

        private bool PrepareNativeFixture()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_ATTACHMENT_FIXTURE") != "1") return false;
            try
            {
                string? game = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_ATTACHMENT_FIXTURE_GAME");
                string? company = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_ATTACHMENT_FIXTURE_COMPANY");
                string? product = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_ATTACHMENT_FIXTURE_PRODUCT");
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product)
                    && company != "Moonfire Entertainment" && product != "Starship EVO"
                    && Application.companyName == company && Application.productName == product
                    && string.Equals(Path.GetFullPath(game!).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "Fixture requires a matching isolated lab path, product and managed experimental profile.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                fixtureStep = FixtureStep.SpawnMount;
            }
            catch (Exception exception) { Fail(exception); }
            return true;
        }

        private void Update()
        {
            if (fixtureStep == FixtureStep.Off || fixtureStep == FixtureStep.Done) return;
            try
            {
                if (fixtureDeadline == 0f) fixtureDeadline = Time.unscaledTime + 120f;
                if (Time.unscaledTime > fixtureDeadline) throw new TimeoutException(fixtureStep.ToString());
                if (!WorldReady()) return;
                switch (fixtureStep)
                {
                    case FixtureStep.SpawnMount:
                        var player = Dependency.PlayerManager;
                        var position = new WorldTransform(player.TransformData);
                        position.systemPosition += new Vector3l(50f * Vector3.up);
                        var hull = TabHull.HULL_A.PreviewBrickData;
                        lockData = TabSystem.MAGLOCK.PreviewBrickData;
                        hull.gridLevel = lockData.gridLevel;
                        hull.scale = 0;
                        mountGrid = player.NetworkSetup.ServerCreateEntity(hull, position, EntityType.spaceship);
                        Require(mountGrid != null, "Native builder rejected the mount grid.");
                        MoveTo(FixtureStep.BuildLock);
                        break;
                    case FixtureStep.BuildLock:
                        if (!mountGrid!.IsInitialized) return;
                        lockData.gridPosition = new Vector3i(0, lockData.GridSizeUnit, 0);
                        var action = new VoxelActionData { entityID = mountGrid.ID, brickData = lockData, type = VoxelActionType.build };
                        Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(action, false, 0);
                        MoveTo(FixtureStep.SpawnTarget);
                        break;
                    case FixtureStep.SpawnTarget:
                        if (!TryGetMount(out var mount)) return;
                        var frame = mount.TransformData;
                        var center = frame.LocalToSystem(mount.Position + 3f * mount.LocalUp);
                        var targetHull = TabHull.HULL_A.PreviewBrickData;
                        targetHull.gridLevel = lockData.gridLevel;
                        targetHull.scale = 0;
                        targetGrid = Dependency.PlayerManager.NetworkSetup.ServerCreateEntity(targetHull,
                            new WorldTransform(center, frame.rotation), EntityType.spaceship);
                        Require(targetGrid != null && !ReferenceEquals(mountGrid, targetGrid), "Native builder rejected the target grid.");
                        MoveTo(FixtureStep.Acquire);
                        break;
                    case FixtureStep.Acquire:
                        if (!TryGetMount(out mount) || !targetGrid!.IsInitialized
                            || !GridAttachmentService.TryGetCandidate(mount, out var candidate)) return;
                        Require(ReferenceEquals(candidate, targetGrid), "The stock acquisition ray selected a different grid.");
                        Require(GridAttachmentService.TryRequestAttach(mount, out var recordedGridId)
                            && recordedGridId == targetGrid!.SGUID, "The native attach request did not record the fixture target.");
                        Require(!GridAttachmentService.TryRequestAttach(mount, out _), "A duplicate request was accepted.");
                        MoveTo(FixtureStep.Attached);
                        break;
                    case FixtureStep.Attached:
                        if (!TryGetMount(out mount) || !GridAttachmentService.TryGetAttachedGrid(mount, out var applied)) return;
                        Require(ReferenceEquals(applied, targetGrid) && targetGrid!.Rigidbody.isParented
                            && ReferenceEquals(targetGrid.Rigidbody.parentedOnMagLock, mount.Command),
                            "The target is missing its native parent or Mag-Lock backlink.");
                        if (!GridAttachmentService.TryRequestRelease(mount)) return;
                        MoveTo(FixtureStep.Released);
                        break;
                    case FixtureStep.Released:
                        if (!TryGetMount(out mount) || targetGrid!.Rigidbody.isParented) return;
                        Require(targetGrid.Rigidbody.parentedOnMagLock == null
                            && !GridAttachmentService.TryGetAttachedGrid(mount, out _), "Native release left a stale lock.");
                        behaviorPassed = true;
                        MoveTo(FixtureStep.Cleanup);
                        break;
                    case FixtureStep.Cleanup:
                        if (mountGrid != null && !mountGrid.WillBeUnspawned) Dependency.PlayerNetwork.ServerDestroyEntity(mountGrid);
                        if (targetGrid != null && !targetGrid.WillBeUnspawned) Dependency.PlayerNetwork.ServerDestroyEntity(targetGrid);
                        mountGrid = targetGrid = null;
                        fixtureStep = FixtureStep.Done;
                        if (behaviorPassed) Logger.LogInfo("KEEL_GRID_ATTACHMENT_PROBE PASS attach recorded and applied, release applied, cleanup requested, disk reload not run");
                        break;
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private bool TryGetMount(out BrickCoord placement)
        {
            placement = default;
            if (mountGrid == null || !mountGrid.IsInitialized) return false;
            int id = mountGrid.Bricks.GetBrickInstanceId(lockData);
            if (id <= 0 || id >= mountGrid.Bricks.brickIds.Length) return false;
            var data = mountGrid.Bricks.GetBrickData(id);
            if (!data.IsValid || data.brickId != lockData.brickId || !data.gridPosition.Equals(lockData.gridPosition)) return false;
            placement = new BrickCoord(mountGrid.ID, data);
            return placement.Command is SystemMagLock;
        }

        private static bool WorldReady()
        {
            var player = Dependency.PlayerManager;
            return GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && NetworkServer.active && NetworkServer.dontListen && player != null && player.IsLocal;
        }

        private void MoveTo(FixtureStep step, float seconds = 12f)
        {
            fixtureStep = step;
            fixtureDeadline = Time.unscaledTime + seconds;
            Logger.LogInfo($"KEEL_GRID_ATTACHMENT_PROBE stage={step}");
        }

        private void Fail(Exception exception)
        {
            Logger.LogError($"KEEL_GRID_ATTACHMENT_PROBE FAIL stage={fixtureStep} {exception.GetBaseException().Message}");
            behaviorPassed = false;
            if (fixtureStep != FixtureStep.Cleanup && (mountGrid != null || targetGrid != null))
                MoveTo(FixtureStep.Cleanup);
            else fixtureStep = FixtureStep.Done;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
