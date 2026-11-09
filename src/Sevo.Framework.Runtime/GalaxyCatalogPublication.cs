using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Manager;
using World;

namespace Sevo.Framework.Runtime
{
    internal sealed class GalaxyCatalogPublication
    {
        private readonly GalaxyStarDistribution distribution;
        private readonly GalaxyWarpGateRoutes routes;
        private readonly GalaxyCatalogCompiledPreview candidate;
        private readonly int[] pointers;
        private readonly int[] lengths;
        private readonly Dictionary<StarData, StarData> forward;
        private readonly Dictionary<StarData, StarData> reverse;
        private readonly List<float> distances;
        internal string Fingerprint { get; }

        internal GalaxyCatalogPublication(GalaxyCatalogCompiledPreview candidate)
        {
            this.candidate = candidate;
            distribution = AssetRepoGalaxy.Distribution;
            routes = AssetRepoGalaxy.WarpGateRoutes;
            int width = Sdk.GalaxyCatalogPlan.SectorWidth;
            pointers = new int[width * width];
            lengths = new int[pointers.Length];
            forward = new Dictionary<StarData, StarData>(candidate.Plan.Routes.Count);
            reverse = new Dictionary<StarData, StarData>(candidate.Plan.Routes.Count);
            distances = new List<float>(candidate.Plan.Routes.Count);
            var endpoints = new HashSet<ulong>();
            foreach (var route in candidate.Plan.Routes)
            {
                endpoints.Add(route.StartSeed);
                endpoints.Add(route.EndSeed);
            }
            var starsBySeed = new Dictionary<ulong, StarData>(endpoints.Count);
            foreach (var star in candidate.NativeStars)
            {
                lengths[star.sector.x + width * star.sector.z]++;
                if (endpoints.Contains(star.seed)) starsBySeed.Add(star.seed, star);
            }
            int offset = 0;
            for (int sector = 0; sector < pointers.Length; sector++)
            {
                pointers[sector] = offset;
                offset += lengths[sector];
            }
            foreach (var route in candidate.Plan.Routes)
            {
                var start = starsBySeed[route.StartSeed];
                var end = starsBySeed[route.EndSeed];
                float distance = (end.GalaxyPosition - start.GalaxyPosition).magnitude * 1280f;
                if (!float.IsFinite(distance) || distance <= 0)
                    throw new InvalidOperationException("A native gate route has an invalid distance.");
                forward.Add(start, end);
                reverse.Add(end, start);
                distances.Add(distance);
            }
            Fingerprint = FingerprintOf(candidate);
        }

        internal bool Contains(StarData star)
        {
            if ((uint)star.sector.x >= Sdk.GalaxyCatalogPlan.SectorWidth || (uint)star.sector.z >= Sdk.GalaxyCatalogPlan.SectorWidth)
                return false;
            int index = star.sector.x + Sdk.GalaxyCatalogPlan.SectorWidth * star.sector.z;
            for (int i = pointers[index]; i < pointers[index] + lengths[index]; i++)
            {
                var row = candidate.NativeStars[i];
                if (row.seed == star.seed && row.sector.y == star.sector.y) return true;
            }
            return false;
        }

        internal void Publish()
        {
            // Installed serialized fields and all loaded map aliases point to these same asset objects.
            // Keep their identities while replacing the managed tables before native entry.
            distribution.StarDatas = candidate.NativeStars;
            distribution.StarShaderDatas = candidate.NativeStarRows;
            distribution.StarSeedPointers = pointers;
            distribution.StarSeedLength = lengths;
            routes.StartToEnd = forward;
            routes.EndToStart = reverse;
            routes.WarpGateRouteDistances = distances;
        }

        private static string FingerprintOf(GalaxyCatalogCompiledPreview candidate)
        {
            using var hash = SHA256.Create();
            using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(1);
            writer.Write(candidate.Plan.Fingerprint);
            foreach (var star in candidate.NativeStars)
            {
                writer.Write(star.position.x);
                writer.Write(star.position.y);
                writer.Write(star.position.z);
            }
            writer.Flush();
            stream.FlushFinalBlock();
            return string.Concat(hash.Hash!.Select(value => value.ToString("x2")));
        }
    }
}
