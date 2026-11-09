#nullable enable
using System;
using System.Collections;
using System.IO;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.MechanismPathProbe
{
    public sealed partial class Probe
    {
        private const float PositionTolerance = 0.06f;
        private ParentEntity? fixtureGrid;
        private BrickInstanceData fixtureBrick;
        private bool pending;
        private bool selectedForBinding;
        private string session = "";

        private bool Selected(MechanismPathContext context, string label)
        {
            if (fixtureGrid != null && ReferenceEquals(context.Placement.entity, fixtureGrid)
                && Matches(context.Placement.brickData, fixtureBrick))
            {
                selectedForBinding = true;
                return true;
            }
            return context.Placement.GetTagLabel("") == label;
        }

        private void PrepareAutomation()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_MECHANISM_PATH_FIXTURE") != "1") return;
            try
            {
                session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? game = Environment.GetEnvironmentVariable("SEVO_KEEL_MECHANISM_PATH_FIXTURE_GAME");
                string? company = Environment.GetEnvironmentVariable("SEVO_KEEL_MECHANISM_PATH_FIXTURE_COMPANY");
                string? product = Environment.GetEnvironmentVariable("SEVO_KEEL_MECHANISM_PATH_FIXTURE_PRODUCT");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_MECHANISM_PATHS") == "1",
                    "Use a managed lab session.");
                Require(!string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company)
                    && !string.IsNullOrWhiteSpace(product) && company != "Moonfire Entertainment" && product != "Starship EVO"
                    && Application.companyName == company && Application.productName == product
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "Use the exact verified isolated lab identity and game path.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, session);
                pending = true;
            }
            catch (Exception exception) { Record("fail", exception.Message); }
        }

        private void Update()
        {
            if (!pending || !Ready()) return;
            pending = false;
            StartCoroutine(Guard(Run()));
        }

        private IEnumerator Guard(IEnumerator fixture)
        {
            try
            {
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!fixture.MoveNext()) break;
                        current = fixture.Current;
                    }
                    catch (Exception exception)
                    {
                        Record("fail", exception.Message);
                        break;
                    }
                    yield return current;
                }
            }
            finally { (fixture as IDisposable)?.Dispose(); }
        }

        private IEnumerator Run()
        {
            var player = Dependency.PlayerManager;
            var position = new WorldTransform(player.TransformData);
            position.systemPosition += new Vector3l(new Vector3(15f, 25f, 0f));
            var root = TabLogic.SWITCH_BUTTON.PreviewBrickData;
            var grid = player.NetworkSetup.ServerCreateEntity(root, position, EntityType.spaceship);
            Require(grid != null, "Stock builder did not create a fixture grid.");
            float deadline = Time.unscaledTime + 12f;
            while (WaitFor(() => grid!.IsInitialized, deadline, "initialized grid")) yield return null;

            var data = TabMechanism.SLIDING.PreviewBrickData;
            data.gridPosition = new Vector3i(64, 0, 0);
            data.scale = BrickInstanceData.PackScale(new Vector3i(2, 0, 0));
            fixtureBrick = data;
            fixtureGrid = grid;
            Require(Ready() && ReferenceEquals(grid!.ID.Entity, grid), "Fixture grid is unavailable.");
            var action = new VoxelActionData { entityID = grid!.ID, brickData = data, type = VoxelActionType.build };
            player.NetworkedBuilder.LocalVoxelAction(action, false, 0);

            SliderSystem? slider = null;
            deadline = Time.unscaledTime + 12f;
            while (WaitFor(() =>
            {
                var live = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(data));
                if (!Matches(live, data)) return false;
                slider = new BrickCoord(grid.ID, live).Command as SliderSystem;
                return slider != null && !slider.IsRemoved && slider.ChildrenEntity != null;
            }, deadline, "native slider with carriage")) yield return null;

            var owned = slider!;
            var liveRoot = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(root));
            var arm = Matches(liveRoot, root) ? new BrickCoord(grid.ID, liveRoot).Command as CommandSwitch : null;
            if (arm == null || arm.IsRemoved || arm.State != 0f || !CommandCommon.CanLinkToCommand(arm, owned))
                throw new InvalidOperationException("Stock switch could not link to the owned slider.");
            CommandCommon.LinkToCommand(arm, owned, false, false);
            Require(owned.predecessors.Count == 1 && ReferenceEquals(owned.predecessors.Command, arm),
                "Stock builder did not retain the switch link.");
            float travel = owned.amplitude;
            Require(selectedForBinding && float.IsFinite(travel) && travel > PositionTolerance * 8,
                "The stock slider did not bind the fixture path or travel is too short.");
            var carriage = owned.ChildrenEntity.transform;
            var origin = carriage.localPosition;
            var facing = carriage.localRotation;
            var basis = Quaternion.Inverse(owned.brickData.Quaternion);
            float half = travel * 0.5f;
            Record("bound", FormattableString.Invariant($"travel={travel:F3};command={owned.commandId}"));

            for (int direction = 0; direction < 2; direction++)
            {
                Require(Ready() && !owned.IsRemoved, "The owned slider is unavailable.");
                player.CommandActivator.CmdActivate(arm.brickCoord, 1f, true);
                bool sawX = false;
                bool sawZ = false;
                float previous = direction == 0 ? 0f : travel;
                deadline = Time.unscaledTime + 20f;
                while (true)
                {
                    if (Time.unscaledTime > deadline)
                        throw new InvalidOperationException("Timed out while driving the stock slider.");
                    if (Ready())
                    {
                        if (owned.IsRemoved || carriage == null || owned.ChildrenEntity == null
                            || !ReferenceEquals(owned.ChildrenEntity.transform, carriage))
                            throw new InvalidOperationException("Native carriage identity changed during travel.");
                        var offset = basis * (carriage.localPosition - origin);
                        float x = offset.x;
                        float z = offset.z;
                        float progress = x + z;
                        Require(float.IsFinite(progress) && Mathf.Abs(offset.y) <= PositionTolerance
                            && Quaternion.Angle(carriage.localRotation, facing) <= 0.25f,
                            "Carriage left the travel plane or changed facing.");
                        Require(x >= -PositionTolerance && x <= half + PositionTolerance && z >= -PositionTolerance
                            && z <= half + PositionTolerance && (Mathf.Abs(z) <= PositionTolerance
                                || Mathf.Abs(x - half) <= PositionTolerance),
                            "Carriage left the two orthogonal legs.");
                        Require(direction == 0 ? progress + PositionTolerance >= previous : progress - PositionTolerance <= previous,
                            "Carriage reversed before the requested endpoint.");
                        previous = progress;
                        sawX |= x > PositionTolerance && x < half - PositionTolerance && Mathf.Abs(z) <= PositionTolerance;
                        sawZ |= z > PositionTolerance && z < half - PositionTolerance && Mathf.Abs(x - half) <= PositionTolerance;
                        float endpoint = direction == 0 ? half : 0f;
                        if (Mathf.Abs(x - endpoint) < 0.002f && Mathf.Abs(z - endpoint) < 0.002f)
                        {
                            Require(sawX && sawZ, "Rendered samples did not cover both path legs.");
                            Record(direction == 0 ? "outbound" : "returned",
                                FormattableString.Invariant($"x={x:F3};z={z:F3};facing=fixed;legs=2"));
                            break;
                        }
                    }
                    yield return null;
                }
            }
            Record("pass", "fixed-facing native carriage translated along both legs and returned");
        }

        private static bool Matches(BrickInstanceData live, BrickInstanceData expected)
            => live.brickId == expected.brickId && live.gridPosition == expected.gridPosition
                && live.rotation == expected.rotation && live.gridLevel == expected.gridLevel && live.scale == expected.scale;

        private static bool Ready()
        {
            var player = Dependency.PlayerManager;
            return GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && NetworkServer.active
                && player != null && player.IsLocal;
        }

        private static bool WaitFor(Func<bool> ready, float deadline, string description)
        {
            if (Ready() && ready()) return false;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Timed out waiting for " + description + ".");
            return true;
        }

        private void Record(string stage, string detail)
            => Logger.LogInfo($"KEEL_MECHANISM_PATH|{session}|{stage}|{detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')}");

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
