#nullable enable
using System;
using System.Globalization;
using BepInEx;
using BepInEx.Logging;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.LogicOutputProbe
{
    [BepInPlugin(Id, "Keel Native Logic Evidence Fixture", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class NativeLogicEvidenceFixture : BaseUnityPlugin
    {
        private const string Id = "example.keel.logicfixture";

        private void Awake()
        {
            var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_LOGIC_OUTPUT_FIXTURE") != "1"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR"))
                || string.IsNullOrWhiteSpace(session) || session.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0)
                return;

            ContentRegistration.RegisterFunctionalBlock(Id, "fixture", 1, "Probe Native Logic Fixture",
                () => TabHull.HULL_A, context => new Fixture(context, Logger, session, RecordAutomatedOutcome));
            LogicOutputProvider.Declare(Id, "fixture");
            PrepareAutomation(session);
        }

        private sealed class Fixture : FunctionalBlockBehavior
        {
            private enum Phase { Idle, Baseline, High, PulseLow, Low, HeldHigh, Passed, Failed }

            private readonly FunctionalBlockContext context;
            private readonly ManualLogSource logger;
            private readonly string session;
            private readonly Action<KeelLogicOutputCommand, bool> outcome;
            private Phase phase;
            private bool loaded;
            private float deadline;
            private KeelLogicOutputCommand? source;
            private Gate signal;
            private Gate pulse;
            private Gate value;
            private int sourceId;

            internal Fixture(FunctionalBlockContext context, ManualLogSource logger, string session,
                Action<KeelLogicOutputCommand, bool> outcome)
            {
                this.context = context;
                this.logger = logger;
                this.session = session;
                this.outcome = outcome;
            }

            public override void OnSpawn() => GameloopManager.OnTransformJobFinished += Tick;
            public override void OnLoad() => loaded = true;

            public override void OnSustainedInput(bool enabled)
            {
                if (enabled && phase == Phase.Idle)
                {
                    phase = Phase.Baseline;
                    deadline = Time.unscaledTime + 8f;
                }
            }

            public override void OnDespawn()
            {
                GameloopManager.OnTransformJobFinished -= Tick;
                if (phase != Phase.Passed || sourceId <= 0) return;
                if (CanInspect() && source != null && TryReadOutputs(source, out var outputs))
                    Write("despawn", "signal=" + F(outputs.Signal) + ",pulse=" + F(outputs.Pulse) + ",value=" + F(outputs.Value));
                else
                    Write("despawn-unobserved", "native state unavailable");
            }

            private void Tick()
            {
                if (phase is Phase.Idle or Phase.Passed or Phase.Failed || !CanInspect()) return;
                try
                {
                    if (!context.TryGetPlacement(out var placement) || placement.Command is not KeelLogicOutputCommand current)
                        return;
                    sourceId = current.commandId;
                    if (source == null)
                    {
                        if (!TryBind(current))
                        {
                            if (Time.unscaledTime > deadline) Fail("expected three configured stock gates");
                            return;
                        }
                        source = current;
                        Write("arm", loaded ? "loaded" : "new");
                    }

                    if (!ReferenceEquals(current, source) || !TryReadOutputs(current, out var outputs)
                        || !signal.TryRead(current.Signal.id, out var signalJob)
                        || !pulse.TryRead(current.Pulse.id, out var pulseJob)
                        || !value.TryRead(current.Value.id, out var valueJob))
                    {
                        Fail("native source, state or gate binding changed");
                        return;
                    }

                    float signalState = signal.Command!.State;
                    float pulseState = pulse.Command!.State;
                    float valueState = value.Command!.State;
                    string Snapshot() => $"src={F(outputs.Signal)},{F(outputs.Pulse)},{F(outputs.Value)}"
                        + $";job={F(signalJob.State)},{F(pulseJob.State)},{F(valueJob.State)}"
                        + $";gate={F(signalState)},{F(pulseState)},{F(valueState)}";

                    if (phase == Phase.Baseline && outputs.Signal == 0f && outputs.Pulse == 0f && outputs.Value == 0f
                        && signalJob.State == 0f && pulseJob.State == 0f && valueJob.State == 0f)
                    {
                        Write("baseline", Snapshot());
                        if (!Publish(true, 7.25f) || !LogicOutputProvider.TryEmitPulse(context)
                            || LogicOutputProvider.TryEmitPulse(context))
                        {
                            Fail("first pulse failed or duplicate pulse was accepted");
                            return;
                        }
                        MoveTo(Phase.High);
                    }
                    else if (phase == Phase.High && outputs.Signal == 1f && outputs.Pulse == 1f && outputs.Value == 7.25f
                        && signalJob.State == 1f && pulseJob.State == 1f && valueJob.State == 1f
                        && signalState == 0f && pulseState == 1f && valueState == 1f)
                    {
                        Write("high", Snapshot());
                        MoveTo(Phase.PulseLow);
                    }
                    else if (phase == Phase.PulseLow && outputs.Signal == 1f && outputs.Pulse == 1f && outputs.Value == 7.25f
                        && signalJob.State == 1f && pulseJob.State == 1f && valueJob.State == 1f
                        && signalState == 0f && pulseState == 0f && valueState == 1f)
                    {
                        Write("pulse-low", Snapshot());
                        if (!Publish(false, 2.5f)) { Fail("low publish failed"); return; }
                        MoveTo(Phase.Low);
                    }
                    else if (phase == Phase.Low && outputs.Signal == 0f && outputs.Pulse == 1f && outputs.Value == 2.5f
                        && signalJob.State == 0f && pulseJob.State == 1f && valueJob.State == 0f
                        && signalState == 1f && pulseState == 0f && valueState == 0f)
                    {
                        Write("low", Snapshot());
                        if (!Publish(true, 7.25f)) { Fail("held high publish failed"); return; }
                        MoveTo(Phase.HeldHigh);
                    }
                    else if (phase == Phase.HeldHigh && outputs.Signal == 1f && outputs.Pulse == 1f && outputs.Value == 7.25f
                        && signalJob.State == 1f && pulseJob.State == 1f && valueJob.State == 1f
                        && signalState == 0f && pulseState == 0f && valueState == 1f)
                    {
                        Write("held-high", Snapshot());
                        Write("pass", "remove source while high to inspect reset");
                        phase = Phase.Passed;
                        outcome(current, true);
                    }
                    else if (Time.unscaledTime > deadline)
                        Fail("timed out in " + phase + " " + Snapshot());
                }
                catch (Exception exception)
                {
                    Fail(exception.GetType().Name + ": " + exception.Message);
                }
            }

            private bool TryBind(KeelLogicOutputCommand output)
            {
                if (!LogicOutputProvider.IsAvailable || output.targets.Count != 3
                    || !TryReadOutputs(output, out _) || !CommandEventGateResources.Datas.IsCreated) return false;
                signal = pulse = value = default;
                foreach (var link in output.targets.Items)
                {
                    if (link.Command is not CommandEventGate gate || gate.IsRemoved
                        || gate.brickCoord.entityID != output.brickCoord.entityID
                        || gate.predecessors.Count != 1 || !ReferenceEquals(gate.predecessors.Command, output))
                        return false;
                    for (int index = 1; index < CommandEventGateResources.Datas.Length; index++)
                    {
                        var job = CommandEventGateResources.Datas[index];
                        if (job.commandId != gate.commandId || job.hotkeyGroupId == -1) continue;
                        var binding = new Gate(gate, index);
                        if (job.stateDataId == output.Signal.id && job.stateType == CommandStateData.StateType.signal
                            && job.signalType == CommandEventGate.SignalType.toggle && signal.Command == null)
                            signal = binding;
                        else if (job.stateDataId == output.Pulse.id && job.stateType == CommandStateData.StateType.pulse
                            && pulse.Command == null)
                            pulse = binding;
                        else if (job.stateDataId == output.Value.id && job.stateType == CommandStateData.StateType.value
                            && job.comparator == CommandEventGate.Comparator.isSuperior && job.valueThreshold == 5f
                            && value.Command == null)
                            value = binding;
                        else
                            return false;
                        break;
                    }
                }
                return signal.Command != null && pulse.Command != null && value.Command != null;
            }

            private bool Publish(bool level, float number)
                => LogicOutputProvider.TrySetSignal(context, level) && LogicOutputProvider.TrySetValue(context, number);

            private void MoveTo(Phase next)
            {
                phase = next;
                deadline = Time.unscaledTime + 5f;
            }

            private void Fail(string detail)
            {
                Write("fail", detail.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' '));
                phase = Phase.Failed;
                if (source != null) outcome(source, false);
            }

            private void Write(string name, string detail)
                => logger.LogInfo($"KEEL_LOGIC_OUTPUT|{session}|{sourceId}|{name}|{detail}");

            private static bool CanInspect()
                => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                    && PlayerState.sessionMode == PlayerState.SessionMode.solo && !PlayerState.InLoadingScreen
                    && !PlayerState.IsQuittingToDesktopOrTitle;

            private static bool TryReadOutputs(KeelLogicOutputCommand output, out (float Signal, float Pulse, float Value) values)
            {
                values = default;
                if (!CommandStateData.stateDatas.IsCreated) return false;
                int count = CommandStateData.stateDatas.Length;
                if (output.Signal.id <= 0 || output.Signal.id >= count || output.Pulse.id <= 0 || output.Pulse.id >= count
                    || output.Value.id <= 0 || output.Value.id >= count) return false;
                values = (output.Signal.Value, output.Pulse.Value, output.Value.Value);
                return true;
            }

            private static string F(float number) => number.ToString("G9", CultureInfo.InvariantCulture);

            private readonly struct Gate
            {
                internal Gate(CommandEventGate command, int index)
                {
                    Command = command;
                    this.index = index;
                }

                internal CommandEventGate? Command { get; }
                private readonly int index;

                internal bool TryRead(int stateDataId, out CommandEventGateResources.EventGateJobData data)
                {
                    data = default;
                    if (Command == null || Command.IsRemoved || !CommandEventGateResources.Datas.IsCreated
                        || index <= 0 || index >= CommandEventGateResources.Datas.Length) return false;
                    var current = CommandEventGateResources.Datas[index];
                    if (current.commandId != Command.commandId || current.stateDataId != stateDataId) return false;
                    data = current;
                    return true;
                }
            }
        }
    }
}
