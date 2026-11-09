using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sevo.Framework.Sdk
{
    /// <summary>A native scanner pulse in system-relative metre coordinates.</summary>
    public readonly struct OreDiscoveryContext
    {
        public OreDiscoveryContext(ulong systemSeed, double originX, double originY, double originZ, float rangeMeters)
        {
            SystemSeed = systemSeed;
            OriginX = originX;
            OriginY = originY;
            OriginZ = originZ;
            RangeMeters = rangeMeters;
        }

        public ulong SystemSeed { get; }
        public double OriginX { get; }
        public double OriginY { get; }
        public double OriginZ { get; }
        public float RangeMeters { get; }
    }

    /// <summary>A mod-owned discovery, measured in metres from the pulse origin.</summary>
    public readonly struct OreDiscoveryResult
    {
        public OreDiscoveryResult(string materialKey, Vector3 offset, int sampleCount)
        {
            MaterialKey = materialKey;
            Offset = offset;
            SampleCount = sampleCount;
        }

        public string MaterialKey { get; }
        public Vector3 Offset { get; }
        public int SampleCount { get; }
    }

    public sealed class OreDiscoverySnapshot
    {
        internal OreDiscoverySnapshot(OreDiscoveryContext context, List<OreDiscoveryResult> results)
        {
            Context = context;
            Results = results.AsReadOnly();
        }

        public OreDiscoveryContext Context { get; }
        public IReadOnlyList<OreDiscoveryResult> Results { get; }
    }

    internal static class OreDiscoveryPlan
    {
        internal const int PerProviderLimit = 32;
        internal const int TotalLimit = 128;

        internal static void Append(List<OreDiscoveryResult> accepted, IReadOnlyList<OreDiscoveryResult> offered, float range)
        {
            int initial = accepted.Count;
            int limit = Math.Min(offered.Count, TotalLimit);
            float squaredRange = range * range;
            for (int index = 0; index < limit && accepted.Count < TotalLimit && accepted.Count - initial < PerProviderLimit; index++)
            {
                var result = offered[index];
                if (result.SampleCount <= 0 || !(result.Offset.LengthSquared() <= squaredRange)) continue;
                try { ContentAllocator.ValidateKey(result.MaterialKey); }
                catch (InvalidOperationException) { continue; }
                accepted.Add(result);
            }
        }
    }
}
