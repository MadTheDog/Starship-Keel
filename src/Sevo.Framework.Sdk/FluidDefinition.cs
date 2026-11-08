using System;

namespace Sevo.Framework.Sdk
{
    /// <summary>Mod-owned metadata for a fluid transported by volume.</summary>
    public sealed class FluidDefinition
    {
        public FluidDefinition(string displayName, uint rgb, float kilogramsPerLitre, float joulesPerLitre)
        {
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
                throw new ArgumentException("A fluid display name of 1 to 128 characters is required.", nameof(displayName));
            if (rgb > 0xffffff)
                throw new ArgumentOutOfRangeException(nameof(rgb), "Use an RGB value in 0xRRGGBB format.");
            if (!float.IsFinite(kilogramsPerLitre) || kilogramsPerLitre <= 0)
                throw new ArgumentOutOfRangeException(nameof(kilogramsPerLitre));
            if (!float.IsFinite(joulesPerLitre) || joulesPerLitre < 0)
                throw new ArgumentOutOfRangeException(nameof(joulesPerLitre));

            DisplayName = displayName;
            Rgb = rgb;
            KilogramsPerLitre = kilogramsPerLitre;
            JoulesPerLitre = joulesPerLitre;
        }

        public string DisplayName { get; }
        public uint Rgb { get; }
        public float KilogramsPerLitre { get; }
        public float JoulesPerLitre { get; }
    }
}
