using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct ContextActionContext
    {
        public BrickCoord Placement { get; }
        internal VoxelEntity Entity { get; }
        internal PlayerCommandManager Player { get; }
        internal int CommandId { get; }
        internal int Frame { get; }

        internal ContextActionContext(BrickCoord placement, VoxelEntity entity, PlayerCommandManager player, int commandId, int frame)
        {
            Placement = placement;
            Entity = entity;
            Player = player;
            CommandId = commandId;
            Frame = frame;
        }

        internal bool Matches(ContextActionContext other)
            => Frame == other.Frame && ReferenceEquals(Entity, other.Entity) && ReferenceEquals(Player, other.Player)
                && Placement.entityID.Equals(other.Placement.entityID)
                && Placement.brickData.instanceId == other.Placement.brickData.instanceId
                && Placement.brickData == other.Placement.brickData && CommandId == other.CommandId;
    }

    public sealed class ContextAction
    {
        public string Key { get; }
        public string Label { get; }
        public ContextActionContext Context { get; }

        internal ContextAction(string key, string label, ContextActionContext context)
        {
            Key = key;
            Label = label;
            Context = context;
        }
    }

    /// <summary>Supplies opt-in actions for the native interaction target. Mods own their controls and presentation.</summary>
    public static class ContextActionProvider
    {
        private static readonly SortedDictionary<string, Provider> Providers = new SortedDictionary<string, Provider>(StringComparer.Ordinal);
        private static Action<string>? report;
        private static int gameThread;
        private static int observedFrame = -1;
        private static VoxelRaycastHit observedHit;
        private static bool dispatching;

        /// <summary>Register on the game thread during the dependent plugin's Awake.</summary>
        public static void Register(string ownerId, string localName, string label,
            Func<ContextActionContext, bool> canInvoke, Action<ContextActionContext> invoke)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A label is required.", nameof(label));
            if (canInvoke == null) throw new ArgumentNullException(nameof(canInvoke));
            if (invoke == null) throw new ArgumentNullException(nameof(invoke));
            Providers.Add(key, new Provider(label, canInvoke, invoke));
        }

        public static IReadOnlyList<ContextAction> GetAvailableActions()
        {
            if (dispatching || !TryGetCurrent(out var context, out _)) return Array.Empty<ContextAction>();
            dispatching = true;
            try
            {
                var actions = new List<ContextAction>();
                foreach (var entry in Providers.ToArray())
                    if (CanInvoke(entry.Key, entry.Value, context))
                        actions.Add(new ContextAction(entry.Key, entry.Value.Label, context));
                return actions.AsReadOnly();
            }
            finally { dispatching = false; }
        }

        public static bool TryInvoke(ContextAction action)
        {
            if (action == null || dispatching || !TryGetCurrent(out var current, out _) || !current.Matches(action.Context)
                || !Providers.TryGetValue(action.Key, out var provider)) return false;
            dispatching = true;
            try
            {
                if (!CanInvoke(action.Key, provider, current) || !TryGetCurrent(out var confirmed, out _)
                    || !confirmed.Matches(current)) return false;
                provider.Invoke(confirmed);
                return true;
            }
            catch (Exception exception)
            {
                report?.Invoke("Context action " + action.Key + " failed: " + exception.Message);
                return false;
            }
            finally { dispatching = false; }
        }

        internal static void Install(Harmony harmony, Action<string> log)
        {
            var target = AccessTools.Method(typeof(ActionActivate), nameof(ActionActivate.TryActivate), new[] { typeof(VoxelRaycastHit) });
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ContextActionProvider), nameof(ObserveTarget)));
            gameThread = Thread.CurrentThread.ManagedThreadId;
            report = log;
        }

        internal static void Disable()
        {
            gameThread = 0;
            observedFrame = -1;
            observedHit = default;
            report = null;
            Providers.Clear();
        }

        private static void ObserveTarget(VoxelRaycastHit __0)
        {
            if (gameThread != Thread.CurrentThread.ManagedThreadId) return;
            observedHit = __0;
            observedFrame = Time.frameCount;
        }

        internal static bool TryGetCurrent(out ContextActionContext context, out ICommandManuallyActivable? command)
        {
            context = default;
            command = null;
            if (gameThread != Thread.CurrentThread.ManagedThreadId || !FunctionalCommandState.IsSolo
                || GameloopManager.IsNativeContainerAccessUnsafe || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || PlayerState.InMenu || PlayerState.BlockActivate
                || Time.frameCount != observedFrame || !observedHit.isHitBrick
                || !float.IsFinite(observedHit.distance) || observedHit.distance < 0f || observedHit.distance > ActionActivateRange)
                return false;

            var player = Dependency.PlayerManager;
            var entity = observedHit.Entity;
            var parent = entity == null ? null : entity.Parent;
            if (ReferenceEquals(player, null) || !CommandCommon.IsLocalPlayer(player) || entity == null || parent == null
                || !parent.IsInitialized || parent.WillBeUnspawned || entity.Bricks == null
                || ActionActivate.CommandOver.Value is not ICommandManuallyActivable observed)
                return false;

            var hit = observedHit.brickCoord;
            var expected = observed.brickCoord;
            var id = hit.brickData.instanceId;
            if (id <= 0 || id >= entity.Bricks.brickIds.Length || expected.entity != entity
                || expected.brickData.instanceId != id || expected.brickData != hit.brickData)
                return false;

            var live = entity.Bricks.GetBrickData(id);
            if (!live.IsValid || live != hit.brickData || live.HealthFactor <= 0f)
                return false;
            var placement = new BrickCoord(hit.entityID, live);
            if (placement.Command is not ICommandManuallyActivable liveCommand
                || liveCommand.GetType() != observed.GetType() || !liveCommand.CanActivateManually(player))
                return false;

            command = liveCommand;
            context = new ContextActionContext(placement, entity, player, placement.BrickIds.commandId, observedFrame);
            return true;
        }

        private const float ActionActivateRange = 5f;

        private static bool CanInvoke(string key, Provider provider, ContextActionContext context)
        {
            try { return provider.CanInvoke(context); }
            catch (Exception exception)
            {
                report?.Invoke("Context action " + key + " predicate failed: " + exception.Message);
                return false;
            }
        }

        private sealed class Provider
        {
            internal string Label { get; }
            internal Func<ContextActionContext, bool> CanInvoke { get; }
            internal Action<ContextActionContext> Invoke { get; }

            internal Provider(string label, Func<ContextActionContext, bool> canInvoke, Action<ContextActionContext> invoke)
            {
                Label = label;
                CanInvoke = canInvoke;
                Invoke = invoke;
            }
        }
    }
}
