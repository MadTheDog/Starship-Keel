using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Manager;
using Sevo.Framework.Sdk;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Compiles candidate catalogs without publishing to stock resources or world saves.</summary>
    public static class GalaxyCatalogPreview
    {
        public static bool IsAvailable { get; private set; }
        internal static void Enable() => IsAvailable = true;

        /// <summary>Call on the game thread after native resources load. The stock starting system is always retained.</summary>
        public static GalaxyCatalogPlan SelectStockSubset(Func<GalaxyStarSeed, bool> select)
        {
            RequireAvailability();
            if (select == null) throw new ArgumentNullException(nameof(select));
            return SelectStockSubsetCore(select);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GalaxyCatalogPlan SelectStockSubsetCore(Func<GalaxyStarSeed, bool> select)
        {
            var distribution = AssetRepoGalaxy.Distribution;
            var routes = AssetRepoGalaxy.WarpGateRoutes;
            int sectorCount = GalaxyCatalogPlan.SectorWidth * GalaxyCatalogPlan.SectorWidth;
            if (distribution == null || distribution.StarDatas == null || distribution.StarDatas.Count == 0
                || distribution.StarSeedPointers?.Length != sectorCount || distribution.StarSeedLength?.Length != sectorCount
                || distribution.StarShaderDatas?.Length != distribution.StarDatas.Count || routes == null || routes.StartToEnd == null)
                throw new InvalidOperationException("The stock galaxy catalog is unavailable.");

            var start = Identity(GameIntroductionResources.STARTING_SYSTEM);
            var stars = new List<GalaxyStarSeed>();
            var retained = new HashSet<ulong>();
            foreach (var star in distribution.StarDatas)
            {
                var entry = Identity(star);
                if (entry.Equals(start) || select(entry))
                {
                    stars.Add(entry);
                    retained.Add(entry.Seed);
                }
            }

            return new GalaxyCatalogPlan(stars, routes.StartToEnd.Where(pair =>
                retained.Contains(pair.Key.seed) && retained.Contains(pair.Value.seed))
                .Select(pair => new GalaxyGateRoute(pair.Key.seed, pair.Value.seed)), start);
        }

        /// <summary>Checks the candidate's seeded native positions and builds its stock-format GPU rows offside.</summary>
        public static GalaxyCatalogCompiledPreview Compile(GalaxyCatalogPlan plan)
        {
            RequireAvailability();
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            return CompileCore(plan);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GalaxyCatalogCompiledPreview CompileCore(GalaxyCatalogPlan plan)
        {
            var start = Identity(GameIntroductionResources.STARTING_SYSTEM);
            if (!plan.StarsInSector(start.SectorX, start.SectorZ).Contains(start))
                throw new InvalidOperationException("The native introduction system is absent or moved.");

            var rows = new GalaxyCommon.ShaderStarData[plan.Stars.Count];
            var stars = new Dictionary<ulong, StarData>();
            var cells = new HashSet<(int X, int Z)>();
            var economy = new int[7];
            for (int index = 0; index < plan.Stars.Count; index++)
            {
                var seed = plan.Stars[index];
                var star = new StarData(seed.Seed, new Vector3i(seed.SectorX, seed.SectorY, seed.SectorZ));
                var cell = star.Coordinate;
                if (!cells.Add((cell.x, cell.z)))
                    throw new InvalidOperationException($"Star {seed.Seed} overlaps another star at native map cell {cell.x}/{cell.z}.");
                stars.Add(seed.Seed, star);
                star.SetEconomyScores(economy);
                rows[index] = new GalaxyCommon.ShaderStarData
                {
                    position = star.HolomapPosition,
                    size = star.size,
                    color = star.color,
                    agricultural = economy[0],
                    industrial = economy[1],
                    mining = economy[2],
                    highTech = economy[3],
                    tradeHub = economy[4],
                    frontier = economy[5],
                    tourism = economy[6],
                };
            }

            var gateRows = plan.Routes.Select(route => new GalaxyCommon.ShaderWarpGateRouteData
            {
                startPosition = stars[route.StartSeed].HolomapPosition,
                endPosition = stars[route.EndSeed].HolomapPosition,
                isDiscovered = 1,
            }).ToArray();
            return new GalaxyCatalogCompiledPreview(plan, rows, gateRows);
        }

        private static void RequireAvailability()
        {
            if (!IsAvailable)
                throw new InvalidOperationException("The native galaxy catalog preview contract was not admitted.");
        }

        private static GalaxyStarSeed Identity(StarData star)
            => new GalaxyStarSeed(star.seed, star.sector.x, star.sector.y, star.sector.z);
    }

    /// <summary>Read-only managed data for a catalog preview. The game continues to use its stock catalog.</summary>
    public sealed class GalaxyCatalogCompiledPreview
    {
        public GalaxyCatalogPlan Plan { get; }
        public IReadOnlyList<GalaxyCommon.ShaderStarData> StarRows { get; }
        public IReadOnlyList<GalaxyCommon.ShaderWarpGateRouteData> GateRows { get; }

        internal GalaxyCatalogCompiledPreview(GalaxyCatalogPlan plan, GalaxyCommon.ShaderStarData[] stars,
            GalaxyCommon.ShaderWarpGateRouteData[] gates)
        {
            Plan = plan;
            StarRows = Array.AsReadOnly(stars);
            GateRows = Array.AsReadOnly(gates);
        }
    }
}
