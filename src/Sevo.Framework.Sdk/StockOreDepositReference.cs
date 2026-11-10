using System;

namespace Sevo.Framework.Sdk
{
    /// <summary>A reference derived from a fixed-resolution survey of a named stock ore.</summary>
    public readonly struct StockOreDepositReference
    {
        public string MaterialName { get; }
        public double DepositsPerCubicMetre { get; }
        public double MedianDepositCubicMetres { get; }

        public StockOreDepositReference(string materialName, long depositCount,
            double eligibleCubicMetres, double medianDepositCubicMetres)
        {
            if (string.IsNullOrWhiteSpace(materialName)) throw new ArgumentException("Name the surveyed stock ore.", nameof(materialName));
            if (depositCount <= 0) throw new ArgumentOutOfRangeException(nameof(depositCount));
            if (!double.IsFinite(eligibleCubicMetres) || eligibleCubicMetres <= 0)
                throw new ArgumentOutOfRangeException(nameof(eligibleCubicMetres));
            if (!double.IsFinite(medianDepositCubicMetres) || medianDepositCubicMetres <= 0)
                throw new ArgumentOutOfRangeException(nameof(medianDepositCubicMetres));
            MaterialName = materialName;
            DepositsPerCubicMetre = depositCount / eligibleCubicMetres;
            MedianDepositCubicMetres = medianDepositCubicMetres;
        }

        /// <summary>Converts the measured median volume to an equivalent ellipsoid and occupancy to cell spacing.</summary>
        public (double Spacing, double Radius, double Chance) ToFieldParameters()
        {
            double radius = Math.Pow(MedianDepositCubicMetres * (3 * Math.Sqrt(2) / (4 * Math.PI)), 1.0 / 3);
            double spacing = Math.Max(2 * radius, Math.Pow(0.5 / DepositsPerCubicMetre, 1.0 / 3));
            return (spacing, radius, DepositsPerCubicMetre * spacing * spacing * spacing);
        }
    }
}
