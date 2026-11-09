using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sevo.Framework.Sdk;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Contributes bounded terrain parameters to the stock solo density generation pipeline.</summary>
    public static class TerrainGenerationProvider
    {
        private static readonly TerrainGenerationCatalog<PlanetTerrainContext, PlanetTerrainOverrides> PlanetProviders =
            new TerrainGenerationCatalog<PlanetTerrainContext, PlanetTerrainOverrides>();
        private static readonly TerrainGenerationCatalog<AsteroidTerrainContext, AsteroidTerrainOverrides> AsteroidProviders =
            new TerrainGenerationCatalog<AsteroidTerrainContext, AsteroidTerrainOverrides>();
        private static Action<string>? planetWarning;
        private static Action<string>? asteroidWarning;
        [ThreadStatic] private static bool resolving;

        public static bool PlanetAvailable => planetWarning != null;
        public static bool AsteroidAvailable => asteroidWarning != null;

        public static void RegisterPlanet(string ownerId, string localName, Func<PlanetTerrainContext, PlanetTerrainOverrides> provider)
            => PlanetProviders.Add(ownerId, localName, provider);

        public static void RegisterAsteroid(string ownerId, string localName, Func<AsteroidTerrainContext, AsteroidTerrainOverrides> provider)
            => AsteroidProviders.Add(ownerId, localName, provider);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void InstallPlanet(Harmony harmony, Action<string> warning)
        {
            var target = AccessTools.DeclaredMethod(typeof(PlanetGenerationLogic), nameof(PlanetGenerationLogic.GetPlanetData),
                new[] { typeof(SolarBody), typeof(int), typeof(bool) })
                ?? throw new MissingMethodException(nameof(PlanetGenerationLogic.GetPlanetData));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainGenerationProvider), nameof(AfterPlanet)));
            planetWarning = warning;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void InstallAsteroid(Harmony harmony, Action<string> warning)
        {
            var target = AccessTools.DeclaredMethod(typeof(AsteroidDensityGenerationData), nameof(AsteroidDensityGenerationData.GetAsteroidDataTemplate),
                new[] { typeof(SolarBody) })
                ?? throw new MissingMethodException(nameof(AsteroidDensityGenerationData.GetAsteroidDataTemplate));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(TerrainGenerationProvider), nameof(AfterAsteroid)));
            asteroidWarning = warning;
        }

        internal static void DisablePlanet() => planetWarning = null;
        internal static void DisableAsteroid() => asteroidWarning = null;

        private static void AfterPlanet(SolarBody __0, int __1, ref PlanetDensityGenerationData __result)
        {
            var warn = planetWarning;
            if (warn == null || resolving || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            resolving = true;
            try
            {
                var data = __result;
                bool hasCanyon = data.canyonFreq > 0f && data.canyonFreq <= 1.5f
                    && data.canyonWarp > 0f && data.canyonWarp <= 1f
                    && data.canyonSmoothOffset > 0f && data.canyonSmoothOffset <= 1f;
                var context = new PlanetTerrainContext(__0.Seed, __1, __0.IsStartingPlanet,
                    data.craterAmount, data.continentThreshold, data.canyonStrength, hasCanyon);
                var proposals = PlanetProviders.Collect(context, warn);
                var crater = PlanetProviders.Resolve(proposals, value => value.CraterAmount, nameof(data.craterAmount), warn);
                var continent = PlanetProviders.Resolve(proposals, value => value.ContinentThreshold, nameof(data.continentThreshold), warn);
                var canyon = PlanetProviders.Resolve(proposals, value => value.CanyonStrength, nameof(data.canyonStrength), warn);
                data.craterAmount = PlanetProviders.ApplyNative(data.craterAmount, crater, 0f, 1f, nameof(data.craterAmount), warn);
                data.continentThreshold = PlanetProviders.ApplyNative(data.continentThreshold, continent, -0.5f, 0.5f,
                    nameof(data.continentThreshold), warn);
                if (hasCanyon)
                    data.canyonStrength = PlanetProviders.ApplyNative(data.canyonStrength, canyon, 0f, 1f, nameof(data.canyonStrength), warn);
                else if (canyon > 0f)
                    PlanetProviders.ReportOnce("no-canyon", "Canyon strength retains its native value where stock canyon parameters are unavailable.", warn);
                __result = data;
            }
            catch (Exception exception)
            {
                DisablePlanet();
                warn("Planet terrain generation disabled: " + exception.Message);
            }
            finally { resolving = false; }
        }

        private static void AfterAsteroid(SolarBody __0, ref AsteroidDensityGenerationData __result)
        {
            var warn = asteroidWarning;
            if (warn == null || resolving || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            resolving = true;
            try
            {
                var data = __result;
                var context = new AsteroidTerrainContext(__0.Seed, __0.planetId, __0.IsStartingPlanet,
                    data.craterAmount, data.craterScaleFactor);
                var proposals = AsteroidProviders.Collect(context, warn);
                var crater = AsteroidProviders.Resolve(proposals, value => value.CraterAmount, nameof(data.craterAmount), warn);
                var scale = AsteroidProviders.Resolve(proposals, value => value.CraterScaleFactor, nameof(data.craterScaleFactor), warn);
                data.craterAmount = AsteroidProviders.ApplyNative(data.craterAmount, crater, 0f, 0.5f, nameof(data.craterAmount), warn);
                data.craterScaleFactor = AsteroidProviders.ApplyNative(data.craterScaleFactor, scale, 0.25f, 0.5f,
                    nameof(data.craterScaleFactor), warn);
                __result = data;
            }
            catch (Exception exception)
            {
                DisableAsteroid();
                warn("Asteroid terrain generation disabled: " + exception.Message);
            }
            finally { resolving = false; }
        }
    }
}
