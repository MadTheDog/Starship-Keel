using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    public readonly struct PlanetTerrainContext
    {
        internal PlanetTerrainContext(ulong seed, int bodyId, bool isStartingPlanet, float craterAmount,
            float continentThreshold, float canyonStrength, bool hasStockCanyon)
        {
            Seed = seed;
            BodyId = bodyId;
            IsStartingPlanet = isStartingPlanet;
            CraterAmount = craterAmount;
            ContinentThreshold = continentThreshold;
            CanyonStrength = canyonStrength;
            HasStockCanyon = hasStockCanyon;
        }

        public ulong Seed { get; }
        public int BodyId { get; }
        public bool IsStartingPlanet { get; }
        public float CraterAmount { get; }
        public float ContinentThreshold { get; }
        public float CanyonStrength { get; }
        public bool HasStockCanyon { get; }
    }

    public readonly struct AsteroidTerrainContext
    {
        internal AsteroidTerrainContext(ulong parentBodySeed, int parentBodyId, bool isStartingPlanet,
            float craterAmount, float craterScaleFactor)
        {
            ParentBodySeed = parentBodySeed;
            ParentBodyId = parentBodyId;
            IsStartingPlanet = isStartingPlanet;
            CraterAmount = craterAmount;
            CraterScaleFactor = craterScaleFactor;
        }

        public ulong ParentBodySeed { get; }
        public int ParentBodyId { get; }
        public bool IsStartingPlanet { get; }
        public float CraterAmount { get; }
        public float CraterScaleFactor { get; }
    }

    public readonly struct PlanetTerrainOverrides
    {
        public PlanetTerrainOverrides(float? craterAmount = null, float? continentThreshold = null, float? canyonStrength = null)
        {
            CraterAmount = TerrainParameter.Validate(craterAmount, 0f, 1f, nameof(craterAmount));
            ContinentThreshold = TerrainParameter.Validate(continentThreshold, -0.5f, 0.5f, nameof(continentThreshold));
            CanyonStrength = TerrainParameter.Validate(canyonStrength, 0f, 1f, nameof(canyonStrength));
        }

        public float? CraterAmount { get; }
        public float? ContinentThreshold { get; }
        public float? CanyonStrength { get; }
    }

    public readonly struct AsteroidTerrainOverrides
    {
        public AsteroidTerrainOverrides(float? craterAmount = null, float? craterScaleFactor = null)
        {
            CraterAmount = TerrainParameter.Validate(craterAmount, 0f, 0.5f, nameof(craterAmount));
            CraterScaleFactor = TerrainParameter.Validate(craterScaleFactor, 0.25f, 0.5f, nameof(craterScaleFactor));
        }

        public float? CraterAmount { get; }
        public float? CraterScaleFactor { get; }
    }

    internal static class TerrainParameter
    {
        internal static float? Validate(float? value, float minimum, float maximum, string argument)
        {
            if (value is float number && !(number >= minimum && number <= maximum))
                throw new ArgumentOutOfRangeException(argument, $"Expected a finite value in [{minimum}, {maximum}].");
            return value;
        }
    }

    internal sealed class TerrainGenerationCatalog<TContext, TProposal> where TProposal : struct
    {
        private readonly object gate = new object();
        private readonly SortedDictionary<string, Func<TContext, TProposal>> entries =
            new SortedDictionary<string, Func<TContext, TProposal>>(StringComparer.Ordinal);
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private KeyValuePair<string, Func<TContext, TProposal>>[]? snapshot;

        internal void Add(string ownerId, string localName, Func<TContext, TProposal> provider)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (gate)
            {
                if (snapshot != null) throw new InvalidOperationException("Terrain provider registration is closed for this process.");
                if (entries.ContainsKey(key)) throw new InvalidOperationException("Duplicate terrain generation provider: " + key);
                entries.Add(key, provider);
            }
        }

        internal (string Key, TProposal Proposal)[] Collect(TContext context, Action<string> warn)
        {
            KeyValuePair<string, Func<TContext, TProposal>>[] providers;
            lock (gate) providers = snapshot ??= entries.ToArray();
            if (providers.Length == 0) return Array.Empty<(string, TProposal)>();
            var proposals = new List<(string, TProposal)>(providers.Length);
            foreach (var provider in providers)
            {
                try { proposals.Add((provider.Key, provider.Value(context))); }
                catch (Exception exception)
                {
                    ReportOnce("provider:" + provider.Key, "Terrain provider " + provider.Key + " declined after an exception: "
                        + exception.Message, warn);
                }
            }
            return proposals.ToArray();
        }

        internal float? Resolve((string Key, TProposal Proposal)[] proposals, Func<TProposal, float?> select,
            string field, Action<string> warn)
        {
            var claims = proposals.Select(proposal => (proposal.Key, Value: select(proposal.Proposal)))
                .Where(claim => claim.Value.HasValue).ToArray();
            if (claims.Length == 1) return claims[0].Value;
            if (claims.Length > 1)
            {
                string owners = string.Join(", ", claims.Select(claim => claim.Key));
                ReportOnce(field + ":" + owners, "Terrain parameter retains its native value due to conflicting providers for "
                    + field + ": " + owners, warn);
            }
            return null;
        }

        internal float ApplyNative(float original, float? proposed, float minimum, float maximum,
            string field, Action<string> warn)
        {
            if (!proposed.HasValue) return original;
            if (original >= minimum && original <= maximum) return proposed.Value;
            ReportOnce("native:" + field, "Terrain parameter retains its native value because it is outside the supported range: "
                + field, warn);
            return original;
        }

        internal void ReportOnce(string key, string reason, Action<string> warn)
        {
            lock (gate)
                if (!reported.Add(key)) return;
            warn(reason);
        }
    }
}
