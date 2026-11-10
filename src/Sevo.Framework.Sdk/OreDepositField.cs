using System;

namespace Sevo.Framework.Sdk
{
    /// <summary>A deterministic body-local field with at most one ellipsoidal deposit per cell.</summary>
    public readonly struct OreDepositField
    {
        private readonly ulong seed;
        private readonly double spacing;
        private readonly double radius;
        private readonly double chance;

        public OreDepositField(string key, ulong worldSeed, ulong bodySeed, double spacing, double radius, double chance)
        {
            ContentAllocator.ValidateKey(key);
            if (!double.IsFinite(spacing) || spacing < 0.01 || spacing > 1e8)
                throw new ArgumentOutOfRangeException(nameof(spacing));
            if (!double.IsFinite(radius) || radius <= 0 || radius > spacing / 2)
                throw new ArgumentOutOfRangeException(nameof(radius));
            if (!double.IsFinite(chance) || chance <= 0 || chance > 1)
                throw new ArgumentOutOfRangeException(nameof(chance));

            ulong hash = 14695981039346656037UL;
            foreach (char character in key)
                hash = unchecked((hash ^ character) * 1099511628211UL);
            seed = Mix(hash ^ Mix(worldSeed) ^ Mix(bodySeed ^ 0xa0761d6478bd642fUL));
            this.spacing = spacing;
            this.radius = radius;
            this.chance = chance;
        }

        public bool Contains(double x, double y, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
                || Math.Abs(x) > 1e12 || Math.Abs(y) > 1e12 || Math.Abs(z) > 1e12)
                return false;

            double cx = Math.Floor(x / spacing);
            double cy = Math.Floor(y / spacing);
            double cz = Math.Floor(z / spacing);
            ulong cell = Mix(seed ^ Mix(unchecked((ulong)(long)cx))
                ^ Mix(unchecked((ulong)(long)cy) ^ 0x9e3779b97f4a7c15UL)
                ^ Mix(unchecked((ulong)(long)cz) ^ 0xe7037ed1a0b428dbUL));
            if (Unit(cell) >= chance) return false;

            double jitter = spacing - 2 * radius;
            double dx = (x - cx * spacing - radius - jitter * Unit(Mix(cell ^ 1))) / radius;
            double dy = (y - cy * spacing - radius - jitter * Unit(Mix(cell ^ 2))) / radius;
            double dz = (z - cz * spacing - radius - jitter * Unit(Mix(cell ^ 3))) / radius;
            return dx * dx + dy * dy * 2 + dz * dz < 1;
        }

        private static double Unit(ulong value) => (value >> 11) * (1.0 / 9007199254740992.0);

        private static ulong Mix(ulong value)
        {
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
            return value ^ (value >> 31);
        }
    }
}
