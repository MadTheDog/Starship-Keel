using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    [Flags]
    public enum OreGenerationScope
    {
        Planets = 1,
        Asteroids = 2,
        All = Planets | Asteroids
    }

    internal sealed class OreGenerationRule
    {
        internal OreGenerationRule(string key, OreGenerationScope scope, int slot, float? size, float? threshold)
        {
            ContentAllocator.ValidateKey(key);
            if (scope == 0 || (scope & ~OreGenerationScope.All) != 0) throw new ArgumentOutOfRangeException(nameof(scope));
            if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
            if (size is float s && !(s > 0f && s < float.PositiveInfinity)) throw new ArgumentOutOfRangeException(nameof(size));
            if (threshold is float t && !(t >= 0f && t <= 1f)) throw new ArgumentOutOfRangeException(nameof(threshold));
            if (size == null && threshold == null) throw new ArgumentException("An ore rule needs a size or threshold.");
            Key = key;
            Scope = scope;
            Slot = slot;
            Size = size;
            Threshold = threshold;
        }

        internal string Key { get; }
        internal OreGenerationScope Scope { get; }
        internal int Slot { get; }
        internal float? Size { get; }
        internal float? Threshold { get; }
    }

    internal sealed class OreGenerationPlan
    {
        private readonly OreGenerationRule?[] slots;
        private OreGenerationPlan(OreGenerationRule?[] slots) => this.slots = slots;
        internal bool HasOverrides => slots.Any(rule => rule != null);

        internal static OreGenerationPlan Resolve(IEnumerable<OreGenerationRule> rules, OreGenerationScope scope,
            int capacity, Action<string> report)
        {
            var slots = new OreGenerationRule?[capacity];
            foreach (var group in rules.Where(rule => (rule.Scope & scope) != 0).GroupBy(rule => rule.Slot).OrderBy(group => group.Key))
            {
                var ordered = group.OrderBy(rule => rule.Key, StringComparer.Ordinal).ToArray();
                if (group.Key >= capacity)
                    report($"Ore {scope} slot {group.Key} is outside the native range.");
                else if (ordered.Length > 1)
                    report($"Ore {scope} slot {group.Key} retains stock values due to conflicting registrations: "
                        + string.Join(", ", ordered.Select(rule => rule.Key)));
                else
                    slots[group.Key] = ordered[0];
            }
            return new OreGenerationPlan(slots);
        }

        internal static float SumWeights(ReadOnlySpan<float> thresholds)
        {
            float total = 0;
            foreach (var threshold in thresholds)
                total += 1f - threshold;
            return total;
        }

        internal bool TryApply(Span<float> sizes, Span<float> thresholds, out float totalWeight)
        {
            totalWeight = 0;
            if (sizes.Length != slots.Length || thresholds.Length != slots.Length) return false;
            float candidateWeight = 0;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                float size = sizes[slot];
                float threshold = thresholds[slot];
                if (!(size > 0f && size < float.PositiveInfinity) || !(threshold >= 0f && threshold < float.PositiveInfinity))
                    return false;
                candidateWeight += 1f - (slots[slot]?.Threshold ?? threshold);
            }
            if (float.IsInfinity(candidateWeight)) return false;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                var rule = slots[slot];
                sizes[slot] = rule?.Size ?? sizes[slot];
                thresholds[slot] = rule?.Threshold ?? thresholds[slot];
            }
            totalWeight = candidateWeight;
            return true;
        }
    }
}
