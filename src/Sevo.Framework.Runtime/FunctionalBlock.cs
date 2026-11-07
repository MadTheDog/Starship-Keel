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
        private KeelFunctionalCommand? command;

        internal FunctionalBlockContext(KeelFunctionalCommand command, FunctionalBinding binding)
        {
            this.command = command;
            Key = binding.Key;
            ContentId = binding.Id;
        }

        public string Key { get; }
        public int ContentId { get; }

        /// <summary>Gets the current native placement on the game thread while it remains usable in solo play.</summary>
        public bool TryGetPlacement(out BrickCoord placement)
        {
            if (command?.CanRun == true)
            {
                placement = command.brickCoord;
                return true;
            }
            placement = default;
            return false;
        }

        internal void Invalidate() => command = null;
    }

    internal sealed class FunctionalBinding
    {
        public FunctionalBinding(string key, int id, Func<FunctionalBlockContext, FunctionalBlockBehavior> factory)
        {
            Key = key;
            Id = id;
            Factory = factory;
        }

        public string Key { get; }
        public int Id { get; }
        public Func<FunctionalBlockContext, FunctionalBlockBehavior> Factory { get; }
    }

    /// <summary>Native command implementation constructed by the game. Register through ContentRegistration.</summary>
    public sealed class KeelFunctionalCommand : Command, ICommandOnEntityInitializedCallback
    {
        internal static bool Enabled { get; set; }
        private static readonly HashSet<KeelFunctionalCommand> Live = new HashSet<KeelFunctionalCommand>();
        private FunctionalBinding? binding;
        private FunctionalBlockContext? context;
        private FunctionalBlockLifetime<FunctionalBlockContext>? lifetime;
        private bool fromLoad;
        private bool gateWaitingForLow;

        internal bool CanRun
        {
            get
            {
                if (!Enabled || IsRemoved || !IsSolo) return false;
                var parent = parentEntity;
                return parent != null && parent.IsInitialized && !parent.WillBeUnspawned;
            }
        }

        private static bool IsSolo => PlayerState.sessionMode == PlayerState.SessionMode.solo;
        public override bool CanBeLinkSource => false;
        public override bool CanBeLinkTarget => true;
        public override bool AllowMultipleInputs => false;
        public override bool CanActivateManually(PlayerCommandManager playerManager) => false;
        public override bool CanBeLinkTargetFromCommand(ICommand predecessor) =>
            predecessor != null && (predecessor.GetType() == typeof(CommandSwitch) || FunctionalEventGate.IsSource(predecessor));

        protected override void OnInitialize(BrickIds ids, bool onLoad, object[] args)
        {
            Stop();
            base.OnInitialize(ids, onLoad, args);
            if (args?.Length == 1 && args[0] is FunctionalBinding value && value.Id == brickCoord.brickData.brickId)
            {
                binding = value;
                fromLoad = onLoad;
                gateWaitingForLow = onLoad;
            }
        }

        public void OnEntityInitialized(bool onLoad)
        {
            var definition = binding;
            if (lifetime != null || definition == null || !CanRun) return;
            var placement = new FunctionalBlockContext(this, definition);
            context = placement;
            var created = new FunctionalBlockLifetime<FunctionalBlockContext>(placement, definition.Factory, exception =>
            {
                placement.Invalidate();
                Plugin.ReportFunctionalFailure(definition.Key, exception);
            });
            lifetime = created;
            Live.Add(this);
            created.Start(fromLoad);
        }

        // Native Activate also carries pulses and manual actions. Source hooks distinguish held levels.
        public override void Activate(bool manualActivation, PlayerCommandManager playerManager, float state) { }

        public override void OnPredecessorAdded(ICommand predecessor, bool onLoad, bool onInit)
        {
            base.OnPredecessorAdded(predecessor, onLoad, onInit);
            if(onLoad || onInit) return;
            if(FunctionalEventGate.IsSource(predecessor)) gateWaitingForLow=false;
            if(predecessor is CommandSwitch source && source.GetType()==typeof(CommandSwitch))
                SetInputFrom(source,source.State==1f);
        }

        public override void OnPredecessorRemoved(ICommand predecessor)
        {
            base.OnPredecessorRemoved(predecessor);
            lifetime?.SetSustainedInput(false);
        }

        private void SetInputFrom(Command source,bool enabled,bool verifiedGateLevel=false)
        {
            if (lifetime == null) return;
            if (!CanRun)
            {
                Stop();
                return;
            }
            var links = predecessors;
            if(links.Count!=1 || !ReferenceEquals(links.Command,source) || source.parentEntity!=parentEntity) return;
            if(verifiedGateLevel && !source.IsRemoved && gateWaitingForLow)
            {
                if(enabled) return;
                gateWaitingForLow=false;
            }
            lifetime.SetSustainedInput(enabled && !source.IsRemoved);
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
            if (!Enabled || !IsSolo || __instance.GetType() != typeof(CommandSwitch)
                || __instance.State == __state || (__instance.State != 0f && __instance.State != 1f)) return;
            DeliverInput(__instance,__instance.State==1f);
        }

        internal static void DeliverInput(Command source,bool enabled,bool verifiedGateLevel=false)
        {
            if(!Enabled || !IsSolo || source.targets.Count==0) return;
            // Snapshot targets before calling a mod, which may change links during its callback.
            var sinks = new List<KeelFunctionalCommand>();
            foreach (var link in source.targets.Items)
                if (link.Command is KeelFunctionalCommand sink) sinks.Add(sink);
            foreach (var sink in sinks) sink.SetInputFrom(source,enabled,verifiedGateLevel);
        }

        public override void OnDestroy(BrickUnspawnMode mode)
        {
            try { Stop(); }
            finally { base.OnDestroy(mode); }
        }

        public override void ReleaseResources()
        {
            try { Stop(); }
            finally { base.ReleaseResources(); }
        }

        private void Stop()
        {
            var existing = lifetime;
            lifetime = null;
            binding = null;
            context?.Invalidate();
            context = null;
            Live.Remove(this);
            existing?.Stop();
        }

        internal static void StopAll()
        {
            foreach (var command in new List<KeelFunctionalCommand>(Live)) command.Stop();
        }

        internal static void StopOutsideSolo()
        {
            if (Live.Count != 0 && !IsSolo) StopAll();
        }
    }
}
