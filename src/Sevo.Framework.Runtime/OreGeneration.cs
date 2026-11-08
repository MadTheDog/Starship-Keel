using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Common.RNG;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers deterministic overrides of existing native ore slots.</summary>
    public static class OreGeneration
    {
        public const int SlotCount = ContractAudit.NativeOreSlotCount;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, OreGenerationRule> Rules = new Dictionary<string, OreGenerationRule>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static Lazy<(OreGenerationPlan Planets, OreGenerationPlan Asteroids)>? plans;
        private static int disabled;
        private static bool sealedRules;

        /// <summary>True while the optional native hook is active in this process.</summary>
        public static bool IsAvailable => plans != null && Volatile.Read(ref disabled) == 0;
        internal static void Disable() => Interlocked.Exchange(ref disabled, 1);

        /// <summary>Declare during the dependent plugin's Awake before the first native terrain generation.</summary>
        public static void Register(string ownerId, string localName, OreGenerationScope scope, int nativeSlot,
            float? size = null, float? threshold = null)
        {
            if ((uint)nativeSlot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(nativeSlot));
            var rule = new OreGenerationRule(ownerId + ":" + localName, scope, nativeSlot, size, threshold);
            lock (Gate)
            {
                if (sealedRules) throw new InvalidOperationException("Ore generation registrations are closed for this process.");
                if (Rules.ContainsKey(rule.Key)) throw new InvalidOperationException("Duplicate ore generation key: " + rule.Key);
                Rules.Add(rule.Key, rule);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var target = typeof(LandformCommon).GetMethod(nameof(LandformCommon.GetOreData),
                new[] { typeof(HashDrawSet), typeof(uint), typeof(StartingLocationType), typeof(bool) })
                ?? throw new MissingMethodException(nameof(LandformCommon.GetOreData));
            var postfix = typeof(OreGeneration).GetMethod(nameof(AfterGetOreData), BindingFlags.NonPublic | BindingFlags.Static)!;
            warning = reportWarning;
            harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            plans = new Lazy<(OreGenerationPlan, OreGenerationPlan)>(ResolvePlans);
        }

        private static (OreGenerationPlan Planets, OreGenerationPlan Asteroids) ResolvePlans()
        {
            lock (Gate)
            {
                sealedRules = true;
                return (OreGenerationPlan.Resolve(Rules.Values, OreGenerationScope.Planets, SlotCount, warning!),
                    OreGenerationPlan.Resolve(Rules.Values, OreGenerationScope.Asteroids, SlotCount, warning!));
            }
        }

        private static unsafe void AfterGetOreData(bool __3, ref LandformCommon.Lf_OreData __result)
        {
            if (!IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            try
            {
                var current = plans!.Value;
                var plan = __3 ? current.Asteroids : current.Planets;
                if (!plan.HasOverrides) return;

                var candidate = __result;
                var sizes = new Span<float>(candidate.oreSize, SlotCount);
                var thresholds = new Span<float>(candidate.oreThreshold, SlotCount);
                if (candidate.totalWeight != OreGenerationPlan.SumWeights(thresholds)
                    || !plan.TryApply(sizes, thresholds, out var weight))
                    throw new InvalidOperationException("Native ore payload no longer uses the expected threshold sum or valid slot values.");
                candidate.totalWeight = weight;
                __result = candidate;
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref disabled, 1) == 0)
                    warning!("Ore generation overrides disabled: " + exception.Message);
            }
        }
    }
}
