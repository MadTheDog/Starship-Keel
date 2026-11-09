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

namespace Sevo.Example.GridDestructionProbe
{
    [BepInPlugin(Id, "Keel Grid Destruction Probe", "0.1.0")]
    [BepInDependency(Plugin.Id, "0.2.0")]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.grid-destruction";
        private const string Label = "KEEL_GRID_DESTRUCTION_PROBE";
        private enum Step { Off, Spawn, Destroy, Cleanup, WaitForRemoval }
        private readonly Stopwatch clock = new Stopwatch();
        private Step step;
        private string character = "";
        private string gridKey = "";
        private ParentEntity? grid;
        private VoxelEntityID gridId;
        private GridDestructionNotice lastNotice;
        private int notices;
        private bool observed;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_DESTRUCTION_PROBE") != "1") return;
            try
            {
                var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                var game = Environment.GetEnvironmentVariable("SEVO_KEEL_GRID_DESTRUCTION_PROBE_GAME");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Application.companyName == "SEVO Keel Gen Lab 0110" && Application.productName == "SEVO WG Test"
                    && string.Equals(Path.GetFullPath(game!).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "Expected the matching disposable game and managed experimental profile.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                character = "FluidProbe-" + session.Substring(0, 8);
                GridDestruction.RegisterObserver(Id, "verify", notice =>
                {
                    if (gridKey.Length == 0 || notice.GridKey != gridKey) return;
                    lastNotice = notice;
                    notices++;
                });
                step = Step.Spawn;
                clock.Start();
                Logger.LogInfo($"{Label} stage=waiting-for-disposable-solo");
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
            if (step == Step.WaitForRemoval)
            {
                Require(BlockEntityManager.instance != null && !PlayerState.IsQuittingToDesktopOrTitle
                    && SaveManager.CharacterName == character, "The fixture world changed before removal could be verified.");
                if (IsFixtureGrid()) return;
                step = Step.Off;
                if (observed)
                {
                    Require(notices == 1, "Stock cleanup delivered another destruction notice.");
                    Logger.LogInfo($"{Label} PASS one native destruction notice grid={gridKey} star={lastNotice.StarSeed}; repeat and cleanup delivered none");
                }
                return;
            }

            var player = Dependency.PlayerManager;
            if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe || GameloopManager.jobStarted
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.gameMode != PlayerState.GameMode.creative
                || !NetworkServer.active || !NetworkServer.dontListen || player == null || !player.IsLocal
                || SaveManager.CharacterName != character || BlockEntityManager.instance == null)
                return;

            switch (step)
            {
                case Step.Spawn:
                    Require(GridDestruction.IsAvailable, "The grid destruction hook was unavailable.");
                    var hull = TabHull.HULL_A.PreviewBrickData;
                    Require(hull.brickId != 0, "The stock hull brick was unavailable.");
                    hull.scale = 0;
                    var position = new WorldTransform(player.TransformData);
                    position.systemPosition += new Vector3l(250f * Vector3.up);
                    grid = player.NetworkSetup.ServerCreateEntity(hull, position, EntityType.spaceship);
                    Require(grid != null, "Stock spawn returned no grid.");
                    gridKey = grid!.SGUID ?? "";
                    gridId = grid.ID;
                    Require(gridKey.Length != 0, "Stock spawn returned no SGUID.");
                    MoveTo(Step.Destroy);
                    break;

                case Step.Destroy:
                    Require(IsFixtureGrid() && !grid!.WillBeUnspawned, "The fixture grid identity changed.");
                    if (!grid!.IsInitialized || grid.BrickCount.totalBricks == 0) return;
                    var distance = (grid.TransformData.systemPosition - player.TransformData.systemPosition).Magnitude;
                    Require(distance >= 150f && grid.Type == EntityType.spaceship && grid.BrickCount.totalBricks == 1
                        && IsUnoccupiedAndDetached(grid) && !grid.Identity.IsNPC && !grid.EntityMetaData.isWorldWreck
                        && !grid.isDestroyed && notices == 0, "The fixture was no longer a safe, fresh isolated grid.");
                    var name = grid.EntityName;
                    var star = grid.SkyTransform.CurrentSystem.seed;
                    Require(star != 0, "The fixture grid had no active star.");
                    player.NetworkSetup.ServerDestroyEntity(grid);
                    Require(grid.isDestroyed && notices == 1 && lastNotice.GridName == name && lastNotice.StarSeed == star
                        && !lastNotice.WasNpc && !lastNotice.WasGeneratedWreck, "Stock destruction did not deliver the expected notice.");
                    grid.OnGameplayDestructionServer();
                    Require(grid.isDestroyed && notices == 1, "Repeating stock destruction delivered a duplicate notice.");
                    observed = true;
                    Logger.LogInfo($"{Label} stage=notice-verified grid={gridKey} star={star} notices={notices}");
                    MoveTo(Step.Cleanup);
                    break;

                case Step.Cleanup:
                    Require(IsFixtureGrid() && IsUnoccupiedAndDetached(grid!), "Cleanup refused a changed or occupied fixture.");
                    if (!grid!.WillBeUnspawned)
                        BlockEntityManager.instance.RemoveAndDestroy(grid, removeFromLocationMapping: true);
                    MoveTo(Step.WaitForRemoval);
                    break;
            }
        }

        private bool IsFixtureGrid()
            => grid != null && gridKey.Length != 0 && grid.SGUID == gridKey && ReferenceEquals(gridId.Entity, grid);

        private static bool IsUnoccupiedAndDetached(ParentEntity candidate)
            => !candidate.IsLocalPlayerParent && !candidate.Rigidbody.isParented && candidate.directDockedChildrens.Count == 0
                && candidate.Tree.ChildrensAndSelf.Count == 1 && candidate.EntityCrew.crews.Count == 0
                && candidate.EntityCrew.npcCharactersIds.Count == 0;

        private void MoveTo(Step next)
        {
            step = next;
            clock.Restart();
            Logger.LogInfo($"{Label} stage={step}");
        }

        private void Fail(Exception exception)
        {
            Logger.LogError($"{Label} FAIL stage={step} {exception.GetBaseException().Message}");
            observed = false;
            if (step != Step.Cleanup && step != Step.WaitForRemoval && grid is not null)
                MoveTo(Step.Cleanup);
            else step = Step.Off;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
