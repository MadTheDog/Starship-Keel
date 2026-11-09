using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    public enum TerrainMaterialReferenceKind
    {
        Stock = 1,
        Registered = 2
    }

    public readonly struct TerrainMaterialReference
    {
        private TerrainMaterialReference(TerrainMaterialReferenceKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public TerrainMaterialReferenceKind Kind { get; }
        public string Value { get; }

        public static TerrainMaterialReference Stock(string nativeShaderName)
        {
            if (string.IsNullOrWhiteSpace(nativeShaderName)) throw new ArgumentException("A stock shader name is required.", nameof(nativeShaderName));
            return new TerrainMaterialReference(TerrainMaterialReferenceKind.Stock, nativeShaderName);
        }

        public static TerrainMaterialReference Registered(string key)
        {
            ContentAllocator.ValidateKey(key);
            return new TerrainMaterialReference(TerrainMaterialReferenceKind.Registered, key);
        }
    }

    [Flags]
    public enum TerrainPlacementScope
    {
        Planets = 1,
        Asteroids = 2,
        All = Planets | Asteroids
    }

    /// <summary>A half-open band of metres relative to a planet's nominal radius.</summary>
    public readonly struct TerrainRadialBand
    {
        public TerrainRadialBand(double minimumOffset, double maximumOffset)
        {
            if (!double.IsFinite(minimumOffset) || !double.IsFinite(maximumOffset) || minimumOffset >= maximumOffset)
                throw new ArgumentException("A radial band requires finite, increasing offsets.");
            MinimumOffset = minimumOffset;
            MaximumOffset = maximumOffset;
        }

        public double MinimumOffset { get; }
        public double MaximumOffset { get; }
    }

    internal sealed class TerrainPlacementRule
    {
        internal TerrainPlacementRule(string key, TerrainPlacementScope scope, TerrainMaterialReference source,
            TerrainMaterialReference destination, ulong? bodySeed, TerrainRadialBand? band = null)
        {
            ContentAllocator.ValidateKey(key);
            if (scope == 0 || (scope & ~TerrainPlacementScope.All) != 0) throw new ArgumentOutOfRangeException(nameof(scope));
            if (source.Kind != TerrainMaterialReferenceKind.Stock)
                throw new ArgumentException("Generated material replacement requires a named stock source.", nameof(source));
            if (destination.Kind != TerrainMaterialReferenceKind.Stock && destination.Kind != TerrainMaterialReferenceKind.Registered)
                throw new ArgumentException("A named stock or registered destination is required.", nameof(destination));
            if (destination.Kind == TerrainMaterialReferenceKind.Stock && source.Value == destination.Value)
                throw new ArgumentException("Source and destination must differ.");
            if (band is TerrainRadialBand radial && (scope != TerrainPlacementScope.Planets
                || radial.MinimumOffset >= radial.MaximumOffset))
                throw new ArgumentException("A radial band requires planet scope and increasing offsets.", nameof(band));
            Key = key;
            Scope = scope;
            Source = source;
            Destination = destination;
            BodySeed = bodySeed;
            Band = band;
        }

        internal string Key { get; }
        internal TerrainPlacementScope Scope { get; }
        internal TerrainMaterialReference Source { get; }
        internal TerrainMaterialReference Destination { get; }
        internal ulong? BodySeed { get; }
        internal TerrainRadialBand? Band { get; }
    }

    internal sealed class TerrainPlacementPlan
    {
        private readonly byte[] map = Enumerable.Range(0, 256).Select(id => (byte)id).ToArray();
        private readonly (TerrainRadialBand Band, byte Destination)[]?[] radial = new (TerrainRadialBand, byte)[256][];
        internal bool HasChanges { get; private set; }
        internal bool HasRadialBands { get; private set; }
        internal byte Map(byte original) => map[original];
        internal bool NeedsRadialOffset(byte original) => radial[original] != null;

        internal byte Map(byte original, double offset)
        {
            if (radial[original] is { } bands)
                foreach (var (band, destination) in bands)
                    if (offset >= band.MinimumOffset && offset < band.MaximumOffset)
                        return destination;
            return map[original];
        }

        internal static TerrainPlacementPlan Build(IReadOnlyList<TerrainPlacementRule> rules, TerrainPlacementScope scope,
            ulong? seed, Func<TerrainMaterialReference, byte?> resolve, Action<string> warn, bool radialBands = true)
        {
            var plan = new TerrainPlacementPlan();
            var matches = new List<(string Key, byte Source, byte Destination, TerrainRadialBand? Band)>();
            foreach (var rule in rules.Where(rule => (rule.Scope & scope) != 0 && (!rule.BodySeed.HasValue || rule.BodySeed == seed)))
            {
                if (rule.Band.HasValue && !radialBands) continue;
                var source = resolve(rule.Source);
                var destination = resolve(rule.Destination);
                if (source.HasValue && destination.HasValue && source != destination)
                    matches.Add((rule.Key, source.Value, destination.Value, rule.Band));
                else
                    warn("Terrain placement rule retains stock materials because its native rows could not be resolved: " + rule.Key);
            }
            foreach (var group in matches.GroupBy(match => match.Source))
            {
                var claims = group.OrderBy(claim => claim.Key, StringComparer.Ordinal).ToArray();
                if (claims.Length == 1 && !claims[0].Band.HasValue)
                {
                    plan.map[group.Key] = claims[0].Destination;
                    plan.HasChanges = true;
                    continue;
                }
                if (claims.Length <= 16 && claims.All(claim => claim.Band.HasValue))
                {
                    var ordered = claims.Select(claim => (Band: claim.Band!.Value, claim.Destination))
                        .OrderBy(claim => claim.Band.MinimumOffset).ToArray();
                    if (!ordered.Zip(ordered.Skip(1), (left, right) => left.Band.MaximumOffset > right.Band.MinimumOffset)
                        .Any(overlap => overlap))
                    {
                        plan.radial[group.Key] = ordered;
                        plan.HasRadialBands = plan.HasChanges = true;
                        continue;
                    }
                }
                warn("Terrain placement source retains its native material due to conflicting rules or too many bands: "
                    + string.Join(", ", claims.Select(claim => claim.Key)));
            }
            return plan;
        }
    }
}
