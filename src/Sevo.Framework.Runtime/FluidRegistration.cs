using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registration and post-job access for mod-defined fluids in managed solo profiles.</summary>
    public static class FluidRegistration
    {
        private const int FirstModId = 4096;
        private const int Capacity = 8192;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, (int Revision, FluidDefinition Definition)> Declarations =
            new Dictionary<string, (int, FluidDefinition)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Action<FluidTickContext>> Ticks =
            new Dictionary<string, Action<FluidTickContext>>(StringComparer.Ordinal);
        private static ProfileSession? profile;
        private static Action<string>? warn;
        private static bool sealedCatalog;
        private static bool enabled;
        private static int lastFrame = -1;
        private static Dictionary<string, (FuelCommon.FuelType Id, FluidDefinition Definition)> active =
            new Dictionary<string, (FuelCommon.FuelType, FluidDefinition)>(StringComparer.Ordinal);
        private static Dictionary<FuelCommon.FuelType, string> labels = new Dictionary<FuelCommon.FuelType, string>();

        /// <summary>Call from the dependent mod's Awake. Physical properties are available to mod code.</summary>
        public static void Register(string ownerId, string localName, int revision, FluidDefinition definition)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision < 1)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            lock (Gate)
            {
                if (sealedCatalog || Declarations.ContainsKey(key))
                    throw new InvalidOperationException("Late or duplicate fluid registration: " + key);
                Declarations.Add(key, (revision, definition));
            }
        }

        /// <summary>Runs after the native frame's jobs in solo gameplay. The callback context is temporary.</summary>
        public static void RegisterTick(string ownerId, string localName, Action<FluidTickContext> callback)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            lock (Gate)
            {
                if (sealedCatalog || Ticks.ContainsKey(key))
                    throw new InvalidOperationException("Late or duplicate fluid tick registration: " + key);
                Ticks.Add(key, callback);
            }
        }

        public static bool IsAvailable => enabled && sealedCatalog;

        public static bool TryGetDefinition(string key, out FluidDefinition? definition)
        {
            if (IsAvailable && active.TryGetValue(key, out var value))
            {
                definition = value.Definition;
                return true;
            }
            definition = null;
            return false;
        }

        internal static bool TryResolve(string key, out FuelCommon.FuelType fluid)
        {
            if (IsAvailable && active.TryGetValue(key, out var value))
            {
                fluid = value.Id;
                return true;
            }
            fluid = default;
            return false;
        }

        internal static void Install(Harmony harmony, Assembly game, ProfileSession session, Action<string> warning)
        {
            const BindingFlags targets = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            const BindingFlags hooks = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo Target(string type, string name, params Type[] arguments) =>
                game.GetType(type, true)!.GetMethod(name, targets, null, arguments, null)
                ?? throw new InvalidOperationException("Missing live fluid hook: " + type + "." + name);
            HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(FluidRegistration).GetMethod(name, hooks));

            var tick = Target("Manager.GameloopManager", "ExecuteCommandsJob");
            var label = Target("VoxelEngine.FuelCommon", "GetFuelLabel", typeof(FuelCommon.FuelType), typeof(bool), typeof(bool));
            var config = Target("VoxelEngine.StorageCommandData", "OpenConfigWindow");
            harmony.Patch(label, postfix: Hook(nameof(AfterLabel)));
            harmony.Patch(config, prefix: Hook(nameof(BeforeTankOptions)));
            harmony.Patch(tick, postfix: Hook(nameof(AfterCommands)));
            profile = session;
            warn = warning;
            enabled = true;
        }

        internal static void Disable() => enabled = false;

        private static void Seal()
        {
            lock (Gate)
            {
                var session = profile ?? throw new InvalidOperationException("No managed fluid profile.");
                var stockIds = ((FuelCommon.FuelType[])Enum.GetValues(typeof(FuelCommon.FuelType)))
                    .Select(value => (int)value).Where(value => value >= 0 && value < Capacity).ToArray();
                var spaces = new[] { new ResourceSpace(ResourceKind.Fluid, Capacity, Enumerable.Range(0, FirstModId), stockIds) };
                string? json = session.ReadFeatureRegistry(ResourceKind.Fluid);
                var conflict = json == null ? null : RegistryJson.Read(json).Mappings
                    .FirstOrDefault(mapping => stockIds.Contains(mapping.Id));
                if (conflict != null)
                    throw new InvalidOperationException("Native fluid occupies persisted ID " + conflict.Id + " for " + conflict.Key + ".");
                var allocator = json == null
                    ? new ContentAllocator(spaces, session.ProfileId)
                    : ContentAllocator.FromJson(json, spaces);
                if (allocator.Lineage != session.ProfileId)
                    throw new InvalidOperationException("Fluid registry belongs to another profile.");

                var ids = allocator.Apply(Declarations.Select(pair => new ContentRequest(pair.Key, ResourceKind.Fluid, pair.Value.Revision)));
                string updated = allocator.ToJson();
                var next = ids.ToDictionary(pair => pair.Key,
                    pair => ((FuelCommon.FuelType)pair.Value, Declarations[pair.Key].Definition), StringComparer.Ordinal);
                var nextLabels = RegistryJson.Read(updated).Mappings.ToDictionary(mapping => (FuelCommon.FuelType)mapping.Id,
                    mapping => mapping.Tombstone ? "Unavailable mod fluid (" + mapping.Key + ")" : next[mapping.Key].Definition.DisplayName);
                if (updated != json && (json != null || ids.Count != 0))
                    session.WriteFeatureRegistry(ResourceKind.Fluid, updated);
                active = next;
                labels = nextLabels;
                sealedCatalog = true;
            }
        }

        private static void AfterLabel(FuelCommon.FuelType __0, ref string __result)
        {
            if (labels.TryGetValue(__0, out var label))
                __result = label;
        }

        private static bool BeforeTankOptions(FuelCommon.FuelType ___fuelType)
        {
            // The stock dropdown indexes four built-in choices. A mod owns controls for a bound tank.
            return !labels.ContainsKey(___fuelType);
        }

        private static void AfterCommands()
        {
            if (!enabled || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || Time.frameCount == lastFrame)
                return;
            lastFrame = Time.frameCount;
            if (!sealedCatalog)
            {
                try { Seal(); }
                catch (Exception exception)
                {
                    Disable();
                    warn?.Invoke("Fluid hooks unavailable: " + exception.Message);
                    return;
                }
            }

            float deltaTime = Time.deltaTime;
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
                return;
            // Registration is closed before the first tick. A failing callback is removed after enumeration.
            List<string>? failed = null;
            foreach (var pair in Ticks)
            {
                var context = new FluidTickContext(deltaTime);
                try { pair.Value(context); }
                catch (Exception exception)
                {
                    (failed ??= new List<string>()).Add(pair.Key);
                    warn?.Invoke("Stopped fluid tick " + pair.Key + ": " + exception);
                }
                finally { context.Close(); }
            }
            if (failed != null)
                foreach (var key in failed)
                    Ticks.Remove(key);
        }
    }
}
