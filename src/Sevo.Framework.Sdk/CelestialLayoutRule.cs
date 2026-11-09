using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    /// <summary>A deterministic body override or append-only rocky moon declaration. Distances are in metres.</summary>
    public sealed class CelestialLayoutRule
    {
        private CelestialLayoutRule(ulong systemSeed, ulong bodySeed, ulong? parentSeed,
            float? radiusMeters, float? orbitRadiusMeters)
        {
            if (systemSeed == 0) throw new ArgumentOutOfRangeException(nameof(systemSeed));
            if (bodySeed == 0 || bodySeed == systemSeed) throw new ArgumentOutOfRangeException(nameof(bodySeed));
            if (parentSeed == 0 || parentSeed == bodySeed || parentSeed == systemSeed)
                throw new ArgumentOutOfRangeException(nameof(parentSeed));
            ValidateDistance(radiusMeters, nameof(radiusMeters));
            ValidateDistance(orbitRadiusMeters, nameof(orbitRadiusMeters));
            if (!radiusMeters.HasValue && !orbitRadiusMeters.HasValue)
                throw new ArgumentException("A body override requires a radius or orbit radius.");

            SystemSeed = systemSeed;
            BodySeed = bodySeed;
            ParentSeed = parentSeed;
            RadiusMeters = radiusMeters;
            OrbitRadiusMeters = orbitRadiusMeters;
        }

        public ulong SystemSeed { get; }
        public ulong BodySeed { get; }
        public ulong? ParentSeed { get; }
        public float? RadiusMeters { get; }
        public float? OrbitRadiusMeters { get; }
        public bool IsAddition => ParentSeed.HasValue;

        public static CelestialLayoutRule OverrideBody(ulong systemSeed, ulong bodySeed,
            float? radiusMeters = null, float? orbitRadiusMeters = null)
            => new CelestialLayoutRule(systemSeed, bodySeed, null, radiusMeters, orbitRadiusMeters);

        public static CelestialLayoutRule AddRockyMoon(ulong systemSeed, ulong parentSeed, ulong moonSeed,
            float radiusMeters, float orbitRadiusMeters)
            => new CelestialLayoutRule(systemSeed, moonSeed, parentSeed, radiusMeters, orbitRadiusMeters);

        private static void ValidateDistance(float? value, string name)
        {
            if (value is float distance && (!(distance > 0) || float.IsInfinity(distance)))
                throw new ArgumentOutOfRangeException(name);
        }
    }

    internal sealed class CelestialLayoutPlan
    {
        private readonly Dictionary<ulong, IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>>> systems;

        private CelestialLayoutPlan(Dictionary<ulong, IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>>> systems)
            => this.systems = systems;

        internal static CelestialLayoutPlan Resolve(IEnumerable<KeyValuePair<string, CelestialLayoutRule>> declarations)
            => new CelestialLayoutPlan(declarations.GroupBy(entry => entry.Value.SystemSeed)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>>)
                    group.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray()));

        internal IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>> GetRules(ulong systemSeed)
            => systems.TryGetValue(systemSeed, out var rules) ? rules : Array.Empty<KeyValuePair<string, CelestialLayoutRule>>();

        internal static bool HasConflict(IReadOnlyList<KeyValuePair<string, CelestialLayoutRule>> rules)
        {
            foreach (var target in rules.GroupBy(entry => entry.Value.BodySeed))
            {
                if (target.Count() < 2) continue;
                if (target.Any(entry => entry.Value.IsAddition)
                    || target.Count(entry => entry.Value.RadiusMeters.HasValue) > 1
                    || target.Count(entry => entry.Value.OrbitRadiusMeters.HasValue) > 1)
                    return true;
            }
            return false;
        }
    }
}
