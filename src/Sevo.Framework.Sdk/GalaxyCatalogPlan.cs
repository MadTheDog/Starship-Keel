using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Sevo.Framework.Sdk
{
    /// <summary>Native star identity and its sector. The game derives its subcell and appearance from the seed.</summary>
    public readonly struct GalaxyStarSeed : IEquatable<GalaxyStarSeed>
    {
        public ulong Seed { get; }
        public int SectorX { get; }
        public int SectorY { get; }
        public int SectorZ { get; }

        public GalaxyStarSeed(ulong seed, int sectorX, int sectorY, int sectorZ)
        {
            if (seed == 0) throw new ArgumentOutOfRangeException(nameof(seed));
            GalaxyCatalogPlan.SectorIndex(sectorX, sectorZ);
            if ((uint)sectorY >= GalaxyCatalogPlan.VerticalSectorCount) throw new ArgumentOutOfRangeException(nameof(sectorY));

            Seed = seed;
            SectorX = sectorX;
            SectorY = sectorY;
            SectorZ = sectorZ;
        }

        public bool Equals(GalaxyStarSeed other)
            => Seed == other.Seed && SectorX == other.SectorX && SectorY == other.SectorY && SectorZ == other.SectorZ;
        public override bool Equals(object? obj) => obj is GalaxyStarSeed other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Seed, SectorX, SectorY, SectorZ);
    }

    /// <summary>One directed route between two native star identities.</summary>
    public readonly struct GalaxyGateRoute
    {
        public ulong StartSeed { get; }
        public ulong EndSeed { get; }

        public GalaxyGateRoute(ulong startSeed, ulong endSeed)
        {
            if (startSeed == 0 || endSeed == 0 || startSeed == endSeed)
                throw new ArgumentException("A gate route requires two distinct nonzero star seeds.");
            StartSeed = startSeed;
            EndSeed = endSeed;
        }
    }

    /// <summary>Immutable candidate catalog for the stock sector dimensions. Native subcells require preview validation.</summary>
    public sealed class GalaxyCatalogPlan
    {
        public const int SectorWidth = 128;
        public const int VerticalSectorCount = 4;
        public const int MaxStars = 2_000_000;

        private readonly GalaxyStarSeed[] stars;
        private readonly int[] pointers = new int[SectorWidth * SectorWidth];
        private readonly int[] lengths = new int[SectorWidth * SectorWidth];

        public IReadOnlyList<GalaxyStarSeed> Stars { get; }
        public IReadOnlyList<GalaxyGateRoute> Routes { get; }
        public string Fingerprint { get; }

        public GalaxyCatalogPlan(IEnumerable<GalaxyStarSeed> stars, IEnumerable<GalaxyGateRoute> routes,
            GalaxyStarSeed requiredStartingSystem)
        {
            if (stars == null) throw new ArgumentNullException(nameof(stars));
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            var seedList = stars.Take(MaxStars + 1).ToArray();
            if (seedList.Length == 0 || seedList.Length > MaxStars)
                throw new ArgumentException("The catalog star count is outside the supported range.", nameof(stars));

            var seen = new HashSet<ulong>();
            bool startFound = false;
            foreach (var entry in seedList)
            {
                if (entry.Seed == 0 || !seen.Add(entry.Seed))
                    throw new ArgumentException("The catalog contains a zero or repeated star seed.", nameof(stars));
                startFound |= entry.Equals(requiredStartingSystem);
            }
            if (!startFound) throw new ArgumentException("The catalog must contain the required starting system at its original sector.", nameof(stars));

            this.stars = seedList.OrderBy(entry => SectorIndex(entry.SectorX, entry.SectorZ)).ThenBy(entry => entry.Seed).ToArray();
            Stars = Array.AsReadOnly(this.stars);
            var routeList = routes.Take(seedList.Length / 2 + 1).OrderBy(route => route.StartSeed).ToArray();
            var endpoints = new HashSet<ulong>();
            foreach (var route in routeList)
            {
                if (!seen.Contains(route.StartSeed) || !seen.Contains(route.EndSeed)
                    || !endpoints.Add(route.StartSeed) || !endpoints.Add(route.EndSeed))
                    throw new ArgumentException("A route endpoint is missing or reused.", nameof(routes));
            }
            Routes = Array.AsReadOnly(routeList);

            foreach (var entry in this.stars)
                lengths[SectorIndex(entry.SectorX, entry.SectorZ)]++;
            for (int i = 1; i < pointers.Length; i++)
                pointers[i] = pointers[i - 1] + lengths[i - 1];

            Fingerprint = ComputeFingerprint(this.stars, routeList);
        }

        public IEnumerable<GalaxyStarSeed> StarsInSector(int sectorX, int sectorZ)
        {
            int sector = SectorIndex(sectorX, sectorZ);
            for (int i = pointers[sector]; i < pointers[sector] + lengths[sector]; i++)
                yield return stars[i];
        }

        internal static int SectorIndex(int x, int z)
        {
            if ((uint)x >= SectorWidth) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)z >= SectorWidth) throw new ArgumentOutOfRangeException(nameof(z));
            return x + SectorWidth * z;
        }

        private static string ComputeFingerprint(GalaxyStarSeed[] stars, GalaxyGateRoute[] routes)
        {
            using var hash = SHA256.Create();
            using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(1);
            writer.Write(stars.Length);
            foreach (var star in stars)
            {
                writer.Write(star.Seed);
                writer.Write(star.SectorX);
                writer.Write(star.SectorY);
                writer.Write(star.SectorZ);
            }
            writer.Write(routes.Length);
            foreach (var route in routes)
            {
                writer.Write(route.StartSeed);
                writer.Write(route.EndSeed);
            }
            writer.Flush();
            stream.FlushFinalBlock();
            return string.Concat(hash.Hash!.Select(value => value.ToString("x2")));
        }
    }
}
