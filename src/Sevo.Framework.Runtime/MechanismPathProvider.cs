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

        internal MechanismPathContext(BrickCoord placement, float travelLength)
        {
            Placement = placement;
            TravelLength = travelLength;
        }
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

        public static void Register(string ownerId, string localName, Func<MechanismPathContext, MechanismPath?> selectPath,
            Func<MechanismPathContext, bool>? canMove = null)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (selectPath == null) throw new ArgumentNullException(nameof(selectPath));
            if (catalogSealed) throw new InvalidOperationException("Register path providers during plugin Awake.");
            Providers.Add(key, new Provider(key, selectPath, canMove));
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
            var context = new MechanismPathContext(slider.brickCoord, slider.amplitude);
            foreach (var provider in Providers.Values)
            {
                try
                {
                    var path = provider.Select(context);
                    if (path == null) continue;
                    if (Math.Abs(path.Length - context.TravelLength) > LengthTolerance)
                    {
                        report?.Invoke("Mechanism path " + provider.Key + " rejected: path length differs from stock travel.");
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

        private static void AfterApply(SliderSystem __instance, Vector3 ___iniPosition, float ___distance)
        {
            if (!bindings.TryGetValue(__instance, out var binding)) return;
            var child = __instance.ChildrenEntity;
            if (child == null) return;
            if (float.IsFinite(___distance) && ___distance >= -LengthTolerance && ___distance <= binding.Path.Length + LengthTolerance
                && binding.Path.TryGetOffset(Math.Max(0f, Math.Min(binding.Path.Length, ___distance)), out var offset))
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
            if (thread != Thread.CurrentThread.ManagedThreadId || !FunctionalCommandState.IsSolo || __instance.IsRemoved
                || !binding.HasPosition || ___addonCountNeg != 0 || ___addonCountPlus != 0
                || !float.IsFinite(__instance.amplitude) || Math.Abs(__instance.amplitude - binding.Path.Length) > LengthTolerance
                || !float.IsFinite(___distance) || ___distance < -LengthTolerance || ___distance > binding.Path.Length + LengthTolerance
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
                __result = nativeSpeed;
                return;
            }
            float maximum = Math.Min(0.05f, 0.25f * __instance.brickCoord.brickData.GridSize);
            float target = targetState * __instance.amplitude;
            float step = binding.Path.LimitStep(___distance, target, maximum);
            if (step > 0f) __result = Math.Min(nativeSpeed, step / (__instance.amplitude * deltaTime));
        }

        private static bool BeforeInstantMove(ChildrenEntitySystem __instance)
            => __instance is not SliderSystem slider || !bindings.TryGetValue(slider, out _);

        private static void AfterDestroy(SliderSystem __instance) => bindings.Remove(__instance);

        private sealed class Provider
        {
            internal string Key { get; }
            internal Func<MechanismPathContext, MechanismPath?> Select { get; }
            internal Func<MechanismPathContext, bool>? CanMove { get; }

            internal Provider(string key, Func<MechanismPathContext, MechanismPath?> select,
                Func<MechanismPathContext, bool>? canMove)
            {
                Key = key;
                Select = select;
                CanMove = canMove;
            }
        }

        private sealed class Binding
        {
            internal Provider Provider { get; }
            internal MechanismPath Path { get; }
            internal Vector3 LastPosition { get; set; }
            internal bool HasPosition { get; set; }

            internal Binding(Provider provider, MechanismPath path)
            {
                Provider = provider;
                Path = path;
            }
        }
    }
}
