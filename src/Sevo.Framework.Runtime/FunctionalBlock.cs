using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A single live native placement. Keep the context only for this placement's lifetime.</summary>
    public sealed class FunctionalBlockContext
    {
        private FunctionalCommandState? state;

        internal FunctionalBlockContext(FunctionalCommandState state, FunctionalBinding binding)
        {
            this.state = state;
            Key = binding.Key;
            ContentId = binding.Id;
        }

        public string Key { get; }
        public int ContentId { get; }

        /// <summary>Gets the current native placement on the game thread while it remains usable in solo play.</summary>
        public bool TryGetPlacement(out BrickCoord placement)
        {
            if (state?.CanRun == true)
            {
                placement = state.Command.brickCoord;
                return true;
            }
            placement = default;
            return false;
        }

        /// <summary>Reads the current native weapon side slots for this placement on the game thread.</summary>
        public IReadOnlyList<WeaponAttachmentSlot> GetWeaponAttachmentSlots() =>
            state?.GetAttachmentSlots() ?? Array.Empty<WeaponAttachmentSlot>();

        internal KeelLogicOutputCommand? LogicOutput => state?.CanRun == true ? state.Command as KeelLogicOutputCommand : null;

        internal void Invalidate() => state = null;
    }

    internal sealed class FunctionalBinding
    {
        public FunctionalBinding(string key, int id, Func<FunctionalBlockContext, FunctionalBlockBehavior> factory,
            IReadOnlyCollection<string>? compatibleAttachmentKeys = null)
        {
            Key = key;
            Id = id;
            Factory = factory;
            CompatibleAttachmentKeys = compatibleAttachmentKeys;
        }

        public string Key { get; }
        public int Id { get; }
        public Func<FunctionalBlockContext, FunctionalBlockBehavior> Factory { get; }
        public IReadOnlyCollection<string>? CompatibleAttachmentKeys { get; }
    }

    internal interface IFunctionalCommand
    {
        FunctionalCommandState FunctionalState { get; }
    }

    internal static class WeaponAttachmentAvailability
    {
        internal static bool Enabled { get; set; }
    }

    /// <summary>Native command implementation constructed by the game. Register through ContentRegistration.</summary>
    public class KeelFunctionalCommand : Command, ICommandOnEntityInitializedCallback, IFunctionalCommand
    {
        internal static bool Enabled { get; set; }
        private FunctionalCommandState FunctionalState { get; }
        FunctionalCommandState IFunctionalCommand.FunctionalState => FunctionalState;

        public KeelFunctionalCommand() => FunctionalState = new FunctionalCommandState(this);
        public override bool CanBeLinkSource => false;
        public override bool CanBeLinkTarget => true;
        public override bool AllowMultipleInputs => false;
        public override bool CanActivateManually(PlayerCommandManager playerManager) => false;
        public override bool CanBeLinkTargetFromCommand(ICommand predecessor) =>
            FunctionalCommandState.SupportsSource(predecessor);

        protected override void OnInitialize(BrickIds ids, bool onLoad, object[] args)
        {
            FunctionalState.Stop();
            base.OnInitialize(ids, onLoad, args);
            FunctionalState.Initialize(onLoad, args);
        }

        public void OnEntityInitialized(bool onLoad) => FunctionalState.Start();

        // Native Activate also carries pulses and manual actions. Source hooks distinguish held levels.
        public override void Activate(bool manualActivation, PlayerCommandManager playerManager, float state) { }

        public override void OnPredecessorAdded(ICommand predecessor, bool onLoad, bool onInit)
        {
            base.OnPredecessorAdded(predecessor, onLoad, onInit);
            FunctionalState.PredecessorAdded(predecessor, onLoad, onInit);
        }

        public override void OnPredecessorRemoved(ICommand predecessor)
        {
            base.OnPredecessorRemoved(predecessor);
            FunctionalState.PredecessorRemoved();
        }

        internal static void BeforeSwitch(CommandSwitch __instance, out float __state) => __state = __instance.State;

        internal static void InstallSwitchHook(Harmony harmony, Assembly game)
        {
            const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Static;
            var activate=game.GetType("VoxelEngine.CommandSwitch",true)!.GetMethod("Activate",
                BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,
                new[] { typeof(bool),typeof(PlayerCommandManager),typeof(float) },null)
                ?? throw new InvalidOperationException("Live stock switch activation method is missing.");
            harmony.Patch(activate,
                prefix:new HarmonyMethod(typeof(KeelFunctionalCommand).GetMethod(nameof(BeforeSwitch),flags)),
                postfix:new HarmonyMethod(typeof(KeelFunctionalCommand).GetMethod(nameof(AfterSwitch),flags)));
        }

        internal static void AfterSwitch(CommandSwitch __instance, float __state)
        {
            if (!Enabled || !FunctionalCommandState.IsSolo || __instance.GetType() != typeof(CommandSwitch)
                || __instance.State == __state || (__instance.State != 0f && __instance.State != 1f)) return;
            DeliverInput(__instance,__instance.State==1f);
        }

        internal static void DeliverInput(Command source,bool enabled,bool verifiedGateLevel=false)
        {
            if(!Enabled || !FunctionalCommandState.IsSolo || source.targets.Count==0) return;
            // Snapshot targets before calling a mod, which may change links during its callback.
            var sinks = new List<FunctionalCommandState>();
            foreach (var link in source.targets.Items)
                if (link.Command is IFunctionalCommand sink) sinks.Add(sink.FunctionalState);
            foreach (var sink in sinks) sink.SetInputFrom(source,enabled,verifiedGateLevel);
        }

        public override void OnDestroy(BrickUnspawnMode mode)
        {
            try { FunctionalState.Stop(); }
            finally { base.OnDestroy(mode); }
        }

        public override void ReleaseResources()
        {
            try { FunctionalState.Stop(); }
            finally { base.ReleaseResources(); }
        }

        internal static void StopAll() => FunctionalCommandState.StopAll();

        internal static void StopOutsideSolo()
        {
            if (!FunctionalCommandState.IsSolo) StopAll();
        }
    }

    internal sealed class FunctionalCommandState
    {
        private static readonly HashSet<FunctionalCommandState> Live = new HashSet<FunctionalCommandState>();
        private readonly Func<IReadOnlyList<WeaponAttachmentSlot>>? attachmentSlots;
        private FunctionalBlockContext? context;
        private FunctionalBlockLifetime<FunctionalBlockContext>? lifetime;
        private bool fromLoad;
        private bool gateWaitingForLow;

        internal FunctionalCommandState(Command command, Func<IReadOnlyList<WeaponAttachmentSlot>>? attachmentSlots = null)
        {
            Command = command;
            this.attachmentSlots = attachmentSlots;
        }

        internal Command Command { get; }
        internal FunctionalBinding? Binding { get; private set; }
        internal static bool IsSolo => PlayerState.sessionMode == PlayerState.SessionMode.solo;

        internal bool CanRun
        {
            get
            {
                if (!KeelFunctionalCommand.Enabled || Command.IsRemoved || !IsSolo ||
                    attachmentSlots != null && !WeaponAttachmentAvailability.Enabled) return false;
                var parent = Command.parentEntity;
                return parent != null && parent.IsInitialized && !parent.WillBeUnspawned;
            }
        }

        internal IReadOnlyList<WeaponAttachmentSlot> GetAttachmentSlots() =>
            attachmentSlots != null && CanRun ? attachmentSlots() : Array.Empty<WeaponAttachmentSlot>();

        internal static bool SupportsSource(ICommand predecessor) =>
            predecessor != null && (predecessor.GetType() == typeof(CommandSwitch) || FunctionalEventGate.IsSource(predecessor));

        internal void Initialize(bool onLoad, object[] args)
        {
            if (args?.Length != 1 || args[0] is not FunctionalBinding binding ||
                binding.Id != Command.brickCoord.brickData.brickId) return;
            Binding = binding;
            fromLoad = onLoad;
            gateWaitingForLow = onLoad;
        }

        internal void Start()
        {
            var definition = Binding;
            if (lifetime != null || definition == null || !CanRun) return;
            var placement = new FunctionalBlockContext(this, definition);
            context = placement;
            var created = new FunctionalBlockLifetime<FunctionalBlockContext>(placement, definition.Factory, exception =>
            {
                LogicOutputProvider.Reset(Command);
                placement.Invalidate();
                Plugin.ReportFunctionalFailure(definition.Key, exception);
            });
            lifetime = created;
            Live.Add(this);
            created.Start(fromLoad);
        }

        internal void PredecessorAdded(ICommand predecessor, bool onLoad, bool onInit)
        {
            if (onLoad || onInit) return;
            if (FunctionalEventGate.IsSource(predecessor)) gateWaitingForLow = false;
            if (predecessor is CommandSwitch source && source.GetType() == typeof(CommandSwitch))
                SetInputFrom(source, source.State == 1f);
        }

        internal void PredecessorRemoved() => lifetime?.SetSustainedInput(false);

        internal void SetInputFrom(Command source, bool enabled, bool verifiedGateLevel = false)
        {
            if (lifetime == null) return;
            if (!CanRun)
            {
                Stop();
                return;
            }
            var links = Command.predecessors;
            if (links.Count != 1 || !ReferenceEquals(links.Command, source) || source.parentEntity != Command.parentEntity) return;
            if (verifiedGateLevel && !source.IsRemoved && gateWaitingForLow)
            {
                if (enabled) return;
                gateWaitingForLow = false;
            }
            lifetime.SetSustainedInput(enabled && !source.IsRemoved);
        }

        internal void Stop()
        {
            LogicOutputProvider.Reset(Command);
            var existing = lifetime;
            lifetime = null;
            Binding = null;
            context?.Invalidate();
            context = null;
            Live.Remove(this);
            existing?.Stop();
        }

        internal static void StopAll()
        {
            foreach (var command in new List<FunctionalCommandState>(Live)) command.Stop();
        }
    }
}
