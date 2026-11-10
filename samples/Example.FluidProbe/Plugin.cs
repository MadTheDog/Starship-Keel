using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using Game.Player;
using Manager;
using MessagePack;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UI;
using UnityEngine;
using UnityEngine.UI;
using VoxelEngine;

namespace Sevo.Example.FluidProbe;

[BepInPlugin(Owner, "Keel Fluid Lab Probe", "0.1.0")]
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.6")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Owner = "example.fluid-probe";
    private const string Coolant = Owner + ":coolant";
    private const string Brine = Owner + ":brine";
    private readonly Stopwatch clock = new Stopwatch();
    private string session = "";
    private string stage = "startup";
    private bool pending;
    private bool startSolo;
    private bool drainStalledFrame;
    private bool bootSurvivalOnly;
    private bool bootOnly;
    private bool roundTrip;
    private int harnessFrames;
    private ParentEntity? grid;
    private BrickInstanceData sourceData;
    private BrickInstanceData sinkData;
    private float seeded;
    private float remainingAtTransfer;
    private float receivedAtTransfer;
    private float consumedAtTransfer;
    private bool finished;

    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE") != "1")
            return;
        try
        {
            session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
            var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            Require(Guid.TryParseExact(session, "N", out _) && state != null, "A managed launcher session is required.");
            Require((Application.companyName == "SEVO Keel Hooks Lab016" && Application.productName == "SEVO HK Test")
                || (Application.companyName == "SEVO Keel Gen Lab 0110" && Application.productName == "SEVO WG Test"),
                "The probe only operates in the disposable Keel hooks lab.");
            ProfileProtocol.Open(state!, Paths.GameRootPath, session);
            startSolo = Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE_START_SOLO") == "1";
            bootSurvivalOnly = startSolo && Environment.GetEnvironmentVariable("SEVO_KEEL_LAB_BOOT_SURVIVAL") == "1";
            drainStalledFrame = Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE_DRAIN_STALLED_FRAME") == "1";
            roundTrip = Environment.GetEnvironmentVariable("SEVO_KEEL_FLUID_PROBE_ROUNDTRIP") == "1";
            bootOnly = bootSurvivalOnly || startSolo && Environment.GetEnvironmentVariable("SEVO_KEEL_LAB_BOOT_CREATIVE") == "1";
            if (!bootOnly)
            {
                FluidRegistration.Register(Owner, "coolant", 1, new FluidDefinition("Probe Coolant", 0x32aaff, 1.12f, 0f));
                FluidRegistration.Register(Owner, "brine", 1, new FluidDefinition("Probe Brine", 0x88aa44, 1.24f, 0f));
                FluidRegistration.RegisterTick(Owner, "pipe-loop", Tick);
            }
            clock.Start();
            pending = true;
            Step("waiting-for-world");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Update()
    {
        if (!pending)
            return;
        if (clock.Elapsed > TimeSpan.FromMinutes(3))
        {
            pending = false;
            Fail(new TimeoutException(stage));
            return;
        }
        bool titleReady = startSolo && PlayerState.sessionMode == PlayerState.SessionMode.mainMenu
            && !NetworkServer.active && NetworkManager.singleton != null && SkinDataCommon.RaceVariants is { Length: > 0 }
            && UI_TitleScreen.instance != null && UI_TitleScreen.IsActive.Value;
        if (!titleReady && !SoloReady())
            return;
        pending = false;
        if (drainStalledFrame)
            StartCoroutine(DrainStalledFrames());
        StartCoroutine(Guard(Run(titleReady)));
    }

    private IEnumerator DrainStalledFrames()
    {
        var phase = typeof(GameloopManager).GetField("jobFrameState", BindingFlags.Static | BindingFlags.NonPublic);
        if (phase == null || !phase.FieldType.IsEnum || !Enum.IsDefined(phase.FieldType, "JobsScheduled"))
        {
            Fail(new InvalidOperationException("The lab fallback cannot identify the exact native scheduled phase."));
            yield break;
        }
        object scheduled = Enum.Parse(phase.FieldType, "JobsScheduled");
        Logger.LogInfo($"KEEL_FLUID_PROBE stage=lab-drain-armed session={session}");
        var end = new WaitForEndOfFrame();
        var stalled = new Stopwatch();
        int missed = 0;
        while (!PlayerState.IsQuittingToDesktopOrTitle)
        {
            yield return end;
            var graph = JobGraph.Main;
            if (!GameloopManager.IsMainThread || !Equals(phase.GetValue(null), scheduled)
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.IsQuittingToDesktopOrTitle
                || graph.CurrentFrame != Time.frameCount || graph.ScheduledNodesThisFrame.Count == 0)
            {
                missed = 0;
                stalled.Reset();
                continue;
            }
            stalled.Start();
            if (++missed < 6 || stalled.ElapsedMilliseconds < 500)
                continue;
            if (harnessFrames == 0)
                Logger.LogWarning($"KEEL_FLUID_PROBE mode=harness stage=stock-render-end-stalled frame={Time.frameCount} session={session}");
            try
            {
                harnessFrames++;
                GameloopManager.OnAfterRendering();
            }
            catch (Exception exception)
            {
                if (finished)
                    Logger.LogError($"KEEL_FLUID_PROBE HARNESS_FAIL stage=post-result-drain session={session} {exception}");
                else
                    Fail(exception);
                yield break;
            }
        }
    }

    private IEnumerator Guard(IEnumerator run)
    {
        while (true)
        {
            object? next;
            try
            {
                if (!run.MoveNext())
                    yield break;
                next = run.Current;
            }
            catch (Exception exception)
            {
                Fail(exception);
                yield break;
            }
            yield return next;
        }
    }

    private IEnumerator Run(bool enterSolo)
    {
        if (enterSolo)
        {
            Step("starting-disposable-solo");
            UI_TitleScreen.instance.ShowPlayScreen();
            yield return null;
            var selection = UnityEngine.Object.FindAnyObjectByType<UI_CharacterSelection>();
            Require(selection != null && UI_CharacterSelection.IsActive.Value, "Stock character selection did not open.");
            selection!.ShowCharacterCreationWindow();
            yield return null;
            var creation = UnityEngine.Object.FindAnyObjectByType<UI_CharacterCreation>();
            Require(creation != null && UI_CharacterCreation.IsActive.Value, "Stock character creation did not open.");
            Read<InputField>(creation!, "input").text = (bootSurvivalOnly ? "TerrainProbe-" : "FluidProbe-") + session.Substring(0, 8);
            Read<InputField>(creation!, "inputSeed").text = "11062645";
            creation!.OnInput();
            creation.OnSeedUpdated();
            if (Read<bool>(creation, "isCreative") == bootSurvivalOnly)
                creation.OnGameModeButton();
            Require(Read<bool>(creation, "isCreative") != bootSurvivalOnly && !Read<bool>(creation, "isInvalid"),
                "Stock character creation rejected the requested fixture.");
            NetworkServer.dontListen = true;
            creation.PlayWithNewCharacter();
        }
        if (bootOnly) yield break;
        while (!SoloReady() || !FluidRegistration.IsAvailable || GameloopManager.jobStarted)
        {
            CheckDeadline();
            yield return null;
        }

        Step("placing-native-tank-pipe-tank");
        var player = Dependency.PlayerManager;
        var position = new WorldTransform(player.TransformData);
        position.systemPosition += new Vector3l(new Vector3(0f, 25f, 0f));
        var pipe = TabStorage.PIPE.PreviewBrickData;
        sourceData = TabStorage.TANK_CUBIC.PreviewBrickData;
        sourceData.gridLevel = pipe.gridLevel;
        sourceData.scale = 0;
        pipe.scale = 0;
        int size = pipe.GridSizeUnit;
        pipe.gridPosition = new Vector3i(0, size, 0);
        sinkData = sourceData;
        sinkData.gridPosition = new Vector3i(0, 2 * size, 0);
        grid = player.NetworkSetup.ServerCreateEntity(sourceData, position, EntityType.spaceship);
        Require(grid != null, "The source tank grid was rejected.");
        while (!grid!.IsInitialized || !FindTank(sourceData).HasValue || GameloopManager.jobStarted)
        {
            CheckDeadline();
            yield return null;
        }
        Place(pipe);
        Place(sinkData);
        Step("waiting-for-bound-tanks");
        while (!finished)
        {
            CheckDeadline();
            yield return null;
        }
    }

    private void Tick(FluidTickContext tick)
    {
        if (finished || grid == null)
            return;
        try
        {
            if (stage == "waiting-for-reconstruction")
            {
                var restored = FindTank(sinkData);
                if (!grid.IsInitialized || !restored.HasValue)
                    return;
                Require(tick.TryGetAmount(restored.Value, Coolant, out var restoredLitres),
                    "The reconstructed tank lost its fluid binding.");
                Require(Math.Abs(restoredLitres - (receivedAtTransfer - consumedAtTransfer)) < 0.01f,
                    "The reconstructed tank lost its fluid volume.");
                ReportPass(restoredLitres);
                return;
            }
            if (stage != "waiting-for-bound-tanks" && stage != "waiting-for-native-transfer")
                return;
            var source = FindTank(sourceData);
            var sink = FindTank(sinkData);
            if (!source.HasValue || !sink.HasValue)
                return;
            if (seeded == 0f)
            {
                Require(tick.TryBindTank(source.Value, Coolant) && tick.TryBindTank(sink.Value, Coolant),
                    "Native tank binding failed.");
                Require(tick.TryGetAmount(source.Value, Coolant, out var oldSource) && oldSource == 0f
                    && tick.TryGetAmount(sink.Value, Coolant, out var oldSink) && oldSink == 0f,
                    "Native tanks did not start empty.");
                Require(tick.Produce(sink.Value, Brine, 10f) == 0f, "A tank accepted a different registered fluid.");
                Require(tick.TryRequestRefill(sink.Value, Coolant), "The sink rejected the native refill request.");
                seeded = tick.Produce(source.Value, Coolant, 80f);
                Require(seeded > 0.1f, "The producer did not add fluid.");
                Step("waiting-for-native-transfer");
                return;
            }
            Require(tick.TryGetAmount(source.Value, Coolant, out var remaining), "The source disappeared.");
            Require(tick.TryGetAmount(sink.Value, Coolant, out var received), "The sink disappeared.");
            if (received <= 0.1f)
                return;
            Require(Math.Abs((remaining + received) - seeded) < 0.01f,
                "Native transfer did not conserve the seeded volume.");
            var removed = tick.Consume(sink.Value, Coolant, received * 0.5f);
            Require(Math.Abs(removed - received * 0.5f) < 0.01f, "The native consumer reported the wrong removed volume.");
            remainingAtTransfer = remaining;
            receivedAtTransfer = received;
            consumedAtTransfer = removed;
            if (roundTrip)
                Reconstruct();
            else
                ReportPass(null);
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Reconstruct()
    {
        Step("snapshotting-native-grid");
        var save = BrickEntity_mp.GetSave(grid, clearSGUID: true);
        save.ClearInstanceData();
        var payload = MessagePackSerializer.Serialize(save, SaveTools.LZ4Compress);
        var clone = MessagePackSerializer.Deserialize<BrickEntity_mp>(payload, SaveTools.LZ4Compress);
        var player = Dependency.PlayerManager;
        var position = new WorldTransform(player.TransformData);
        position.systemPosition += new Vector3l(new Vector3(0f, 35f, 0f));
        var shell = BlockEntityManager.instance.CreateEntity(position, player.SkyTransform.CurrentSystem);
        Require(shell != null && !ReferenceEquals(shell, grid), "Native save reconstruction needs a fresh grid.");
        clone.LoadInto(shell);
        grid = shell;
        Step("waiting-for-reconstruction");
    }

    private void ReportPass(float? restored)
    {
        finished = true;
        Logger.LogInfo(FormattableString.Invariant(
            $"KEEL_FLUID_PROBE PASS session={session} mode={(harnessFrames == 0 ? "native" : "harness")} harnessFrames={harnessFrames} burstEnabled={Unity.Burst.BurstCompiler.IsEnabled} produced={seeded:F3} source={remainingAtTransfer:F3} received={receivedAtTransfer:F3} consumed={consumedAtTransfer:F3} reconstructed={restored:F3}"));
    }

    private BrickCoord? FindTank(BrickInstanceData expected)
    {
        int instanceId = grid!.Bricks.GetBrickInstanceId(expected);
        var found = grid.Bricks.GetBrickData(instanceId);
        if (found.brickId != expected.brickId || !found.gridPosition.Equals(expected.gridPosition))
            return null;
        var coord = new BrickCoord(grid.ID, found);
        return coord.Command is StorageCommandData ? coord : (BrickCoord?)null;
    }

    private void Place(BrickInstanceData brick)
    {
        var action = new VoxelActionData { entityID = grid!.ID, brickData = brick, type = VoxelActionType.build };
        Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(action, false, 0);
    }

    private static bool SoloReady() => PlayerState.sessionMode == PlayerState.SessionMode.solo
        && !PlayerState.InLoadingScreen && NetworkServer.active && Dependency.PlayerManager != null
        && Dependency.PlayerManager.IsLocal;

    private static T Read<T>(UI_CharacterCreation creation, string name) =>
        (T)(typeof(UI_CharacterCreation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(creation)
            ?? throw new InvalidOperationException("Missing character creation field " + name));

    private void CheckDeadline()
    {
        if (clock.Elapsed > TimeSpan.FromMinutes(3))
            throw new TimeoutException(stage);
    }

    private void Step(string value)
    {
        stage = value;
        Logger.LogInfo($"KEEL_FLUID_PROBE stage={stage} session={session}");
    }

    private void Fail(Exception exception)
    {
        finished = true;
        Logger.LogError($"KEEL_FLUID_PROBE FAIL stage={stage} session={session} {exception}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
