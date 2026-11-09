using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.GridRecoveryProbe
{
    [BepInPlugin(Id, "Keel Grid Recovery Probe", "0.1.0")]
    [BepInDependency(Plugin.Id, "0.2.0")]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.grid-recovery";
        private const string Label = "KEEL_GRID_RECOVERY_PROBE";
        private enum Step { Off, Spawn, Damage, Defeat, Repair, Recover, RecoverAfterDefeat, Confirm, Cleanup, WaitForRemoval }
        private readonly Stopwatch clock = new Stopwatch();
        private Step step;
        private string session = "";
        private string character = "";
        private string key = "";
        private ParentEntity? grid;
        private VoxelEntityID entityId;
        private BrickInstanceData hull;
        private bool recovered;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_RECOVERY_PROBE") != "1") return;
            try
            {
                session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                var game = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_RECOVERY_PROBE_GAME");
                var company = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_RECOVERY_PROBE_COMPANY");
                var product = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_RECOVERY_PROBE_PRODUCT");
                character = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_RECOVERY_PROBE_CHARACTER") ?? "";
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && company != "Moonfire Entertainment" && product != "Starship EVO"
                    && Application.companyName == company && Application.productName == product
                    && string.Equals(Path.GetFullPath(game!).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "Use the exact isolated game and managed experimental profile.");
                Require(character.EndsWith("-" + session.Substring(0, 8), StringComparison.Ordinal),
                    "The disposable test character must end with the first eight session characters.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                MoveTo(Step.Spawn);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Update()
        {
            if (step == Step.Off) return;
            try { Advance(); }
            catch (Exception exception) { Fail(exception); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Advance()
        {
            if (clock.Elapsed > (step == Step.Spawn ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Fixture stage timed out.");
            if (!WorldReady()) return;

            if (step == Step.Spawn)
            {
                hull = TabHull.HULL_A.PreviewBrickData;
                Require(hull.brickId != 0, "The stock hull brick is unavailable.");
                hull.scale = 0;
                var player = Dependency.PlayerManager;
                var position = new WorldTransform(player.TransformData);
                position.systemPosition += new Vector3l(250f * Vector3.up);
                grid = player.NetworkSetup.ServerCreateEntity(hull, position, EntityType.spaceship);
                Require(grid != null, "Stock build did not return a grid.");
                key = grid!.SGUID ?? "";
                entityId = grid.ID;
                Require(key.Length != 0, "Stock build did not allocate a persistent grid ID.");
                MoveTo(Step.Damage);
                return;
            }

            if (step == Step.WaitForRemoval)
            {
                if (BlockEntityManager.instance.GetEntityBySGUID(key) != null || ReferenceEquals(entityId.Entity, grid)) return;
                if (recovered)
                {
                    Require(!GridRecoveryService.TryRecover(grid!), "A stale fixture reference accepted recovery.");
                    Logger.LogInfo($"{Label} PASS session={session} grid={key} stock defeat, below-threshold rejection, repair and recovery of the same grid, native cleanup and stale-reference rejection");
                }
                step = Step.Off;
                return;
            }

            if (!IsFixtureGrid())
            {
                if (step == Step.Cleanup && BlockEntityManager.instance.GetEntityBySGUID(key) == null)
                {
                    MoveTo(Step.WaitForRemoval);
                    return;
                }
                throw new InvalidOperationException("The fixture grid identity changed.");
            }
            var target = grid!;
            if (step == Step.Cleanup)
            {
                Require(IsSafeTarget(target), "Stock cleanup refused an occupied or attached fixture.");
                if (!target.WillBeUnspawned)
                    BlockEntityManager.instance.RemoveAndDestroy(target, removeFromLocationMapping: true);
                MoveTo(Step.WaitForRemoval);
                return;
            }

            if (!target.IsInitialized || target.BrickCount.totalBricks == 0) return;
            Require(!target.WillBeUnspawned && IsSafeTarget(target) && target.Type == EntityType.spaceship
                && target.BrickCount.totalBricks == 1 && target.Identity.IsPlayerOwned && target.CanRecoverFromWreck
                && (target.TransformData.systemPosition - Dependency.PlayerManager.TransformData.systemPosition).Magnitude >= 150f,
                "The target is not the isolated, player-owned one-brick fixture.");
            float current = target.Systems.HullPointPercent;
            float threshold = target.Systems.HullPointPercentRecoveryThreshold;
            Require(float.IsFinite(current) && float.IsFinite(threshold) && threshold > 0.02f && threshold <= 1f,
                "Native hull health or recovery threshold is invalid for this fixture.");

            switch (step)
            {
                case Step.Damage:
                    Require(!target.isDestroyed && current >= threshold && !GridRecoveryService.TryRecover(target),
                        "The healthy grid did not reject recovery.");
                    var brick = FindHull(target);
                    int maximum = brick.MaximumHealthPoint;
                    Require(maximum > 1 && brick.CurrentHealthPoint == maximum,
                        "The fixture hull is unavailable or was already damaged.");
                    int residual = Mathf.CeilToInt(maximum * 0.02f);
                    Require((float)residual / maximum < threshold, "Fixture hull granularity cannot reach the native threshold safely.");
                    target.Bricks.DamageBrick(brick.instanceId, maximum - residual, true, default);
                    MoveTo(Step.Defeat);
                    break;
                case Step.Defeat:
                    Require(current > 0f && current < threshold && FindHull(target).HealthFactor > 0f,
                        $"Native damage did not leave a surviving hull below threshold. hull={current:F3} threshold={threshold:F3}");
                    if (!target.isDestroyed) Dependency.PlayerManager.NetworkSetup.ServerDestroyEntity(target);
                    Require(target.isDestroyed, "Stock destruction did not defeat the fixture grid.");
                    Require(!GridRecoveryService.TryRecover(target) && target.isDestroyed,
                        "A defeated grid below the recovery threshold was accepted.");
                    Report($"below-threshold rejection PASS hull={current:F3} threshold={threshold:F3}");
                    MoveTo(Step.Repair);
                    break;
                case Step.Repair:
                    Require(target.isDestroyed, "The fixture recovered before stock repairs.");
                    target.RepairAll();
                    MoveTo(Step.Recover);
                    break;
                case Step.Recover:
                    Require(current >= threshold && FindHull(target).HealthFactor == 1f,
                        $"Stock repair did not restore the defeated hull. hull={current:F3} threshold={threshold:F3}");
                    if (!target.isDestroyed)
                    {
                        Report("stock repair also recovered the grid before the next safe callback");
                        Dependency.PlayerManager.NetworkSetup.ServerDestroyEntity(target);
                        Require(target.isDestroyed && IsFixtureGrid(),
                            "Stock authority did not defeat the same repaired grid for explicit recovery.");
                        MoveTo(Step.RecoverAfterDefeat);
                        break;
                    }
                    goto case Step.RecoverAfterDefeat;
                case Step.RecoverAfterDefeat:
                    Require(target.isDestroyed && current >= threshold,
                        "Stock defeat did not leave a settled eligible wreck.");
                    Require(GridRecoveryService.TryRecover(target) && !target.isDestroyed,
                        "Keel did not recover the repaired fixture through the native gate.");
                    MoveTo(Step.Confirm);
                    break;
                case Step.Confirm:
                    Require(!target.isDestroyed && !GridRecoveryService.TryRecover(target),
                        "The recovered grid failed to stay recovered or accepted a second recovery.");
                    recovered = true;
                    MoveTo(Step.Cleanup);
                    break;
            }
        }

        private BrickInstanceData FindHull(ParentEntity target)
        {
            int id = target.Bricks.GetBrickInstanceId(hull);
            Require(id > 0 && id < target.Bricks.brickIds.Length, "Fixture hull no longer resolves.");
            var live = target.Bricks.GetBrickData(id);
            Require(live.IsValid && live.brickId == hull.brickId && live.gridPosition.Equals(hull.gridPosition),
                "Fixture hull identity changed.");
            return live;
        }

        private bool IsFixtureGrid() => grid != null && key.Length != 0 && grid.SGUID == key
            && ReferenceEquals(entityId.Entity, grid) && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(key), grid);

        private static bool IsSafeTarget(ParentEntity target) => target.controlMode == BaseVehicle.VehicleControlMode.notControlled
            && !target.IsLocalPlayerParent && target.Rigidbody != null && !target.Rigidbody.isParented
            && target.directDockedChildrens.Count == 0 && target.Tree.ChildrensAndSelf.Count == 1
            && target.EntityCrew.crews.Count == 0 && target.EntityCrew.npcCharactersIds.Count == 0;

        private bool WorldReady()
        {
            var player = Dependency.PlayerManager;
            return GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && NetworkServer.active && NetworkServer.dontListen && player != null && player.IsLocal
                && SaveManager.CharacterName == character && BlockEntityManager.instance != null;
        }

        private void MoveTo(Step next)
        {
            step = next;
            clock.Restart();
            Report("stage=" + step);
        }

        private void Report(string text) => Logger.LogInfo($"{Label} session={session} {text}");

        private void Fail(Exception exception)
        {
            Logger.LogError($"{Label} FAIL session={session} stage={step} {exception.GetBaseException().Message}");
            recovered = false;
            if (step != Step.Cleanup && step != Step.WaitForRemoval && grid != null && key.Length != 0)
                MoveTo(Step.Cleanup);
            else step = Step.Off;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
