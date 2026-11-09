#nullable enable
using System;
using System.Collections;
using System.IO;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.SeatInteractionProbe
{
    public sealed partial class Probe
    {
        private enum Trial { None, Clear, Obstructed, Unrelated }
        private static Probe? activeFixture;
        private Harmony? fixtureHarmony;
        private string fixtureSession = "";
        private bool pendingFixture;
        private float fixtureDeadline;
        private Trial trial;
        private EnterCommandSeat? ownedSeat;
        private EnterCommandSeat? unrelatedSeat;
        private bool sampled;
        private int sampledFrame;
        private Vector3l stockEndpoint;
        private Vector3l wantedEndpoint;
        private Vector3 candidateLocal;
        private Vector3 clearCandidate;
        private float stockCorrection;
        private float targetCorrection;
        private bool observedExit;

        private void PrepareDismountFixture()
        {
            const string flag = "SEVO_KEEL_DISMOUNT_FIXTURE";
            if (Environment.GetEnvironmentVariable(flag) != "1") return;
            try
            {
                fixtureSession = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? game = Environment.GetEnvironmentVariable(flag + "_GAME");
                Require(Guid.TryParseExact(fixtureSession, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && Path.IsPathFullyQualified(game)
                    && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test"
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "The fixture requires the exact managed disposable connector lab.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, fixtureSession);
                Require(Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_DISMOUNT_POSES") == "1",
                    "Enable the experimental dismount capability.");
                fixtureHarmony = new Harmony(Id + ".native-dismount-fixture");
                fixtureHarmony.Patch(AccessTools.Method(typeof(EnterCommandSeat), nameof(EnterCommandSeat.OnPostExit),
                    new[] { typeof(PlayerCommandManager) }), postfix: new HarmonyMethod(typeof(Probe), nameof(ObserveDismount))
                    { priority = Priority.Last, after = new[] { Plugin.Id + ".dismountPoses" } });
                activeFixture = this;
                pendingFixture = true;
                RecordDismount("armed", "awaiting solo creative world");
            }
            catch (Exception exception) { FailDismount(exception); }
        }

        private void OnDestroy()
        {
            DisposeFootFixture();
            if (ReferenceEquals(activeFixture, this)) activeFixture = null;
            fixtureHarmony?.UnpatchSelf();
        }

        private void Update()
        {
            UpdateFootFixture();
            if (!pendingFixture) return;
            if (fixtureDeadline == 0f) fixtureDeadline = Time.realtimeSinceStartup + 100f;
            if (Time.realtimeSinceStartup > fixtureDeadline)
            {
                pendingFixture = false;
                FailDismount(new TimeoutException("disposable world readiness"));
                return;
            }
            if (!ReadyForSeatFixture()) return;
            pendingFixture = false;
            StartCoroutine(GuardDismount(RunDismount()));
        }

        private IEnumerator GuardDismount(IEnumerator routine)
        {
            try
            {
                while (ReferenceEquals(activeFixture, this))
                {
                    object? current;
                    try
                    {
                        if (!routine.MoveNext()) break;
                        current = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        FailDismount(exception);
                        break;
                    }
                    yield return current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        private IEnumerator RunDismount()
        {
            Require(!PlayerState.SystemEntered, "Exit all stock commands before arming the fixture.");
            var player = Dependency.PlayerManager;
            var position = new WorldTransform(player.TransformData);
            position.systemPosition += new Vector3l(new Vector3(0f, 25f, 0f));
            var chair = TabInterior.SEAT_PILOT.PreviewBrickData;
            var selected = player.NetworkSetup.ServerCreateEntity(chair, position, EntityType.spaceship);
            position.systemPosition += new Vector3l(new Vector3(8f, 0f, 0f));
            var control = player.NetworkSetup.ServerCreateEntity(chair, position, EntityType.spaceship);
            Require(selected != null && control != null && !ReferenceEquals(selected, control), "Stock builder rejected the seats.");
            float deadline = Time.realtimeSinceStartup + 15f;
            while (WaitForSeat(() => (ownedSeat = FindSeat(selected!, chair)) != null
                && (unrelatedSeat = FindSeat(control!, chair)) != null, deadline, "two stock seats")) yield return null;

            foreach (var test in new[] { Trial.Clear, Trial.Obstructed, Trial.Unrelated })
            {
                var seat = test == Trial.Unrelated ? unrelatedSeat! : ownedSeat!;
                if (test == Trial.Obstructed)
                {
                    var grid = selected!;
                    var hull = TabHull.HULL_A.PreviewBrickData;
                    hull.scale = 0;
                    hull.gridLevel = 1;
                    var center = seat.brickCoord.BlockToEntity(clearCandidate);
                    var bounds = hull.GetAabb(useGeoBounds: true);
                    var cells = 32f * (center - ((Vector3)bounds.Min + (Vector3)bounds.Max) * 0.5f);
                    hull.gridPosition = new Vector3i(Mathf.RoundToInt(cells.x), Mathf.RoundToInt(cells.y), Mathf.RoundToInt(cells.z));
                    Require(ReadyForSeatFixture() && ReferenceEquals(grid.ID.Entity, grid), "Owned seat grid is unavailable.");
                    player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData
                    {
                        entityID = grid.ID, brickData = hull, type = VoxelActionType.build
                    }, false, 0);
                    deadline = Time.realtimeSinceStartup + 10f;
                    while (WaitForSeat(() => FindPlacement(grid, hull).HasValue, deadline, "stock hull obstruction")) yield return null;
                    RecordDismount("native-obstruction", "stock hull placed at the clear trial candidate");
                }

                Require(ReadyForSeatFixture() && !PlayerState.SystemEntered && !seat.IsRemoved, "Owned seat is unavailable.");
                // Move only the local disposable test character to the free approach side of the selected seat.
                var approach = seat.brickCoord.TransformData.LocalToSystem(seat.brickCoord.BlockToEntity(new Vector3(1.8f, 0.9f, 0f)));
                player.SkyTransform.SetTransform(approach, player.TransformData.rotation);
                deadline = Time.realtimeSinceStartup + 8f;
                while (WaitForSeat(() => Delta(player.SkyTransform.SystemPosition, approach).magnitude < 0.25f,
                    deadline, "local approach pose")) yield return null;
                trial = test;
                sampled = false;
                observedExit = false;
                player.MultiTool.ActionActivate.EnterCommand(seat);
                deadline = Time.realtimeSinceStartup + 8f;
                while (WaitForSeat(() => player.IsSeated && PlayerState.SystemEntered, deadline, "stock seat entry")) yield return null;
                yield return new WaitForSecondsRealtime(0.35f);
                deadline = Time.realtimeSinceStartup + 8f;
                while (WaitForSeat(() => player.IsSeated && PlayerState.SystemEntered, deadline, "settled stock seat")) yield return null;
                player.MultiTool.ActionActivate.ExitAllCommand();
                deadline = Time.realtimeSinceStartup + 8f;
                while (WaitForSeat(() => observedExit && !player.IsSeated && !PlayerState.SystemEntered,
                    deadline, "stock final dismount " + test)) yield return null;
                trial = Trial.None;
            }
            RecordDismount("pass", "clear candidate used; obstruction and unrelated seat retained stock exits");
            activeFixture = null;
        }

        private bool TrySelectFixtureExit(SeatInteractionContext context, out Vector3? candidate)
        {
            candidate = null;
            if (!ReferenceEquals(activeFixture, this) || trial == Trial.None) return false;
            var expected = trial == Trial.Unrelated ? unrelatedSeat : ownedSeat;
            if (!ReferenceEquals(context.Seat.Command, expected)) return false;
            var player = Dependency.PlayerManager;
            stockEndpoint = player.newSystemPosition;
            sampled = true;
            sampledFrame = Time.frameCount;
            if (trial == Trial.Unrelated) return true;

            var worldStock = PositionManager.ToWorldPosition(stockEndpoint);
            var stockLocal = context.Seat.TransformData.WorldToLocal(worldStock);
            var seatBasis = context.Seat.brickData.Quaternion;
            candidateLocal = Quaternion.Inverse(seatBasis) * (stockLocal - context.Seat.Position) + 0.9f * Vector3.forward;
            if (trial == Trial.Clear) clearCandidate = candidateLocal;
            else candidateLocal = clearCandidate;
            wantedEndpoint = context.Seat.TransformData.LocalToSystem(context.Seat.BlockToEntity(candidateLocal));
            var worldWanted = PositionManager.ToWorldPosition(wantedEndpoint);
            var axis = 0.5f * (player.newRotation * Vector3.up);
            var gravity = player.PlayerRigidbody.GravityForce;
            stockCorrection = VoxelPhysics.OffsetCapsuleMove(Vector3.zero, worldStock, 0.35f, axis, gravity, SkyPhysics.CONTEXT).magnitude;
            targetCorrection = VoxelPhysics.OffsetCapsuleMove(Vector3.zero, worldWanted, 0.35f, axis, gravity, SkyPhysics.CONTEXT).magnitude;
            candidate = candidateLocal;
            return true;
        }

        private static void ObserveDismount(EnterCommandSeat __instance, PlayerCommandManager __0, bool __result)
        {
            var fixture = activeFixture;
            if (!__result || fixture == null || fixture.trial == Trial.None) return;
            var expected = fixture.trial == Trial.Unrelated ? fixture.unrelatedSeat : fixture.ownedSeat;
            if (!ReferenceEquals(expected, __instance) || !ReferenceEquals(__0, Dependency.PlayerManager)) return;
            try
            {
                Require(fixture.sampled && fixture.sampledFrame == Time.frameCount, "The Keel provider did not observe the final stock exit.");
                float stockError = Delta(__0.newSystemPosition, fixture.stockEndpoint).magnitude;
                if (fixture.trial == Trial.Unrelated)
                {
                    Require(stockError < 0.002f, "The unrelated seat's native exit was overridden.");
                    fixture.RecordDismount("unrelated-stock", FormattableString.Invariant($"stockError={stockError:F4}"));
                }
                else
                {
                    float separation = Delta(fixture.wantedEndpoint, fixture.stockEndpoint).magnitude;
                    float targetError = Delta(__0.newSystemPosition, fixture.wantedEndpoint).magnitude;
                    Require(separation is > 0.35f and < 1.25f && fixture.stockCorrection < 0.0015f,
                        "Stock exit and candidate did not form a valid test case.");
                    if (fixture.trial == Trial.Clear)
                    {
                        Require(fixture.targetCorrection < 0.0015f && targetError < 0.002f,
                            "The clear candidate was not applied.");
                        fixture.RecordDismount("clear-applied", FormattableString.Invariant($"stockError={stockError:F4};targetError={targetError:F4}"));
                    }
                    else
                    {
                        Require(fixture.targetCorrection > 0.005f && stockError < 0.002f,
                            "The obstructed candidate did not fall back to the clear stock exit.");
                        fixture.RecordDismount("blocked-stock", FormattableString.Invariant($"stockError={stockError:F4};obstruction={fixture.targetCorrection:F4}"));
                    }
                }
                fixture.observedExit = true;
            }
            catch (Exception exception) { fixture.FailDismount(exception); }
        }

        private static Vector3 Delta(Vector3l a, Vector3l b) => (a - b).ToFloat3();

        private static EnterCommandSeat? FindSeat(ParentEntity grid, BrickInstanceData expected)
            => FindPlacement(grid, expected)?.Command as EnterCommandSeat;

        private static BrickCoord? FindPlacement(ParentEntity grid, BrickInstanceData expected)
        {
            if (grid == null || !grid.IsInitialized || !ReferenceEquals(grid.ID.Entity, grid)) return null;
            var live = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(expected));
            return live.IsValid && live.brickId == expected.brickId && live.gridPosition == expected.gridPosition
                && live.gridLevel == expected.gridLevel && live.rotation == expected.rotation ? new BrickCoord(grid.ID, live) : null;
        }

        private static bool WaitForSeat(Func<bool> condition, float deadline, string step)
        {
            if (ReadyForSeatFixture() && condition()) return false;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(step);
            return true;
        }

        private static bool ReadyForSeatFixture()
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && !PlayerState.InMenu
                && NetworkServer.active && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal;

        private void FailDismount(Exception exception)
        {
            RecordDismount("fail", trial + " " + exception.Message);
            activeFixture = null;
        }

        private void RecordDismount(string stage, string detail)
            => Logger.LogInfo($"KEEL_DISMOUNT|{fixtureSession}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
    }
}
