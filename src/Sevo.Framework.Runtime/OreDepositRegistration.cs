using System;
using System.Collections.Generic;
using System.Linq;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    /// <summary>Declares independent deposits in fresh solo terrain using a stock non-ore host material.</summary>
    public static class OreDepositRegistration
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Declaration> Rules = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        private static Declaration[]? sealedRules;

        public static bool IsAvailable => TerrainPlacement.AreRadialBandsAvailable && TerrainMaterialRegistration.IsEnabled;

        public static void Register(string ownerId, string localName, TerrainPlacementScope scope,
            string hostMaterial, string materialKey, StockOreDepositReference stockReference,
            double? maximumNominalDepth = null)
        {
            var (spacing, radius, chance) = stockReference.ToFieldParameters();
            Register(ownerId, localName, scope, hostMaterial, materialKey, spacing, radius, chance, maximumNominalDepth);
        }

        public static void Register(string ownerId, string localName, TerrainPlacementScope scope,
            string hostMaterial, string materialKey, double spacing, double radius, double chance,
            double? maximumNominalDepth = null)
        {
            string key = ownerId + ":" + localName;
            _ = new OreDepositField(key, 0, 0, spacing, radius, chance);
            ContentAllocator.ValidateKey(materialKey);
            if (scope == 0 || (scope & ~TerrainPlacementScope.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(scope));
            if (hostMaterial is not ("TEX_ROCK_A" or "TEX_ROCK_B" or "LARGE_ROCK_A" or "LARGE_ROCK_B" or "DIRTY_ROCK_A" or "DIRTY_ROCK_B"))
                throw new ArgumentException("A stock host shader name is required.", nameof(hostMaterial));
            if (maximumNominalDepth.HasValue && (scope != TerrainPlacementScope.Planets
                || !double.IsFinite(maximumNominalDepth.Value) || maximumNominalDepth.Value <= 0))
                throw new ArgumentOutOfRangeException(nameof(maximumNominalDepth), "Nominal depth requires planet scope.");

            lock (Gate)
            {
                if (sealedRules != null) throw new InvalidOperationException("Ore deposit registration is closed.");
                Rules.Add(key, new Declaration(key, scope, hostMaterial, materialKey, spacing, radius, chance, maximumNominalDepth));
            }
        }

        internal static Declaration[] Seal()
        {
            lock (Gate) return sealedRules ??= Rules.Values.OrderBy(rule => rule.Key, StringComparer.Ordinal).ToArray();
        }

        internal sealed class Declaration
        {
            internal readonly string Key;
            internal readonly TerrainPlacementScope Scope;
            internal readonly string Host;
            internal readonly string Material;
            internal readonly double Spacing;
            internal readonly double Radius;
            internal readonly double Chance;
            internal readonly double? MaximumNominalDepth;

            internal Declaration(string key, TerrainPlacementScope scope, string host, string material,
                double spacing, double radius, double chance, double? maximumNominalDepth)
            {
                Key = key;
                Scope = scope;
                Host = host;
                Material = material;
                Spacing = spacing;
                Radius = radius;
                Chance = chance;
                MaximumNominalDepth = maximumNominalDepth;
            }
        }
    }
}
