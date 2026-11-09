#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using BepInEx;
using Manager;
using MessagePack;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.LogicOutputProbe
{
    public sealed partial class NativeLogicEvidenceFixture
    {
        private ProfileSession? pendingAutomation;
        private string automatedSession = "";
        private KeelLogicOutputCommand? automatedSource;
        private int automatedSourceId;
        private bool? automatedResult;

        private void PrepareAutomation(string session)
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_LOGIC_OUTPUT_FIXTURE_BUILD") != "1") return;
            automatedSession = session;
            try
            {
                string? game = Environment.GetEnvironmentVariable("SEVO_KEEL_LOGIC_OUTPUT_FIXTURE_GAME");
                string? company = Environment.GetEnvironmentVariable("SEVO_KEEL_LOGIC_OUTPUT_FIXTURE_COMPANY");
                string? product = Environment.GetEnvironmentVariable("SEVO_KEEL_LOGIC_OUTPUT_FIXTURE_PRODUCT");
                string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                Require(Guid.TryParseExact(session, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1",
                    "Automated fixture requires a managed experimental launch.");
                Require(!string.IsNullOrWhiteSpace(game) && !string.IsNullOrWhiteSpace(company)
                    && !string.IsNullOrWhiteSpace(product) && company != "Moonfire Entertainment" && product != "Starship EVO"
                    && Application.companyName == company && Application.productName == product
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                    "Automated fixture requires the exact verified isolated lab identity and game path.");
                pendingAutomation = ProfileProtocol.Open(state!, Paths.GameRootPath, session);
            }
            catch (Exception exception) { AutomatedFailure(exception); }
        }

        private void Update()
        {
            var profile = pendingAutomation;
            if (profile == null || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.InLoadingScreen)
                return;
            if (PlayerState.gameMode != PlayerState.GameMode.creative)
            {
                pendingAutomation = null;
                AutomatedFailure(new InvalidOperationException("Enter a fresh disposable solo creative world."));
                return;
            }
            if (!WorldReady() || !LogicOutputProvider.IsAvailable) return;
            pendingAutomation = null;
            StartCoroutine(GuardAutomatedBuild(BuildAndArm(profile)));
        }

        private IEnumerator GuardAutomatedBuild(IEnumerator build)
        {
            try
            {
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!build.MoveNext()) break;
                        current = build.Current;
                    }
                    catch (Exception exception)
                    {
                        AutomatedFailure(exception);
                        break;
                    }
                    yield return current;
                }
            }
            finally { (build as IDisposable)?.Dispose(); }
        }

        private IEnumerator BuildAndArm(ProfileSession profile)
        {
            var sourceDefinition = ReadFixtureDefinition(profile);
            var player = Dependency.PlayerManager;
            var position = new WorldTransform(player.TransformData);
            position.systemPosition += new Vector3l(new Vector3(0f, 25f, 0f));
            var switchData = TabLogic.SWITCH_BUTTON.PreviewBrickData;
            var sourceData = At(sourceDefinition, 32);
            var signalData = At(TabLogic.EVENT_GATE, 64);
            var pulseData = At(TabLogic.EVENT_GATE, 96);
            var valueData = At(TabLogic.EVENT_GATE, 128);
            Require(WorldReady(), "Native builder is not ready.");
            var grid = player.NetworkSetup.ServerCreateEntity(switchData, position, EntityType.spaceship);
            Require(grid != null, "The stock builder did not create a fixture grid.");
            float deadline = Time.unscaledTime + 12f;
            while (WaitingFor(() => grid!.IsInitialized && FindCommand(grid, switchData) is CommandSwitch,
                deadline, "initialized fixture Switch")) yield return null;

            var arm = (CommandSwitch)FindCommand(grid!, switchData)!;
            Require(arm.State == 0f, "The stock fixture Switch did not start low.");
            PlaceOrDelete(grid!, sourceData, VoxelActionType.build);
            PlaceOrDelete(grid!, signalData, VoxelActionType.build);
            PlaceOrDelete(grid!, pulseData, VoxelActionType.build);
            PlaceOrDelete(grid!, valueData, VoxelActionType.build);

            deadline = Time.unscaledTime + 12f;
            while (WaitingFor(() => FindCommand(grid!, sourceData) is KeelLogicOutputCommand
                && FindCommand(grid!, signalData) is CommandEventGate && FindCommand(grid!, pulseData) is CommandEventGate
                && FindCommand(grid!, valueData) is CommandEventGate, deadline, "native fixture source and gates")) yield return null;

            var output = (KeelLogicOutputCommand)FindCommand(grid!, sourceData)!;
            var signal = (CommandEventGate)FindCommand(grid!, signalData)!;
            var pulse = (CommandEventGate)FindCommand(grid!, pulseData)!;
            var value = (CommandEventGate)FindCommand(grid!, valueData)!;
            automatedSource = output;
            automatedSourceId = output.commandId;
            Link(arm, output);
            Link(output, signal);
            Link(output, pulse);
            Link(output, value);
            ConfigureGate(signal, nameof(KeelLogicOutputCommand.Signal), inverted: true);
            ConfigureGate(pulse, nameof(KeelLogicOutputCommand.Pulse), inverted: false);
            ConfigureGate(value, nameof(KeelLogicOutputCommand.Value), inverted: false);
            AutomatedRecord("automated-built", "native placement and four stock links verified");

            Require(WorldReady() && !arm.IsRemoved, "Native fixture Switch is unavailable.");
            player.CommandActivator.CmdActivate(arm.brickCoord, 1f, true);
            deadline = Time.unscaledTime + 40f;
            while (WaitingFor(() => automatedResult.HasValue, deadline, "native logic propagation")) yield return null;
            Require(automatedResult == true, "Native logic probe reported failure.");

            var current = FindCommand(grid!, sourceData);
            Require(ReferenceEquals(current, output) && !output.IsRemoved, "Owned fixture source changed before removal.");
            int signalJob = FindGateJob(signal, output.Signal.id);
            int pulseJob = FindGateJob(pulse, output.Pulse.id);
            int valueJob = FindGateJob(value, output.Value.id);
            PlaceOrDelete(grid!, output.brickData, VoxelActionType.delete);
            deadline = Time.unscaledTime + 12f;
            while (WaitingFor(() => FindCommand(grid!, sourceData) == null && !signal.IsRemoved && !pulse.IsRemoved
                && !value.IsRemoved && signal.predecessors.Count == 0 && pulse.predecessors.Count == 0
                && value.predecessors.Count == 0, deadline, "native source removal and gate unlink")) yield return null;
            deadline = Time.unscaledTime + 1.25f;
            do
            {
                if (WorldReady())
                {
                    Require(!signal.IsRemoved && !pulse.IsRemoved && !value.IsRemoved && pulse.State == 0f
                        && GateJobDetached(signalJob) && GateJobDetached(pulseJob) && GateJobDetached(valueJob),
                        "A detached native gate still polls its released source or emitted an extra pulse.");
                }
                yield return null;
            } while (Time.unscaledTime < deadline);
            Require(WorldReady() && pulse.State == 0f && GateJobDetached(signalJob)
                && GateJobDetached(pulseJob) && GateJobDetached(valueJob),
                "The native detached gate jobs did not remain quiet.");
            AutomatedRecord("automated-unlinked", "three retained gates with inactive jobs and no extra pulse");
        }

        private static int FindGateJob(CommandEventGate gate, int stateId)
        {
            var jobs = CommandEventGateResources.Datas;
            for (int i = 0; i < jobs.Length; i++)
            {
                var job = jobs[i];
                if (job.commandId == gate.commandId && job.stateDataId == stateId) return i;
            }
            throw new InvalidOperationException("Could not capture the bound native gate job.");
        }

        private static bool GateJobDetached(int index)
        {
            var jobs = CommandEventGateResources.Datas;
            return jobs.IsCreated && index < jobs.Length && jobs[index].commandId == 0 && jobs[index].stateDataId == 0;
        }

        private static Brick ReadFixtureDefinition(ProfileSession profile)
        {
            var json = profile.ReadRegistry() ?? throw new InvalidOperationException("Managed content registry is missing.");
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var registry = (RegistryState)new DataContractJsonSerializer(typeof(RegistryState)).ReadObject(stream);
            var mapping = registry.Mappings.Single(entry => entry.Key == Id + ":fixture" && !entry.Tombstone);
            Require(mapping.Id > 0 && mapping.Id < Brick.BRICK_INDEX.Length, "Fixture content ID is outside the native table.");
            var definition = Brick.BRICK_INDEX[mapping.Id];
            Require(definition != null && definition.IsCommand, "Fixture content did not reach the native brick table.");
            return definition!;
        }

        private static BrickInstanceData At(Brick definition, int x)
        {
            var data = definition.PreviewBrickData;
            data.gridPosition = new Vector3i(x, 0, 0);
            return data;
        }

        private static void PlaceOrDelete(ParentEntity grid, BrickInstanceData data, VoxelActionType type)
        {
            Require(WorldReady() && grid.IsInitialized && ReferenceEquals(grid.ID.Entity, grid),
                "Owned fixture grid or builder is unavailable.");
            var action = new VoxelActionData { entityID = grid.ID, brickData = data, type = type };
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(action, false, 0);
        }

        private static Command? FindCommand(ParentEntity grid, BrickInstanceData expected)
        {
            if (grid == null || !grid.IsInitialized || !ReferenceEquals(grid.ID.Entity, grid)) return null;
            int instanceId = grid.Bricks.GetBrickInstanceId(expected);
            var live = grid.Bricks.GetBrickData(instanceId);
            return live.brickId == expected.brickId && live.gridPosition == expected.gridPosition
                && live.rotation == expected.rotation && live.gridLevel == expected.gridLevel
                ? new BrickCoord(grid.ID, live).Command as Command : null;
        }

        private static void Link(Command from, Command to)
        {
            Require(WorldReady() && !from.IsRemoved && !to.IsRemoved && from.brickCoord.entityID == to.brickCoord.entityID
                && CommandCommon.CanLinkToCommand(from, to), "The stock link preflight rejected a fixture connection.");
            CommandCommon.LinkToCommand(from, to, false, false);
            Require(to.predecessors.Count == 1 && ReferenceEquals(to.predecessors.Command, from),
                "The stock link operation did not establish its expected predecessor.");
        }

        private static void ConfigureGate(CommandEventGate gate, string output, bool inverted)
        {
            var field = typeof(KeelLogicOutputCommand).GetField(output)?.GetCustomAttribute<CommandStateData.CommandStateDataField>()
                ?? throw new InvalidOperationException("Keel output declaration unavailable: " + output);
            var config = MessagePackSerializer.Deserialize<CommandEventGate.EventGateDataSave>(gate.GetCustomData(false));
            config.eventSelected = field.eventId;
            config.signalType = (int)CommandEventGate.SignalType.toggle;
            config.fireGroupMode = 0;
            config.isInverted = inverted;
            config.isPulse = false;
            config.pulseLength = 1f;
            config.comparator = (int)CommandEventGate.Comparator.isSuperior;
            config.valueThreshold = 5f;
            gate.OnSaveLoaded(MessagePackSerializer.Serialize(config), false);
            var saved = MessagePackSerializer.Deserialize<CommandEventGate.EventGateDataSave>(gate.GetCustomData(false));
            Require(saved.eventSelected == field.eventId && saved.isInverted == inverted,
                "The stock gate did not retain its configured channel.");
        }

        private static bool WorldReady()
        {
            var player = Dependency.PlayerManager;
            return GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
                && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
                && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && NetworkServer.active
                && player != null && player.IsLocal;
        }

        private static bool WaitingFor(Func<bool> ready, float deadline, string description)
        {
            if (WorldReady() && ready()) return false;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Timed out waiting for " + description + ".");
            return true;
        }

        private void RecordAutomatedOutcome(KeelLogicOutputCommand source, bool success)
        {
            if (ReferenceEquals(source, automatedSource)) automatedResult = success;
        }

        private void AutomatedFailure(Exception exception)
            => AutomatedRecord("fail", exception.Message.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' '));

        private void AutomatedRecord(string name, string detail)
            => Logger.LogInfo($"KEEL_LOGIC_OUTPUT|{automatedSession}|{automatedSourceId}|{name}|{detail}");

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
