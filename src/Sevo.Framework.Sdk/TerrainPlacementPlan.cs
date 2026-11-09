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

    /// <summary>A half-open band of inward metres beneath a mod-authored planet surface.</summary>
    public readonly struct TerrainAuthoredSurfaceDepthBand
    {
        public TerrainAuthoredSurfaceDepthBand(double minimumDepth, double maximumDepth)
        {
            if (!double.IsFinite(minimumDepth) || !double.IsFinite(maximumDepth)
                || minimumDepth < 0 || minimumDepth >= maximumDepth)
                throw new ArgumentException("A surface depth band requires finite, increasing nonnegative depths.");
            MinimumDepth = minimumDepth;
            MaximumDepth = maximumDepth;
        }

        public double MinimumDepth { get; }
        public double MaximumDepth { get; }
    }

    /// <summary>A unit direction in the same body-local frame as the planet's terrain samples.</summary>
    public readonly struct TerrainAuthoredSurfaceSample
    {
        private TerrainAuthoredSurfaceSample(ulong systemSeed, ulong bodySeed, double nominalRadius, double radius,
            double x, double y, double z)
        {
            SystemSeed = systemSeed;
            BodySeed = bodySeed;
            NominalRadius = nominalRadius;
            Radius = radius;
            DirectionX = x / radius;
            DirectionY = y / radius;
            DirectionZ = z / radius;
        }

        public ulong SystemSeed { get; }
        public ulong BodySeed { get; }
        public double NominalRadius { get; }
        public double DirectionX { get; }
        public double DirectionY { get; }
        public double DirectionZ { get; }
        internal double Radius { get; }

        internal static bool TryCreate(ulong bodySeed, double nominalRadius, double x, double y, double z,
            out TerrainAuthoredSurfaceSample sample, ulong systemSeed = 0)
        {
            double radius = Math.Sqrt(x * x + y * y + z * z);
            if (double.IsFinite(nominalRadius) && nominalRadius > 0 && double.IsFinite(radius) && radius > 0)
            {
                sample = new TerrainAuthoredSurfaceSample(systemSeed, bodySeed, nominalRadius, radius, x, y, z);
                return true;
            }
            sample = default;
            return false;
        }
    }

    internal sealed class TerrainPlacementRule
    {
        internal TerrainPlacementRule(string key, TerrainPlacementScope scope, TerrainMaterialReference source,
            TerrainMaterialReference destination, ulong? bodySeed, TerrainRadialBand? band = null,
            TerrainAuthoredSurfaceDepthBand? depth = null, Func<TerrainAuthoredSurfaceSample, double?>? surfaceRadius = null)
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
            if (depth.HasValue != (surfaceRadius != null) || depth is TerrainAuthoredSurfaceDepthBand authored
                && (scope != TerrainPlacementScope.Planets || !bodySeed.HasValue || band.HasValue
                    || authored.MinimumDepth >= authored.MaximumDepth))
                throw new ArgumentException("An authored surface depth band requires a body seed and a surface radius provider.", nameof(depth));
            Key = key;
            Scope = scope;
            Source = source;
            Destination = destination;
            BodySeed = bodySeed;
            Band = band;
            Depth = depth;
            SurfaceRadius = surfaceRadius;
        }

        internal string Key { get; }
        internal TerrainPlacementScope Scope { get; }
        internal TerrainMaterialReference Source { get; }
        internal TerrainMaterialReference Destination { get; }
        internal ulong? BodySeed { get; }
        internal TerrainRadialBand? Band { get; }
        internal TerrainAuthoredSurfaceDepthBand? Depth { get; }
        internal Func<TerrainAuthoredSurfaceSample, double?>? SurfaceRadius { get; }
    }

    internal sealed class TerrainPlacementPlan
    {
        private readonly byte[] map = Enumerable.Range(0, 256).Select(id => (byte)id).ToArray();
        private readonly (TerrainRadialBand Band, byte Destination)[]?[] radial = new (TerrainRadialBand, byte)[256][];
        private Dictionary<byte, (Func<TerrainAuthoredSurfaceSample, double?> Radius, string Key,
            (TerrainAuthoredSurfaceDepthBand Band, byte Destination)[] Bands)>? authored;
        private readonly Action<string> warn;
        private TerrainPlacementPlan(Action<string> warn) => this.warn = warn;

        internal bool HasChanges { get; private set; }
        internal bool HasRadialBands { get; private set; }
        internal bool HasAuthoredSurfaceBands => authored != null;
        internal byte Map(byte original) => map[original];
        internal bool NeedsRadialOffset(byte original) => radial[original] != null;
        internal bool NeedsAuthoredSurface(byte original) => authored?.ContainsKey(original) == true;

        internal byte Map(byte original, double offset)
        {
            if (radial[original] is { } bands)
                foreach (var (band, destination) in bands)
                    if (offset >= band.MinimumOffset && offset < band.MaximumOffset)
                        return destination;
            return map[original];
        }

        internal byte MapAuthoredSurface(byte original, TerrainAuthoredSurfaceSample sample, bool isSolid)
        {
            if (!isSolid || sample.Radius <= 0 || authored == null || !authored.TryGetValue(original, out var field))
                return map[original];
            double? ground;
            try { ground = field.Radius(sample); }
            catch (Exception)
            {
                warn("Terrain authored surface provider failed for " + field.Key + ". Affected samples retain stock materials.");
                return original;
            }
            if (ground is not double radius || !double.IsFinite(radius) || radius <= 0) return original;
            double depth = radius - sample.Radius;
            foreach (var (band, destination) in field.Bands)
                if (depth >= band.MinimumDepth && depth < band.MaximumDepth)
                    return destination;
            return original;
        }

        internal static TerrainPlacementPlan Build(IReadOnlyList<TerrainPlacementRule> rules, TerrainPlacementScope scope,
            ulong? seed, Func<TerrainMaterialReference, byte?> resolve, Action<string> warn, bool radialBands = true,
            bool authoredSurfaceBands = true)
        {
            var plan = new TerrainPlacementPlan(warn);
            var matches = new List<(TerrainPlacementRule Rule, byte Source, byte Destination)>();
            foreach (var rule in rules.Where(rule => (rule.Scope & scope) != 0 && (!rule.BodySeed.HasValue || rule.BodySeed == seed)))
            {
                if ((!radialBands && rule.Band.HasValue) || (!authoredSurfaceBands && rule.Depth.HasValue)) continue;
                var source = resolve(rule.Source);
                var destination = resolve(rule.Destination);
                if (source.HasValue && destination.HasValue && source != destination)
                    matches.Add((rule, source.Value, destination.Value));
                else
                    warn("Terrain placement rule retains stock materials because its native rows could not be resolved: " + rule.Key);
            }
            foreach (var group in matches.GroupBy(match => match.Source))
            {
                var claims = group.OrderBy(claim => claim.Rule.Key, StringComparer.Ordinal).ToArray();
                if (claims.Length == 1 && !claims[0].Rule.Band.HasValue && !claims[0].Rule.Depth.HasValue)
                {
                    plan.map[group.Key] = claims[0].Destination;
                    plan.HasChanges = true;
                    continue;
                }
                if (claims.Length <= 16 && claims.All(claim => claim.Rule.Band.HasValue))
                {
                    var ordered = claims.Select(claim => (Band: claim.Rule.Band!.Value, claim.Destination))
                        .OrderBy(claim => claim.Band.MinimumOffset).ToArray();
                    if (!ordered.Zip(ordered.Skip(1), (left, right) => left.Band.MaximumOffset > right.Band.MinimumOffset)
                        .Any(overlap => overlap))
                    {
                        plan.radial[group.Key] = ordered;
                        plan.HasRadialBands = plan.HasChanges = true;
                        continue;
                    }
                }
                if (claims.Length <= 16 && claims[0].Rule.SurfaceRadius is { } field
                    && claims.All(claim => claim.Rule.Depth.HasValue && claim.Rule.SurfaceRadius == field))
                {
                    var ordered = claims.Select(claim => (Band: claim.Rule.Depth!.Value, claim.Destination))
                        .OrderBy(claim => claim.Band.MinimumDepth).ToArray();
                    if (!ordered.Zip(ordered.Skip(1), (left, right) => left.Band.MaximumDepth > right.Band.MinimumDepth)
                        .Any(overlap => overlap))
                    {
                        plan.authored ??= new Dictionary<byte, (Func<TerrainAuthoredSurfaceSample, double?>, string,
                            (TerrainAuthoredSurfaceDepthBand, byte)[])>();
                        plan.authored.Add(group.Key, (field, claims[0].Rule.Key, ordered));
                        plan.HasChanges = true;
                        continue;
                    }
                }
                warn("Terrain placement source retains its native material due to conflicting rules or too many bands: "
                    + string.Join(", ", claims.Select(claim => claim.Rule.Key)));
            }
            return plan;
        }
    }
}
