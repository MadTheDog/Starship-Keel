using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Manager;
using Mono.Cecil;
using Sevo.Framework.Sdk;
using UnityEngine;
using UnityEngine.SceneManagement;
using World;
using World.Galaxy;

namespace Sevo.Framework.Runtime
{
    /// <summary>Compiles candidate catalogs without publishing to stock resources or world saves.</summary>
    public static class GalaxyCatalogPreview
    {
        public static bool IsAvailable { get; private set; }
        internal static void Enable() => IsAvailable = true;

        /// <summary>Audits the installed and loaded stock catalog references before native character entry.</summary>
        public static CapabilityResult AuditPreEntrySource()
        {
            try
            {
                RequireAvailability();
                return AuditPreEntrySourceCore();
            }
            catch (Exception exception)
            {
                return new CapabilityResult("galaxyCatalogPreEntrySource", false, exception.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CapabilityResult AuditPreEntrySourceCore()
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.mainMenu)
                throw new InvalidOperationException("Audit galaxy bindings in the main menu before character entry.");
            string data = ProfileProtocol.Canonical(Application.dataPath);
            string managed = Path.Combine(data, "Managed", "Assembly-CSharp.dll");
            if (!string.Equals(ProfileProtocol.Canonical(typeof(StarData).Assembly.Location), managed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The loaded game assembly does not belong to the installed asset source.");
            int scenes = SceneManager.sceneCountInBuildSettings;
            if (scenes <= 0 || scenes > 128 || Enumerable.Range(0, scenes).Any(index => !File.Exists(Path.Combine(data, "level" + index))))
                throw new InvalidOperationException("The installed asset graph does not cover the player scene list.");
            ProfileProtocol.NoReparse(managed);
            using var assembly = AssemblyDefinition.ReadAssembly(managed);
            var contract = ContractAudit.EvaluateGalaxyCatalogPreEntry(assembly.MainModule);
            if (!contract.Available) throw new InvalidOperationException(contract.Reason);
            var bindings = GalaxyCatalogAssetContract.ReadInstalled(assembly.MainModule, data);
            var source = GalaxyCatalogAssetContract.VerifyBindings(bindings);

            const BindingFlags privateFields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var repoCatalog = typeof(AssetRepoGalaxy).GetField("distribution", privateFields);
            var repoRoutes = typeof(AssetRepoGalaxy).GetField("warpGateRoutes", privateFields);
            var routeCatalog = typeof(GalaxyWarpGateRoutes).GetField("distribution", privateFields);
            var mapCatalog = typeof(GalaxyMap).GetField("starDistribution", privateFields);
            var renderer = typeof(GalaxyMap).GetField("starsRenderer", privateFields);
            var repositories = Resources.FindObjectsOfTypeAll<AssetRepoGalaxy>();
            var maps = Resources.FindObjectsOfTypeAll<GalaxyMap>();
            var stock = AssetRepoGalaxy.Distribution;
            var routes = AssetRepoGalaxy.WarpGateRoutes;
            if (stock == null || routes == null || repositories.Length != 1 || repoCatalog == null || repoRoutes == null
                || routeCatalog == null || mapCatalog == null || renderer == null
                || !ReferenceEquals(repoCatalog.GetValue(repositories[0]), stock)
                || !ReferenceEquals(repoRoutes.GetValue(repositories[0]), routes)
                || !ReferenceEquals(routeCatalog.GetValue(routes), stock)
                || maps.Any(map => !ReferenceEquals(mapCatalog.GetValue(map), stock) || renderer.GetValue(map) != null))
                throw new InvalidOperationException("The loaded galaxy aliases are inconsistent or a map renderer already cached them.");

            return new CapabilityResult("galaxyCatalogPreEntrySource", true,
                "Installed galaxy source " + source + " and current runtime aliases agree before character entry.");
        }

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
            if (GalaxyCatalogProvider.IsPublished)
                throw new InvalidOperationException("Restart before selecting a subset of the stock galaxy.");
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
            var nativeStars = new List<StarData>(plan.Stars.Count);
            var endpoints = new HashSet<ulong>(plan.Routes.SelectMany(route => new[] { route.StartSeed, route.EndSeed }));
            var stars = new Dictionary<ulong, StarData>(endpoints.Count);
            var cells = new HashSet<(int X, int Z)>();
            var economy = new int[7];
            for (int index = 0; index < plan.Stars.Count; index++)
            {
                var seed = plan.Stars[index];
                var star = seed.Equals(start) ? GameIntroductionResources.STARTING_SYSTEM
                    : new StarData(seed.Seed, new Vector3i(seed.SectorX, seed.SectorY, seed.SectorZ));
                var cell = star.Coordinate;
                if (!cells.Add((cell.x, cell.z)))
                    throw new InvalidOperationException($"Star {seed.Seed} overlaps another star at native map cell {cell.x}/{cell.z}.");
                nativeStars.Add(star);
                if (endpoints.Contains(seed.Seed)) stars.Add(seed.Seed, star);
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
            return new GalaxyCatalogCompiledPreview(plan, nativeStars, rows, gateRows);
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
        internal List<StarData> NativeStars { get; }
        internal GalaxyCommon.ShaderStarData[] NativeStarRows { get; }

        internal GalaxyCatalogCompiledPreview(GalaxyCatalogPlan plan, List<StarData> nativeStars, GalaxyCommon.ShaderStarData[] stars,
            GalaxyCommon.ShaderWarpGateRouteData[] gates)
        {
            Plan = plan;
            NativeStars = nativeStars;
            NativeStarRows = stars;
            StarRows = Array.AsReadOnly(stars);
            GateRows = Array.AsReadOnly(gates);
        }
    }
}
