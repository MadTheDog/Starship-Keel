using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.HoverRailGuide
{
    public sealed partial class Probe
    {
        private const string FixtureFlag = "SEVO_KEEL_HOVER_RAIL_FIXTURE";
        private ProfileSession? fixtureProfile;
        private ParentEntity? fixturePod;
        private ParentEntity? fixtureTrack;
        private BrickCoord fixturePodMarker;
        private BrickCoord fixtureTrackMarker;
        private bool fixtureDrive;
        private bool fixtureCaptured;
        private bool fixtureHandedOff;
        private bool fixtureReleased;
        private bool fixtureObserving;
        private string? fixtureUnexpectedRelease;
        private float fixtureNextTelemetry;

        private void PrepareFixture()
        {
            if (Environment.GetEnvironmentVariable(FixtureFlag) != "1") return;
            try
            {
                string? game = Environment.GetEnvironmentVariable(FixtureFlag + "_GAME");
                string? company = Environment.GetEnvironmentVariable(FixtureFlag + "_COMPANY");
                string? product = Environment.GetEnvironmentVariable(FixtureFlag + "_PRODUCT");
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                string? session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && !string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product)
                    && company != "Moonfire Entertainment" && product != "Starship EVO"
                    && Application.companyName == company && Application.productName == product
                    && string.Equals(Path.GetFullPath(game!).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "The probe requires a verified disposable game and managed profile.");
                fixtureProfile = ProfileProtocol.Open(state!, Paths.GameRootPath, session!);
                GridPhysicsStep.Register(Id, "fixture-propulsion", PropelFixture);
            }
            catch (Exception exception) { Logger.LogError("KEEL_HOVER_RAIL FAIL " + exception.Message); }
        }

        private void Update()
        {
            var profile = fixtureProfile;
            if (profile == null || !WorldReady() || !GridPhysicsStep.IsAvailable || !WorldSurfaceQuery.IsAvailable) return;
            fixtureProfile = null;
            StartCoroutine(RunFixture(BuildAndTest(profile)));
        }

        private IEnumerator RunFixture(IEnumerator routine)
        {
            try
            {
                while (true)
                {
                    object? value;
                    try
                    {
                        if (!routine.MoveNext()) break;
                        value = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        Logger.LogError("KEEL_HOVER_RAIL FAIL " + exception);
                        break;
                    }
                    yield return value;
                }
            }
            finally
            {
                fixtureDrive = false;
                fixtureObserving = false;
                (routine as IDisposable)?.Dispose();
            }
        }

        private IEnumerator BuildAndTest(ProfileSession profile)
        {
            var player = Dependency.PlayerManager;
            var origin = new WorldTransform(player.TransformData);
            origin.systemPosition += new Vector3l(35f * Vector3.up);
            var straight = Definition(profile, "straight").PreviewBrickData;
            var pod = Definition(profile, "pod").PreviewBrickData;
            var switchData = TabLogic.SWITCH_BUTTON.PreviewBrickData;
            Require(straight.gridLevel == pod.gridLevel, "Fixture marker grid sizes differ.");
            float unit = straight.GridSize;
            var continuation = straight;
            switchData.gridLevel = pod.gridLevel;
            // Keep the control above the guide point so it clears the rail at the join.
            switchData.gridPosition = new Vector3i(0, switchData.GridSizeUnit, 0);
            fixtureTrack = player.NetworkSetup.ServerCreateEntity(straight, origin, EntityType.spaceship);
            Require(fixtureTrack != null, "Stock builder rejected the guideway.");
            float deadline = Time.unscaledTime + 12f;
            BrickCoord start = default;
            while (WaitFor(() => TryFind(fixtureTrack, straight, out start), deadline, "first rail marker")) yield return null;
            int spacing = 4 * straight.GridSizeUnit;
            var forward = start.LocalForward;
            continuation.gridPosition = straight.gridPosition + new Vector3i(
                Mathf.RoundToInt(forward.x * spacing), Mathf.RoundToInt(forward.y * spacing), Mathf.RoundToInt(forward.z * spacing));
            Place(fixtureTrack!, continuation);
            BrickCoord next = default;
            while (WaitFor(() => TryFind(fixtureTrack, continuation, out next), deadline, "second rail marker")) yield return null;
            Require((next.Position - start.Position - 4f * unit * forward).sqrMagnitude < 0.0025f,
                "The native markers do not share the expected straight endpoint.");

            var frame = start.TransformData;
            Vector3 spawn = start.Position + start.LocalForward * unit + start.LocalUp * (1.5f * unit)
                + start.LocalRight * (0.1f * unit);
            fixturePod = player.NetworkSetup.ServerCreateEntity(pod,
                new WorldTransform(frame.LocalToSystem(spawn), frame.rotation), EntityType.spaceship);
            Require(fixturePod != null, "Stock builder rejected the pod.");
            deadline = Time.unscaledTime + 12f;
            while (WaitFor(() => TryFind(fixturePod, pod, out fixturePodMarker), deadline, "pod marker")) yield return null;
            Place(fixturePod!, switchData);
            BrickCoord toggle = default;
            while (WaitFor(() => TryFind(fixturePod, switchData, out toggle), deadline, "pod Switch")) yield return null;
            var source = (Command)toggle.Command;
            var destination = (Command)fixturePodMarker.Command;
            Require(CommandCommon.CanLinkToCommand(source, destination), "Stock logic rejected the pod switch link.");
            CommandCommon.LinkToCommand(source, destination, false, false);
            Require(ReferenceEquals(destination.predecessors.Command, source), "Pod switch link was not installed.");

            fixtureTrackMarker = start;
            fixtureObserving = true;
            player.CommandActivator.CmdActivate(toggle, 1f, true);
            deadline = Time.unscaledTime + 15f;
            while (WaitFor(() => fixtureCaptured, deadline, "guide capture")) yield return null;
            fixtureDrive = true;
            while (WaitFor(() => fixtureHandedOff || fixtureUnexpectedRelease != null, deadline, "continuous segment handoff"))
                yield return null;
            Require(fixtureHandedOff, "Pod released before handoff: " + fixtureUnexpectedRelease);
            fixtureDrive = false;
            player.CommandActivator.CmdActivate(toggle, 1f, true);
            deadline = Time.unscaledTime + 5f;
            while (WaitFor(() => fixtureReleased, deadline, "switch release")) yield return null;
            Logger.LogInfo("KEEL_HOVER_RAIL PASS live guide capture, straight handoff, and switch release");
        }

        private void PropelFixture(GridPhysicsContext step)
        {
            var pod = fixturePod;
            var track = fixtureTrack;
            if (!fixtureDrive || pod == null || track == null || pod.Rigidbody == null || track.Rigidbody == null) return;
            Vector3 point = fixturePodMarker.WorldPosition;
            Vector3 forward = fixtureTrackMarker.WorldForward;
            Vector3 relative = pod.Rigidbody.VelocityAtPoint(point) - track.Rigidbody.VelocityAtPoint(point);
            if (Time.unscaledTime >= fixtureNextTelemetry)
            {
                fixtureNextTelemetry = Time.unscaledTime + 1f;
                Vector3 offset = point - fixtureTrackMarker.WorldPosition;
                Logger.LogInfo(FormattableString.Invariant($"KEEL_HOVER_RAIL fixture motion along={Vector3.Dot(offset, forward):F2} height={Vector3.Dot(offset, fixtureTrackMarker.WorldUp):F2} cross={Vector3.Dot(offset, fixtureTrackMarker.WorldRight):F2} speed={Vector3.Dot(relative, forward):F2}"));
            }
            float acceleration = Mathf.Clamp(4f * (1.5f - Vector3.Dot(relative, forward)), -8f, 8f);
            if (Mathf.Abs(acceleration) > 0.001f)
                GridForceService.TryApplyAtGridPoint(step, pod, pod.TransformData.WorldToLocal(point),
                    pod.Rigidbody.Mass * acceleration * forward);
        }

        private void RecordFixtureObservation(string message)
        {
            if (!fixtureObserving) return;
            fixtureCaptured |= message == "captured";
            fixtureHandedOff |= message == "continuous segment handoff";
            fixtureReleased |= message == "released: switch";
            if (message.StartsWith("released:", StringComparison.Ordinal) && !fixtureReleased)
                fixtureUnexpectedRelease = message;
        }

        private static Brick Definition(ProfileSession profile, string name)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(profile.ReadRegistry()
                ?? throw new InvalidOperationException("Managed content registry is missing.")));
            var registry = (RegistryState)new DataContractJsonSerializer(typeof(RegistryState)).ReadObject(stream);
            var mapping = registry.Mappings.Single(entry => entry.Key == Id + ":" + name && !entry.Tombstone);
            Require(mapping.Id > 0 && mapping.Id < Brick.BRICK_INDEX.Length && Brick.BRICK_INDEX[mapping.Id]?.IsCommand == true,
                "Fixture marker was not admitted to the native brick table.");
            return Brick.BRICK_INDEX[mapping.Id];
        }

        private static void Place(ParentEntity entity, BrickInstanceData brick)
        {
            Require(WorldReady() && entity.IsInitialized && ReferenceEquals(entity.ID.Entity, entity), "Fixture grid is unavailable.");
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(
                new VoxelActionData { entityID = entity.ID, brickData = brick, type = VoxelActionType.build }, false, 0);
        }

        private static bool TryFind(ParentEntity? entity, BrickInstanceData expected, out BrickCoord placement)
        {
            placement = default;
            if (entity == null || !entity.IsInitialized || !ReferenceEquals(entity.ID.Entity, entity)) return false;
            int instance = entity.Bricks.GetBrickInstanceId(expected);
            if (instance <= 0) return false;
            var brick = entity.Bricks.GetBrickData(instance);
            if (brick.brickId != expected.brickId || brick.gridPosition != expected.gridPosition) return false;
            placement = new BrickCoord(entity.ID, brick);
            return placement.Command is Command;
        }

        private static bool WaitFor(Func<bool> condition, float deadline, string description)
        {
            if (Time.unscaledTime > deadline) throw new TimeoutException(description);
            return !WorldReady() || !condition();
        }

        private static bool WorldReady()
            => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && NetworkServer.active
                && Dependency.PlayerManager is { IsLocal: true };

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
