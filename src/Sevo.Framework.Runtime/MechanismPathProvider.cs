using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct MechanismPathContext
    {
        public BrickCoord Placement { get; }
        public float TravelLength { get; }
        public int NegativeExtensions { get; }
        public int PositiveExtensions { get; }
        private readonly SliderSystem? slider;

        internal MechanismPathContext(BrickCoord placement, float travelLength, int negative = 0, int positive = 0,
            SliderSystem? slider = null)
            => (Placement, TravelLength, NegativeExtensions, PositiveExtensions, this.slider)
                = (placement, travelLength, negative, positive, slider);

        /// <summary>Reads the current stock straight extension chains and returns their slider-local centerline.</summary>
        public bool TryDiscoverStockStraightRail(out MechanismPath? path)
            => MechanismStockRail.TryDiscover(slider, out path);
    }

    /// <summary>The next bounded slider-local translation to clear for the entire carriage and its payload.</summary>
    public readonly struct MechanismRailSweep
    {
        internal MechanismRailSweep(MechanismPathContext slider, ChildrenEntity carriage,
            System.Numerics.Vector3 from, System.Numerics.Vector3 to)
            => (Slider, Carriage, FromOffset, ToOffset) = (slider, carriage, from, to);

        public MechanismPathContext Slider { get; }
        public ChildrenEntity Carriage { get; }
        public System.Numerics.Vector3 FromOffset { get; }
        public System.Numerics.Vector3 ToOffset { get; }
    }

    /// <summary>Maps opted-in native slider travel onto a fixed local path without changing carriage facing.</summary>
    public static class MechanismPathProvider
    {
        private const float LengthTolerance = 0.0005f;
        private static readonly SortedDictionary<string, Provider> Providers =
            new SortedDictionary<string, Provider>(StringComparer.Ordinal);
        private static ConditionalWeakTable<SliderSystem, Binding> bindings = new ConditionalWeakTable<SliderSystem, Binding>();
        private static Action<string>? report;
        private static int thread;
        private static bool catalogSealed;
        private static AccessTools.FieldRef<SliderSystem, int>? negativeExtensions;
        private static AccessTools.FieldRef<SliderSystem, int>? positiveExtensions;
        public static bool IsConnectedRailAvailable { get; private set; }
        public static bool IsStockRailToolsAvailable => IsConnectedRailAvailable && MechanismStockRail.IsAvailable;

        /// <summary>Discovers the current stock rail after its native add-on links are ready.</summary>
        public static bool TryDiscoverStockStraightRail(SliderSystem slider, out MechanismPath? path)
            => MechanismStockRail.TryDiscover(slider, out path);

        internal static void EnableConnectedRails()
        {
            var negative = AccessTools.FieldRefAccess<SliderSystem, int>("addonCountNeg");
            var positive = AccessTools.FieldRefAccess<SliderSystem, int>("addonCountPlus");
            negativeExtensions = negative;
            positiveExtensions = positive;
            IsConnectedRailAvailable = true;
        }

        internal static void DisableConnectedRails()
        {
            IsConnectedRailAvailable = false;
            negativeExtensions = positiveExtensions = null;
            MechanismStockRail.Disable();
        }

        public static void Register(string ownerId, string localName, Func<MechanismPathContext, MechanismPath?> selectPath,
            Func<MechanismPathContext, bool>? canMove = null)
            => AddProvider(ownerId, localName, selectPath, canMove, null);

        /// <summary>Registers a connected rail and a mandatory sweep callback for each proposed movement.</summary>
        /// <remarks>The callback must clear the whole payload over the supplied offsets and return false if uncertain.</remarks>
        public static void RegisterConnectedRail(string ownerId, string localName, Func<MechanismPathContext, MechanismPath?> selectPath,
            Func<MechanismRailSweep, bool> canSweep)
            => AddProvider(ownerId, localName, selectPath, null, canSweep ?? throw new ArgumentNullException(nameof(canSweep)));

        /// <summary>Registers a connected rail using Keel's conservative native collider sweep.</summary>
        public static void RegisterConnectedRail(string ownerId, string localName, Func<MechanismPathContext, MechanismPath?> selectPath)
            => AddProvider(ownerId, localName, selectPath, null, MechanismStockRail.CanSweep, usesStockTools: true);

        private static void AddProvider(string ownerId, string localName, Func<MechanismPathContext, MechanismPath?> selectPath,
            Func<MechanismPathContext, bool>? canMove, Func<MechanismRailSweep, bool>? canSweep, bool usesStockTools = false)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (selectPath == null) throw new ArgumentNullException(nameof(selectPath));
            if (catalogSealed) throw new InvalidOperationException("Register path providers during plugin Awake.");
            Providers.Add(key, new Provider(key, selectPath, canMove, canSweep, usesStockTools));
        }

        internal static void Install(Harmony harmony, Action<string> log)
        {
            var mechanism = typeof(ChildrenEntitySystem);
            var slider = typeof(SliderSystem);
            harmony.Patch(AccessTools.Method(mechanism, "InitializeChildrenEntity", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(MechanismPathProvider), nameof(BeforeInitialize)));
            harmony.Patch(AccessTools.Method(slider, "ApplyTransform", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(MechanismPathProvider), nameof(AfterApply)));
            harmony.Patch(AccessTools.Method(slider, "GetMoveSpeed", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(MechanismPathProvider), nameof(AfterGetMoveSpeed)));
            harmony.Patch(AccessTools.Method(mechanism, nameof(ChildrenEntitySystem.SetStateFromShipyard),
                new[] { typeof(float) }), prefix: new HarmonyMethod(typeof(MechanismPathProvider), nameof(BeforeInstantMove)));
            harmony.Patch(AccessTools.Method(slider, nameof(SliderSystem.OnDestroy), new[] { typeof(BrickUnspawnMode) }),
                postfix: new HarmonyMethod(typeof(MechanismPathProvider), nameof(AfterDestroy)));
            thread = Thread.CurrentThread.ManagedThreadId;
            report = log;
        }

        internal static void Disable()
        {
            thread = 0;
            report = null;
            bindings = new ConditionalWeakTable<SliderSystem, Binding>();
            Providers.Clear();
            catalogSealed = false;
            DisableConnectedRails();
        }

        private static void BeforeInitialize(ChildrenEntitySystem __instance)
        {
            if (thread != Thread.CurrentThread.ManagedThreadId || !FunctionalCommandState.IsSolo
                || __instance.GetType() != typeof(SliderSystem)) return;
            var slider = (SliderSystem)__instance;
            if (slider.IsRemoved || slider.brickCoord.entity == null || slider.ChildrenEntity == null
                || !float.IsFinite(slider.amplitude) || slider.amplitude <= 0f || bindings.TryGetValue(slider, out _))
                return;

            catalogSealed = true;
            var context = new MechanismPathContext(slider.brickCoord, slider.amplitude,
                negativeExtensions == null ? 0 : negativeExtensions(slider),
                positiveExtensions == null ? 0 : positiveExtensions(slider), slider);
            foreach (var provider in Providers.Values)
            {
                if (provider.CanSweep != null && !IsConnectedRailAvailable) continue;
                if (provider.UsesStockTools && !IsStockRailToolsAvailable) continue;
                try
                {
                    var path = provider.Select(context);
                    if (path == null) continue;
                    if (Math.Abs(path.Length - context.TravelLength) > LengthTolerance
                        || (provider.CanSweep != null && !path.IsConnectedRail))
                    {
                        report?.Invoke("Mechanism path " + provider.Key + " rejected: invalid rail or path length differs from stock travel.");
                        return;
                    }
                    bindings.Add(slider, new Binding(provider, path));
                    return;
                }
                catch (Exception exception)
                {
                    report?.Invoke("Mechanism path " + provider.Key + " failed: " + exception.GetBaseException().Message);
                    return;
                }
            }
        }

        private static void AfterApply(SliderSystem __instance, Vector3 ___iniPosition, float ___distance,
            int ___addonCountNeg, int ___addonCountPlus)
        {
            if (!bindings.TryGetValue(__instance, out var binding)) return;
            var child = __instance.ChildrenEntity;
            if (child == null) return;
            var stock = __instance.brickCoord.brickData;
            bool topologyMatches = binding.CheckTopology(___addonCountNeg, ___addonCountPlus, stock.GridSize, __instance.amplitude);
            float pathDistance = ___distance + binding.NegativeExtension;
            if (topologyMatches && float.IsFinite(pathDistance) && pathDistance >= -LengthTolerance
                && pathDistance <= binding.Path.Length + LengthTolerance
                && binding.Path.TryGetRailDisplacement(
                    Math.Max(0f, Math.Min(binding.Path.Length, pathDistance)) - binding.NegativeExtension,
                    binding.NegativeExtension, out var offset))
            {
                var displacement = __instance.brickCoord.brickData.Quaternion * new Vector3(offset.X, offset.Y, offset.Z);
                var position = ___iniPosition + displacement;
                if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
                {
                    binding.LastPosition = position;
                    binding.HasPosition = true;
                }
            }
            child.transform.localPosition = binding.HasPosition ? binding.LastPosition : ___iniPosition;
        }

        private static void AfterGetMoveSpeed(SliderSystem __instance, float ___distance, int ___addonCountNeg,
            int ___addonCountPlus, ref float __result)
        {
            if (!bindings.TryGetValue(__instance, out var binding)) return;
            float nativeSpeed = __result;
            __result = 0f;
            float targetState = __instance.State;
            float distance = ___distance + binding.NegativeExtension;
            if (thread != Thread.CurrentThread.ManagedThreadId || !FunctionalCommandState.IsSolo || __instance.IsRemoved
                || !binding.HasPosition || (binding.Provider.CanSweep == null && (___addonCountNeg != 0 || ___addonCountPlus != 0))
                || !binding.CheckTopology(___addonCountNeg, ___addonCountPlus, __instance.brickCoord.brickData.GridSize, __instance.amplitude)
                || !float.IsFinite(__instance.amplitude) || Math.Abs(__instance.amplitude - binding.Path.Length) > LengthTolerance
                || !float.IsFinite(distance) || distance < -LengthTolerance || distance > binding.Path.Length + LengthTolerance
                || !float.IsFinite(targetState) || targetState < 0f || targetState > 1f || !float.IsFinite(nativeSpeed)
                || nativeSpeed <= 0f)
                return;

            try
            {
                var context = new MechanismPathContext(__instance.brickCoord, __instance.amplitude);
                if (binding.Provider.CanMove?.Invoke(context) == false || __instance.IsRemoved) return;
            }
            catch (Exception exception)
            {
                report?.Invoke("Mechanism path " + binding.Provider.Key + " stopped: " + exception.GetBaseException().Message);
                return;
            }

            float deltaTime = Time.deltaTime;
            if (!float.IsFinite(deltaTime) || deltaTime < 0f) return;
            if (deltaTime == 0f)
            {
                if (binding.Provider.CanSweep == null) __result = nativeSpeed;
                return;
            }
            float maximum = Math.Min(0.05f, 0.25f * __instance.brickCoord.brickData.GridSize);
            float target = targetState * __instance.amplitude;
            if (binding.Provider.CanSweep == null)
            {
                float legacyStep = binding.Path.LimitStep(___distance, target, maximum);
                if (legacyStep > 0f) __result = Math.Min(nativeSpeed, legacyStep / (__instance.amplitude * deltaTime));
                return;
            }
            distance = Math.Max(0f, Math.Min(binding.Path.Length, distance));
            float step = Math.Min(Math.Abs(target - distance), binding.Path.LimitStep(distance, target, maximum));
            if (step <= 0f) return;
            float speed = Math.Min(nativeSpeed, step / (__instance.amplitude * deltaTime));
            float next = Math.Max(0f, Math.Min(binding.Path.Length,
                distance + Math.Sign(target - distance) * step));
            var carriage = __instance.ChildrenEntity;
            if (carriage == null
                || !binding.Path.TryGetRailDisplacement(distance - binding.NegativeExtension, binding.NegativeExtension, out var from)
                || !binding.Path.TryGetRailDisplacement(next - binding.NegativeExtension, binding.NegativeExtension, out var to))
                return;
            try
            {
                if (!binding.Provider.CanSweep(new MechanismRailSweep(
                    new MechanismPathContext(__instance.brickCoord, __instance.amplitude, ___addonCountNeg, ___addonCountPlus, __instance),
                    carriage, from, to))
                    || __instance.IsRemoved) return;
            }
            catch (Exception exception)
            {
                report?.Invoke("Connected rail " + binding.Provider.Key + " sweep failed: " + exception.GetBaseException().Message);
                return;
            }
            __result = speed;
        }

        private static bool BeforeInstantMove(ChildrenEntitySystem __instance)
            => __instance is not SliderSystem slider || !bindings.TryGetValue(slider, out _);

        private static void AfterDestroy(SliderSystem __instance) => bindings.Remove(__instance);

        private sealed class Provider
        {
            internal string Key { get; }
            internal Func<MechanismPathContext, MechanismPath?> Select { get; }
            internal Func<MechanismPathContext, bool>? CanMove { get; }
            internal Func<MechanismRailSweep, bool>? CanSweep { get; }
            internal bool UsesStockTools { get; }

            internal Provider(string key, Func<MechanismPathContext, MechanismPath?> select,
                Func<MechanismPathContext, bool>? canMove, Func<MechanismRailSweep, bool>? canSweep, bool usesStockTools)
            {
                Key = key;
                Select = select;
                CanMove = canMove;
                CanSweep = canSweep;
                UsesStockTools = usesStockTools;
            }
        }

        private sealed class Binding
        {
            internal Provider Provider { get; }
            internal MechanismPath Path { get; }
            internal Vector3 LastPosition { get; set; }
            internal bool HasPosition { get; set; }
            private (int Negative, int Positive, float GridSize)? topology;
            internal float NegativeExtension => topology is { } value ? value.Negative * value.GridSize : 0f;

            internal bool CheckTopology(int negative, int positive, float gridSize, float travel)
            {
                if (Provider.CanSweep == null) return true;
                if (negative < 0 || positive < 0 || !float.IsFinite(gridSize) || gridSize <= 0f
                    || !float.IsFinite(travel) || Math.Abs(travel - Path.Length) > LengthTolerance
                    || negative * gridSize > Path.Length)
                    return false;
                topology ??= (negative, positive, gridSize);
                return topology == (negative, positive, gridSize);
            }

            internal Binding(Provider provider, MechanismPath path)
            {
                Provider = provider;
                Path = path;
            }
        }
    }
}
