using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.BlockAssemblyProbe
{
    [BepInPlugin(Id, "Keel Native Shipyard Assembly Fixture", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class NativeShipyardFixture : BaseUnityPlugin
    {
        private const string Id = "example.keel.shipyardassemblyfixture";
        private static NativeShipyardFixture? active;
        private Harmony? observer;
        private string session = "";
        private bool pending;
        private float readyDeadline;
        private CommandShipyard? yard;
        private bool queryRunning;
        private bool originalRan;
        private int observations;

        private void Awake()
        {
            const string flag = "SEVO_KEEL_SHIPYARD_ASSEMBLY_FIXTURE";
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
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_BLOCK_ASSEMBLIES") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_SHIPYARD_ASSEMBLY_ELIGIBILITY") == "1",
                    "Enable block assemblies and shipyard assembly eligibility.");
                observer = new Harmony(Id);
                observer.Patch(AccessTools.Method(typeof(CommandShipyard), nameof(CommandShipyard.CanSpawnBlueprint),
                    new[] { typeof(EntityMetaData), typeof(Dictionary<int, int>), typeof(StringBuilder) }),
                    postfix: new HarmonyMethod(typeof(NativeShipyardFixture), nameof(ObserveNativeQuery))
                    { priority = Priority.Last });
                active = this;
                pending = true;
                Record("armed", "waiting for stock solo creative readiness");
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Update()
        {
            if (!pending) return;
            if (readyDeadline == 0f) readyDeadline = Time.realtimeSinceStartup + 100f;
            if (Time.realtimeSinceStartup > readyDeadline)
            {
                Fail(new TimeoutException("stock solo creative readiness"));
                return;
            }
            if (!Ready() || !CanBuild()) return;
            pending = false;
            StartCoroutine(Guard(Run()));
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(active, this)) active = null;
            observer?.UnpatchSelf();
        }

        private IEnumerator Guard(IEnumerator routine)
        {
            try
            {
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!routine.MoveNext()) break;
                        current = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        Fail(exception);
                        break;
                    }
                    yield return current;
                }
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }

        private IEnumerator Run()
        {
            var player = Dependency.PlayerManager;
            var position = new WorldTransform(player.TransformData);
            position.systemPosition += new Vector3l(new Vector3(0f, 25f, 0f));
            var yardData = TabSystem.SHIPYARD.PreviewBrickData;
            var grid = player.NetworkSetup.ServerCreateEntity(yardData, position, EntityType.spaceship);
            Require(grid != null, "Native shipyard fixture grid was rejected.");
            float deadline = Time.realtimeSinceStartup + 15f;
            while (WaitFor(() => (yard = Find(grid!, yardData)?.Command as CommandShipyard) != null,
                deadline, "initialized native shipyard")) yield return null;

            var blueprint = new EntityMetaData
            {
                boundSize = Vector3.one,
                mass = 1f,
                brickNumber = 1,
                spaceshipOrientation = Quaternion.identity
            };
            Check(blueprint, "native-baseline", true, true);

            BrickCoord frame = default;
            BrickCoord welder = default;
            Require(AssemblyExamples.RequireShipyardFrameAndWelder(yard!, grid!, () => (frame, welder), out var requirement)
                && requirement != null, "Keel did not attach a requirement to the live stock shipyard.");
            using (requirement)
            {
                Check(blueprint, "empty-denied", false, false);
                var frameData = At(TabHull.HULL_A, 64, yardData.gridLevel);
                PlaceOrDelete(grid!, frameData, VoxelActionType.build);
                deadline = Time.realtimeSinceStartup + 12f;
                while (WaitFor(() => Find(grid!, frameData).HasValue, deadline, "stock frame part")) yield return null;
                frame = Find(grid!, frameData)!.Value;
                Check(blueprint, "incomplete-denied", false, false);

                var welderData = At(TabLogic.SWITCH_BUTTON, 128, yardData.gridLevel);
                PlaceOrDelete(grid!, welderData, VoxelActionType.build);
                deadline = Time.realtimeSinceStartup + 12f;
                while (WaitFor(() => Find(grid!, welderData).HasValue, deadline, "stock welder stand-in")) yield return null;
                welder = Find(grid!, welderData)!.Value;
                Check(blueprint, "complete-stock", true, true);

                PlaceOrDelete(grid!, welder.brickData, VoxelActionType.delete);
                deadline = Time.realtimeSinceStartup + 12f;
                while (WaitFor(() => !Find(grid!, welderData).HasValue, deadline, "native part deletion")) yield return null;
                Check(blueprint, "deleted-denied", false, false);

                var replacementData = At(TabHull.HULL_A, 128, yardData.gridLevel);
                PlaceOrDelete(grid!, replacementData, VoxelActionType.build);
                deadline = Time.realtimeSinceStartup + 12f;
                while (WaitFor(() => Find(grid!, replacementData).HasValue, deadline, "different native replacement"))
                    yield return null;
                Check(blueprint, "replacement-denied", false, false);
            }
            Check(blueprint, "requirement-released", true, true);
            Record("pass", "native preview used; no blueprint committed");
        }

        private void Check(EntityMetaData blueprint, string stage, bool expected, bool runOriginal)
        {
            Require(Ready() && yard != null && !yard.IsRemoved, "Native preview query requires the live owned shipyard.");
            observations = 0;
            originalRan = false;
            queryRunning = true;
            bool actual;
            try { actual = yard!.CanSpawnBlueprint(blueprint, null, new StringBuilder()); }
            finally { queryRunning = false; }
            Require(observations == 1 && actual == expected && originalRan == runOriginal,
                $"{stage} returned={actual};original={originalRan};observations={observations}");
            Record(stage, $"returned={actual};original={originalRan}");
        }

        private static void ObserveNativeQuery(CommandShipyard __instance, bool __runOriginal)
        {
            var fixture = active;
            if (fixture == null || !fixture.queryRunning || !ReferenceEquals(fixture.yard, __instance)) return;
            fixture.observations++;
            fixture.originalRan = __runOriginal;
        }

        private static BrickInstanceData At(Brick definition, int x, byte gridLevel)
        {
            var data = definition.PreviewBrickData;
            data.gridPosition = new Vector3i(x, 0, 0);
            data.gridLevel = gridLevel;
            data.scale = 0;
            return data;
        }

        private static BrickCoord? Find(ParentEntity grid, BrickInstanceData expected)
        {
            if (grid == null || !grid.IsInitialized || !ReferenceEquals(grid.ID.Entity, grid)) return null;
            var actual = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(expected));
            return actual.IsValid && actual.brickId == expected.brickId && actual.gridPosition == expected.gridPosition
                && actual.rotation == expected.rotation && actual.gridLevel == expected.gridLevel
                ? new BrickCoord(grid.ID, actual) : null;
        }

        private static void PlaceOrDelete(ParentEntity grid, BrickInstanceData data, VoxelActionType type)
        {
            Require(Ready() && grid.IsInitialized && ReferenceEquals(grid.ID.Entity, grid), "Owned shipyard grid is unavailable.");
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(
                new VoxelActionData { entityID = grid.ID, brickData = data, type = type }, false, 0);
        }

        private static bool WaitFor(Func<bool> condition, float deadline, string step)
        {
            if (Ready() && condition()) return false;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(step);
            return true;
        }

        private bool CanBuild()
        {
            var player = Dependency.PlayerManager;
            return SaveManager.CharacterName == "ConnectorProbe-" + session.Substring(0, 8)
                && !PlayerState.InMenu && !PlayerState.SystemEntered && player.Health != null && !player.Health.IsRespawning
                && player.TransformData.systemPosition.Magnitude >= 100f
                && GameIntroductionResources.instance?.Medbay?.inAnimation == false;
        }

        private static bool Ready()
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && NetworkServer.active
                && NetworkServer.dontListen && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal;

        private void Fail(Exception exception)
        {
            pending = false;
            Record("fail", exception.Message);
        }

        private void Record(string stage, string detail)
            => Logger.LogInfo($"KEEL_SHIPYARD_ASSEMBLY|{session}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

        private static void Require(bool value, string detail)
        {
            if (!value) throw new InvalidOperationException(detail);
        }
    }
}
