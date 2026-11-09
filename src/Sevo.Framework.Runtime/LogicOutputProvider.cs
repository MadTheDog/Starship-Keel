using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Publishes native Event Gate outputs from opted-in functional blocks.</summary>
    public static class LogicOutputProvider
    {
        internal const int SignalEventId = 125;
        internal const int PulseEventId = 126;
        internal const int ValueEventId = 127;
        private static readonly HashSet<string> Declarations = new HashSet<string>(StringComparer.Ordinal);
        private static OutputField[]? registered;
        private static AccessTools.FieldRef<CommandEventGate, int>? gateJobId;
        private static bool enabled;
        private static bool declarationsSealed;

        public static bool IsAvailable => enabled && registered != null;

        /// <summary>Declares output channels for the same namespaced key as a functional block. Call in plugin Awake.</summary>
        public static void Declare(string ownerId, string localName)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            lock (Declarations)
            {
                if (declarationsSealed || !Declarations.Add(key))
                    throw new InvalidOperationException("Logic output declaration is late or already registered: " + key);
            }
        }

        internal static bool UsesOutputs(string key)
        {
            lock (Declarations)
            {
                declarationsSealed = true;
                return IsAvailable && Declarations.Contains(key);
            }
        }

        internal static void Enable()
        {
            var type = typeof(KeelLogicOutputCommand);
            var fields = new[]
            {
                Bind(nameof(KeelLogicOutputCommand.Signal), SignalEventId),
                Bind(nameof(KeelLogicOutputCommand.Pulse), PulseEventId),
                Bind(nameof(KeelLogicOutputCommand.Value), ValueEventId)
            };
            var infos = CommandEventGateResources.FieldInfos;
            var labels = CommandEventGateResources.CommandStateDataFields;
            var types = CommandEventGateResources.TypeFields;
            if (types.ContainsKey(type) || ValueEventId >= infos.Length || ValueEventId >= labels.Length)
                throw new InvalidOperationException("Native Event Gate tables cannot hold Keel's stable output slots.");

            var ownFields = new List<FieldInfo>(fields.Length);
            foreach (var output in fields)
            {
                if (infos[output.Id] != null || labels[output.Id] != null)
                    throw new InvalidOperationException("A native Event Gate slot is occupied: " + output.Id);
                ownFields.Add(output.Field);
            }

            gateJobId = AccessTools.FieldRefAccess<CommandEventGate, int>("jobDataId");

            // Stock only scans the game assembly. Append exactly our managed type and three vacant records.
            types.Add(type, ownFields);
            foreach (var output in fields)
            {
                infos[output.Id] = output.Field;
                labels[output.Id] = output.Label;
            }
            registered = fields;
            enabled = true;

            OutputField Bind(string name, int id)
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                var label = field?.GetCustomAttribute<CommandStateData.CommandStateDataField>();
                if (field?.FieldType != typeof(CommandStateData) || label == null || label.eventId != id)
                    throw new InvalidOperationException("Keel's saved output schema is invalid: " + name);
                return new OutputField(id, field, label);
            }
        }

        // Leave owned metadata in place until process teardown so existing gate references remain valid.
        internal static void Disable() => enabled = false;

        internal static bool IsSafePhase => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle;

        public static bool TrySetSignal(FunctionalBlockContext context, bool active)
        {
            var output = Resolve(context);
            if (output == null) return false;
            output.Signal.Set(active ? 1f : 0f);
            return true;
        }

        public static bool TrySetValue(FunctionalBlockContext context, float value)
        {
            if (!float.IsFinite(value)) return false;
            var output = Resolve(context);
            if (output == null) return false;
            output.Value.Set(value);
            return true;
        }

        public static bool TryEmitPulse(FunctionalBlockContext context)
            => Resolve(context)?.TryPulse() == true;

        private static KeelLogicOutputCommand? Resolve(FunctionalBlockContext context)
            => IsAvailable && IsSafePhase && context?.LogicOutput is { HasOutputs: true } output ? output : null;

        internal static void Reset(Command command)
        {
            if (command is KeelLogicOutputCommand output && IsSafePhase && output.HasOutputs)
            {
                output.Signal.Set(0f);
                output.Value.Set(0f);
                // A native pulse is an edge bit. Changing it here would emit another pulse.
            }
        }

        internal static void DetachGate(KeelLogicOutputCommand source, CommandEventGate gate)
        {
            if (!IsSafePhase || gateJobId == null || gate.IsRemoved || gate.predecessors.Count != 0
                || !CommandEventGateResources.Datas.IsCreated)
                return;

            int index = gateJobId(gate);
            if (index < 0 || index >= CommandEventGateResources.Datas.Length) return;
            var job = CommandEventGateResources.Datas[index];
            int stateId = job.stateDataId;
            if (job.commandId != gate.commandId || stateId <= 0
                || (stateId != source.Signal.id && stateId != source.Pulse.id && stateId != source.Value.id))
                return;

            // Stock SetStateData unbinds the gate but retains its old job record when no predecessor remains.
            // Clear the owned binding before the old state slot can be reused.
            CommandEventGateResources.Datas[index] = default;
        }

        private sealed class OutputField
        {
            internal int Id { get; }
            internal FieldInfo Field { get; }
            internal CommandStateData.CommandStateDataField Label { get; }

            internal OutputField(int id, FieldInfo field, CommandStateData.CommandStateDataField label)
            {
                Id = id;
                Field = field;
                Label = label;
            }
        }
    }

    /// <summary>The native command backing a declared logic output block.</summary>
    public sealed class KeelLogicOutputCommand : KeelFunctionalCommand, ICommandEventStateProvider
    {
        [CommandStateData.CommandStateDataField("Keel Signal", (EventId)LogicOutputProvider.SignalEventId)]
        public CommandStateData Signal;

        [CommandStateData.CommandStateDataField("Keel Pulse", (EventId)LogicOutputProvider.PulseEventId)]
        public CommandStateData Pulse;

        [CommandStateData.CommandStateDataField("Keel Value", (EventId)LogicOutputProvider.ValueEventId)]
        public CommandStateData Value;

        private int lastPulseFrame = -1;
        public EventTextureId EventTextureId => default;
        public override bool CanBeLinkSource => LogicOutputProvider.IsAvailable && FunctionalCommandState.IsSolo && HasOutputs;
        public override bool CanLinkToCommand(ICommand target) => CanBeLinkSource && target is CommandEventGate;
        internal bool HasOutputs => IsLive(Signal) && IsLive(Pulse) && IsLive(Value);

        protected override void OnInitialize(BrickIds ids, bool onLoad, object[] args)
        {
            ReleaseOutputs();
            base.OnInitialize(ids, onLoad, args);
            lastPulseFrame = -1;
            if (!LogicOutputProvider.IsAvailable || !FunctionalCommandState.IsSolo || !CommandStateData.stateDatas.IsCreated)
                return;
            try
            {
                Signal.Register(CommandStateData.StateType.signal);
                Pulse.Register(CommandStateData.StateType.pulse);
                Value.Register(CommandStateData.StateType.value);
            }
            catch
            {
                ReleaseOutputs();
                throw;
            }
        }

        internal bool TryPulse()
        {
            if (Time.frameCount == lastPulseFrame) return false;
            Pulse.EmitPulse();
            lastPulseFrame = Time.frameCount;
            return true;
        }

        public override void OnTargetRemoved(ICommand target, bool onUnload)
        {
            base.OnTargetRemoved(target, onUnload);
            if (!onUnload && target is CommandEventGate gate)
                LogicOutputProvider.DetachGate(this, gate);
        }

        public override void OnDestroy(BrickUnspawnMode mode)
        {
            try { base.OnDestroy(mode); }
            finally { ReleaseOutputs(); }
        }

        public override void ReleaseResources()
        {
            try { base.ReleaseResources(); }
            finally { ReleaseOutputs(); }
        }

        private void ReleaseOutputs()
        {
            Release(ref Signal);
            Release(ref Pulse);
            Release(ref Value);
        }

        private static bool IsLive(CommandStateData output)
            => output.id > 0 && CommandStateData.stateDatas.IsCreated && output.id < CommandStateData.stateDatas.Length;

        private static void Release(ref CommandStateData output)
        {
            if (IsLive(output)) output.Release();
            output = default;
        }
    }
}
