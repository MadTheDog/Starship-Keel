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

    internal sealed class TerrainPlacementRule
    {
        internal TerrainPlacementRule(string key, TerrainPlacementScope scope, TerrainMaterialReference source,
            TerrainMaterialReference destination, ulong? bodySeed)
        {
            ContentAllocator.ValidateKey(key);
            if (scope == 0 || (scope & ~TerrainPlacementScope.All) != 0) throw new ArgumentOutOfRangeException(nameof(scope));
            if (source.Kind != TerrainMaterialReferenceKind.Stock)
                throw new ArgumentException("Generated material replacement requires a named stock source.", nameof(source));
            if (destination.Kind != TerrainMaterialReferenceKind.Stock && destination.Kind != TerrainMaterialReferenceKind.Registered)
                throw new ArgumentException("A named stock or registered destination is required.", nameof(destination));
            if (destination.Kind == TerrainMaterialReferenceKind.Stock && source.Value == destination.Value)
                throw new ArgumentException("Source and destination must differ.");
            Key = key;
            Scope = scope;
            Source = source;
            Destination = destination;
            BodySeed = bodySeed;
        }

        internal string Key { get; }
        internal TerrainPlacementScope Scope { get; }
        internal TerrainMaterialReference Source { get; }
        internal TerrainMaterialReference Destination { get; }
        internal ulong? BodySeed { get; }
    }

    internal sealed class TerrainPlacementPlan
    {
        private readonly byte[] map = Enumerable.Range(0, 256).Select(id => (byte)id).ToArray();
        internal bool HasChanges { get; private set; }
        internal byte Map(byte original) => map[original];

        internal static TerrainPlacementPlan Build(IReadOnlyList<TerrainPlacementRule> rules, TerrainPlacementScope scope,
            ulong? seed, Func<TerrainMaterialReference, byte?> resolve, Action<string> warn)
        {
            var plan = new TerrainPlacementPlan();
            var matches = new List<(string Key, byte Source, byte Destination)>();
            foreach (var rule in rules.Where(rule => (rule.Scope & scope) != 0 && (!rule.BodySeed.HasValue || rule.BodySeed == seed)))
            {
                var source = resolve(rule.Source);
                var destination = resolve(rule.Destination);
                if (source.HasValue && destination.HasValue && source != destination)
                    matches.Add((rule.Key, source.Value, destination.Value));
                else
                    warn("Terrain placement rule retains stock materials because its native rows could not be resolved: " + rule.Key);
            }
            foreach (var group in matches.GroupBy(match => match.Source))
            {
                var claims = group.OrderBy(claim => claim.Key, StringComparer.Ordinal).ToArray();
                if (claims.Length != 1)
                {
                    warn("Terrain placement source retains its native material due to conflicting rules: "
                        + string.Join(", ", claims.Select(claim => claim.Key)));
                    continue;
                }
                plan.map[group.Key] = claims[0].Destination;
                plan.HasChanges = true;
            }
            return plan;
        }
    }
}
