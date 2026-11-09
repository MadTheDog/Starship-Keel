using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    /// <summary>Identifies one seeded body within a star system, independently of runtime IDs.</summary>
    public readonly struct BodyEnvironmentTarget : IEquatable<BodyEnvironmentTarget>
    {
        public ulong StarSeed { get; }
        public ulong BodySeed { get; }

        public BodyEnvironmentTarget(ulong starSeed, ulong bodySeed)
        {
            if (starSeed == 0) throw new ArgumentOutOfRangeException(nameof(starSeed));
            StarSeed = starSeed;
            BodySeed = bodySeed;
        }

        public bool Equals(BodyEnvironmentTarget other) => StarSeed == other.StarSeed && BodySeed == other.BodySeed;
        public override bool Equals(object? obj) => obj is BodyEnvironmentTarget other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StarSeed, BodySeed);
        public override string ToString() => FormattableString.Invariant($"{StarSeed}/{BodySeed}");
    }

    /// <summary>Optional overrides for a rocky body's native environment. A null value preserves stock generation.</summary>
    public sealed class BodyEnvironmentOverride
    {
        public float? WaterAmount { get; }
        public float? AtmosphereDensity { get; }
        public float? OxygenDensity { get; }
        public float? GravityScale { get; }
        public uint? AtmosphereRgb { get; }
        public uint? OceanMainRgb { get; }
        public uint? OceanSecondaryRgb { get; }
        public uint? OceanDepthRgb { get; }

        public BodyEnvironmentOverride(float? waterAmount = null, float? atmosphereDensity = null, float? oxygenDensity = null,
            float? gravityScale = null, uint? atmosphereRgb = null, uint? oceanMainRgb = null,
            uint? oceanSecondaryRgb = null, uint? oceanDepthRgb = null)
        {
            CheckRange(waterAmount, 1f, nameof(waterAmount));
            if (waterAmount > 0f && waterAmount <= 0.01f)
                throw new ArgumentOutOfRangeException(nameof(waterAmount), "Use zero for a dry body or a value above 0.01 for an ocean.");
            CheckRange(atmosphereDensity, 1f, nameof(atmosphereDensity));
            CheckRange(oxygenDensity, 1f, nameof(oxygenDensity));
            CheckRange(gravityScale, 4f, nameof(gravityScale));
            CheckRgb(atmosphereRgb, nameof(atmosphereRgb));
            CheckRgb(oceanMainRgb, nameof(oceanMainRgb));
            CheckRgb(oceanSecondaryRgb, nameof(oceanSecondaryRgb));
            CheckRgb(oceanDepthRgb, nameof(oceanDepthRgb));

            WaterAmount = waterAmount;
            AtmosphereDensity = atmosphereDensity;
            OxygenDensity = oxygenDensity;
            GravityScale = gravityScale;
            AtmosphereRgb = atmosphereRgb;
            OceanMainRgb = oceanMainRgb;
            OceanSecondaryRgb = oceanSecondaryRgb;
            OceanDepthRgb = oceanDepthRgb;
        }

        internal bool HasOverrides => WaterAmount.HasValue || AtmosphereDensity.HasValue || OxygenDensity.HasValue
            || GravityScale.HasValue || AtmosphereRgb.HasValue || OceanMainRgb.HasValue || OceanSecondaryRgb.HasValue || OceanDepthRgb.HasValue;

        internal float ResolveOxygen(float stock, float atmosphere)
        {
            if (OxygenDensity.HasValue || AtmosphereDensity.HasValue)
                return Math.Min(OxygenDensity ?? stock, atmosphere);
            return stock;
        }

        private static void CheckRange(float? value, float maximum, string name)
        {
            if (value is float number && !(number >= 0f && number <= maximum))
                throw new ArgumentOutOfRangeException(name);
        }

        private static void CheckRgb(uint? color, string name)
        {
            if (color > 0xFFFFFF) throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class BodyEnvironmentRule
    {
        internal string Key { get; }
        internal BodyEnvironmentTarget Target { get; }
        internal BodyEnvironmentOverride Value { get; }

        internal BodyEnvironmentRule(string key, BodyEnvironmentTarget target, BodyEnvironmentOverride value)
        {
            ContentAllocator.ValidateKey(key);
            if (target.StarSeed == 0) throw new ArgumentException("A nonzero star seed is required.", nameof(target));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!value.HasOverrides) throw new ArgumentException("At least one environment field is required.", nameof(value));

            Key = key;
            Target = target;
            Value = value;
        }
    }

    internal static class BodyEnvironmentPlan
    {
        internal static IReadOnlyDictionary<BodyEnvironmentTarget, BodyEnvironmentOverride> Resolve(
            IEnumerable<BodyEnvironmentRule> rules, Action<string> warning)
        {
            var result = new Dictionary<BodyEnvironmentTarget, BodyEnvironmentOverride>();
            foreach (var group in rules.OrderBy(rule => rule.Key, StringComparer.Ordinal).GroupBy(rule => rule.Target))
            {
                var declarations = group.ToArray();
                T? Pick<T>(string field, Func<BodyEnvironmentOverride, T?> select) where T : struct
                {
                    var owners = declarations.Where(rule => select(rule.Value).HasValue).ToArray();
                    if (owners.Length == 1) return select(owners[0].Value);
                    if (owners.Length > 1)
                        warning($"Body {group.Key} retains stock {field} due to conflicting declarations from "
                            + string.Join(", ", owners.Select(rule => rule.Key)) + ".");
                    return null;
                }

                result.Add(group.Key, new BodyEnvironmentOverride(
                    Pick(nameof(BodyEnvironmentOverride.WaterAmount), value => value.WaterAmount),
                    Pick(nameof(BodyEnvironmentOverride.AtmosphereDensity), value => value.AtmosphereDensity),
                    Pick(nameof(BodyEnvironmentOverride.OxygenDensity), value => value.OxygenDensity),
                    Pick(nameof(BodyEnvironmentOverride.GravityScale), value => value.GravityScale),
                    Pick(nameof(BodyEnvironmentOverride.AtmosphereRgb), value => value.AtmosphereRgb),
                    Pick(nameof(BodyEnvironmentOverride.OceanMainRgb), value => value.OceanMainRgb),
                    Pick(nameof(BodyEnvironmentOverride.OceanSecondaryRgb), value => value.OceanSecondaryRgb),
                    Pick(nameof(BodyEnvironmentOverride.OceanDepthRgb), value => value.OceanDepthRgb)));
            }
            return result;
        }
    }
}
