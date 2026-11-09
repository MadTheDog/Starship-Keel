#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;

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
            bool obstruct = Environment.GetEnvironmentVariable("SEVO_KEEL_MECHANISM_PATH_FIXTURE_OBSTRUCTION") == "1";
            if (obstruct)
                Require(NetworkServer.dontListen && SaveManager.CharacterName.EndsWith("-" + session.Substring(0, 8), StringComparison.Ordinal),
                    "Obstruction fixture requires the disposable session character and a non-listening solo server.");
            var player = Dependency.PlayerManager;
            var position = new WorldTransform(player.TransformData);
            if (obstruct)
            {
                float siteDeadline = Time.unscaledTime + 90f;
                while (WaitFor(() => TryClearSite(out position), siteDeadline, "settled player and clear slider site")) yield return null;
                Record("site", FormattableString.Invariant($"system={player.SkyTransform.CurrentSystem.seed};position={position.systemPosition}"));
            }
            else position.systemPosition += new Vector3l(new Vector3(15f, 25f, 0f));
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

            var payload = default(BrickInstanceData);
            if (obstruct)
            {
                var child = owned.ChildrenEntity;
                Require(Ready() && child != null && half >= 0.8f, "The fixture is too short or unavailable for a stock obstruction.");
                payload = HullAt(1.5f * Vector3.up);
                EditBrick(child!, payload, VoxelActionType.build);
                deadline = Time.unscaledTime + 12f;
                while (WaitFor(() => FindBrick(child!, payload, out _), deadline, "stock child payload")) yield return null;
            }

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
                        if (obstruct && (grid.isDestroyed || grid.WillBeUnspawned || !ReferenceEquals(grid.ID.Entity, grid)))
                            throw new InvalidOperationException("The obstruction fixture grid was defeated or unloaded during its baseline.");
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
            if (obstruct)
                foreach (var frame in VerifyObstruction(grid!, owned, arm, payload, origin, facing, basis, half)) yield return frame;
            Record("pass", obstruct ? "fixed-facing travel, native collision rollback and resumed travel"
                : "fixed-facing native carriage translated along both legs and returned");
        }

        private IEnumerable<object?> VerifyObstruction(ParentEntity grid, SliderSystem slider, CommandSwitch arm,
            BrickInstanceData payload, Vector3 origin, Quaternion facing, Quaternion inverseBasis, float half)
        {
            var child = slider.ChildrenEntity;
            var carriage = child.transform;
            var basis = Quaternion.Inverse(inverseBasis);
            var payloadBounds = payload.GetAabb(useGeoBounds: false);
            Vector3 payloadCenter = ((Vector3)payloadBounds.Min + (Vector3)payloadBounds.Max) * 0.5f;
            Vector3 rootCenter = origin + facing * payloadCenter;
            Vector3 contact = rootCenter + basis * new Vector3(half, 0f, half * 0.65f);
            // Native penetration tests carriage surface points against the obstacle. Keep the
            // cross-section wider so a point can enter past the stock 15 mm stop threshold.
            var obstacleScale = new Vector3i(2, 2, 0);
            var bareHull = HullAt(Vector3.zero, obstacleScale);
            var bareBounds = bareHull.GetAabb(useGeoBounds: false);
            Vector3 hullCenter = ((Vector3)bareBounds.Min + (Vector3)bareBounds.Max) * 0.5f;
            var obstacle = HullAt(contact - hullCenter, obstacleScale);
            var obstacleBounds = obstacle.GetAabb(useGeoBounds: false);
            Vector3 obstacleCenter = ((Vector3)obstacleBounds.Min + (Vector3)obstacleBounds.Max) * 0.5f;
            var payloadSize = (Vector3)(payloadBounds.Max - payloadBounds.Min);
            var obstacleSize = (Vector3)(obstacleBounds.Max - obstacleBounds.Min);
            Require(obstacleSize.x > payloadSize.x + 0.2f && obstacleSize.y > payloadSize.y + 0.2f
                && (obstacleCenter - contact).sqrMagnitude < 0.05f && !FindBrick(grid, obstacle, out _)
                && Vector3.Distance(carriage.localPosition, origin) <= 0.002f,
                "The parent obstacle location is occupied or could not be aligned with the second leg.");
            var previousState = typeof(ChildrenEntitySystem).GetField("prevState", BindingFlags.Instance | BindingFlags.NonPublic);
            var penetration = typeof(ChildrenEntitySystem).GetField("prevPenetration", BindingFlags.Instance | BindingFlags.NonPublic);
            var queryId = typeof(ChildrenEntitySystem).GetField("queryId", BindingFlags.Instance | BindingFlags.NonPublic);
            var running = typeof(ChildrenEntitySystem).GetField("isRunning", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(previousState?.FieldType == typeof(float) && penetration?.FieldType == typeof(float)
                && queryId?.FieldType == typeof(int) && running?.FieldType == typeof(bool),
                "Stock rollback or penetration state field changed.");

            float deadline = Time.unscaledTime + 12f;
            while (WaitFor(() => !(bool)running!.GetValue(slider)!, deadline, "completed native return")) yield return null;
            EditBrick(grid, obstacle, VoxelActionType.build);
            deadline = Time.unscaledTime + 12f;
            BrickInstanceData liveObstacle = default;
            while (WaitFor(() => FindBrick(grid, obstacle, out liveObstacle), deadline, "stock root obstacle")) yield return null;
            // Give the stock collider jobs a completed frame after the build.
            yield return null;
            while (WaitFor(() => true, deadline, "completed obstacle build frame")) yield return null;
            Record("obstacle", FormattableString.Invariant(
                $"childCollider={child.bvhElementId};parentCollider={grid.worldColliderId};payload={payloadBounds.Min}/{payloadBounds.Max};obstacle={obstacleBounds.Min}/{obstacleBounds.Max};expectedContact={contact}"));

            int stops = 0;
            void OnStop() => stops++;
            child.OnEntityMoveFinished += OnStop;
            try
            {
                Require(arm.State <= 0f, "Stock switch did not reset after the baseline.");
                Toggle(arm);
                deadline = Time.unscaledTime + 15f;
                int samples = 0, activeQueries = 0;
                float furthest = 0f, maximumDepth = 0f, closest = float.PositiveInfinity;
                var offset = Vector3.zero;
                while (stops == 0 || !Ready())
                {
                    if (Time.unscaledTime > deadline)
                    {
                        Record("obstacle-timeout", FormattableString.Invariant(
                            $"samples={samples};stops={stops};activeQueries={activeQueries};maxDepth={maximumDepth:F4};progress={furthest:F4};lastX={offset.x:F4};lastZ={offset.z:F4};closestCenters={closest:F4}"));
                        throw new TimeoutException("Timed out waiting for native collision stop.");
                    }
                    if (Ready())
                    {
                        Require(!grid.isDestroyed && !grid.WillBeUnspawned && ReferenceEquals(grid.ID.Entity, grid),
                            "The obstruction fixture grid was defeated or unloaded.");
                        offset = ReadOffset(slider, child, origin, facing, inverseBasis);
                        samples++;
                        furthest = Mathf.Max(furthest, offset.x + offset.z);
                        closest = Mathf.Min(closest, Vector3l.Distance(child.TransformData.LocalToSystem(payloadCenter),
                            grid.TransformData.LocalToSystem(obstacleCenter)));
                        int id = (int)queryId!.GetValue(slider)!;
                        if ((bool)running!.GetValue(slider)! && id > 0 && id < QueryPenetrationOnEntityResources.queries.Length)
                        {
                            var query = QueryPenetrationOnEntityResources.queries[id];
                            if (query.isActive && query.ourEntityColliderId == child.bvhElementId
                                && query.entityToCheckWorldColliderId == grid.worldColliderId)
                            {
                                activeQueries++;
                                maximumDepth = Mathf.Max(maximumDepth, query.maxPenetration);
                            }
                        }
                    }
                    yield return null;
                }
                var held = ReadOffset(slider, child, origin, facing, inverseBasis);
                float restored = (float)previousState!.GetValue(slider)!;
                float depth = (float)penetration!.GetValue(slider)!;
                Record("native-stop", FormattableString.Invariant(
                    $"stops={stops};samples={samples};activeQueries={activeQueries};maxDepth={maximumDepth:F4};depth={depth:F4};arm={arm.State:F4};state={slider.State:F4};previous={restored:F4};x={held.x:F4};z={held.z:F4};progress={furthest:F4};closestCenters={closest:F4}"));
                Require(stops == 1 && float.IsFinite(depth) && depth > 0.015f && arm.State > 0f
                    && Mathf.Abs(held.x - half) <= PositionTolerance
                    && held.z > PositionTolerance && held.z < half - PositionTolerance
                    && Mathf.Abs(slider.State - restored) < 0.0001f
                    && Mathf.Abs((held.x + held.z) / (2f * half) - restored) <= PositionTolerance,
                    "Native motion did not restore and hold a non-endpoint state at the parent obstacle.");
                for (int frame = 0; frame < 3; frame++)
                {
                    yield return null;
                    while (WaitFor(() => true, deadline, "stable restored position")) yield return null;
                    Require(stops == 1 && Vector3.Distance(held, ReadOffset(slider, child, origin, facing, inverseBasis)) < 0.002f,
                        "Carriage moved after the native collision stop.");
                }
                Record("blocked", FormattableString.Invariant($"x={held.x:F3};z={held.z:F3};state={restored:F3};depth={depth:F3};nativeStop=1;facing=fixed"));
            }
            finally { child.OnEntityMoveFinished -= OnStop; }

            Require(FindBrick(grid, obstacle, out var current) && current.instanceId == liveObstacle.instanceId,
                "Stock obstacle identity changed before removal.");
            EditBrick(grid, current, VoxelActionType.delete);
            deadline = Time.unscaledTime + 12f;
            while (WaitFor(() => !FindBrick(grid, obstacle, out _), deadline, "native obstacle removal")) yield return null;
            for (int leg = 0; leg < 2; leg++)
            {
                Toggle(arm);
                float endpoint = leg == 0 ? 0f : half;
                deadline = Time.unscaledTime + 15f;
                while (WaitFor(() =>
                {
                    var offset = ReadOffset(slider, child, origin, facing, inverseBasis);
                    return Mathf.Abs(offset.x - endpoint) < 0.002f && Mathf.Abs(offset.z - endpoint) < 0.002f;
                }, deadline, "unobstructed retry endpoint")) yield return null;
            }
            Record("resumed", "exact obstacle deleted; stock carriage returned and reached the clear endpoint");
        }

        private static Vector3 ReadOffset(SliderSystem slider, ChildrenEntity child, Vector3 origin,
            Quaternion facing, Quaternion inverseBasis)
        {
            if (slider.IsRemoved || child == null || !ReferenceEquals(slider.ChildrenEntity, child)
                || Quaternion.Angle(child.transform.localRotation, facing) > 0.25f)
                throw new InvalidOperationException("Carriage identity or facing changed during obstruction.");
            var offset = inverseBasis * (child.transform.localPosition - origin);
            Require(float.IsFinite(offset.x) && float.IsFinite(offset.z) && Mathf.Abs(offset.y) <= PositionTolerance,
                "Carriage left the travel plane.");
            return offset;
        }

        private static BrickInstanceData HullAt(Vector3 position, Vector3i scale = default)
        {
            var hull = TabHull.HULL_A.PreviewBrickData;
            hull.gridLevel = 1;
            hull.scale = BrickInstanceData.PackScale(scale);
            int unit = hull.GridSizeUnit;
            int Quantize(float value) => Mathf.RoundToInt(value * 32f / unit) * unit;
            hull.gridPosition = new Vector3i(Quantize(position.x), Quantize(position.y), Quantize(position.z));
            return hull;
        }

        private static bool FindBrick(VoxelEntity entity, BrickInstanceData expected, out BrickInstanceData live)
        {
            live = default;
            if (entity == null || entity.Parent == null || !ReferenceEquals(entity.ID.Entity, entity)) return false;
            int id = entity.Bricks.GetBrickInstanceId(expected);
            if (id <= 0 || id >= entity.Bricks.brickIds.Length) return false;
            live = entity.Bricks.GetBrickData(id);
            return live.IsValid && live.HealthFactor > 0f && Matches(live, expected);
        }

        private static void EditBrick(VoxelEntity entity, BrickInstanceData brick, VoxelActionType type)
        {
            if (!Ready() || entity == null || !ReferenceEquals(entity.ID.Entity, entity))
                throw new InvalidOperationException("Fixture brick edit is unavailable.");
            var action = new VoxelActionData { entityID = entity.ID, brickData = brick, type = type };
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(action, false, 0);
        }

        private static void Toggle(CommandSwitch arm) =>
            Dependency.PlayerManager.CommandActivator.CmdActivate(arm.brickCoord, 1f, true);

        private static bool TryClearSite(out WorldTransform pose)
        {
            pose = default;
            var player = Dependency.PlayerManager;
            var system = player.SkyTransform.CurrentSystem;
            var origin = player.TransformData.systemPosition;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData) || origin.Equals(Vector3l.zero)
                || PlanetarySystem.SolarBodies.Count == 0 || PlayerState.InMenu
                || GameIntroductionResources.instance?.Medbay?.inAnimation != false)
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
                pose = new WorldTransform(candidate, Quaternion.identity);
                return true;
            }
            return false;
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
