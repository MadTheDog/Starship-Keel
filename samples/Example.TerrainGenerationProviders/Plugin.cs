using BepInEx;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using World;
using World.Terrain.Planet;

namespace Sevo.Example.TerrainGenerationProviders
{
    [BepInPlugin(Id, "Terrain Generation Provider Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class TerrainGenerationProbe : BaseUnityPlugin
    {
        private const string Id = "example.terrain-providers";
        private const float PlanetCrater = 0.8125f;
        private const float RingCraterScale = 0.375f;
        private bool planetObserved;
        private bool ringObserved;

        private void Awake()
        {
            TerrainGenerationProvider.RegisterPlanet(Id, "planet-relief", _ => new PlanetTerrainOverrides(craterAmount: PlanetCrater));
            TerrainGenerationProvider.RegisterAsteroid(Id, "ring-relief", _ => new AsteroidTerrainOverrides(craterScaleFactor: RingCraterScale));
            PlanetarySystem.OnNewSystem += ReadNativeTables;
        }

        private void OnDestroy() => PlanetarySystem.OnNewSystem -= ReadNativeTables;

        private void ReadNativeTables()
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            foreach (var body in PlanetarySystem.SolarBodies)
            {
                if (!planetObserved && TerrainGenerationProvider.PlanetAvailable && body.planetGenerationId > 0
                    && PlanetResources.Datas.IsCreated && body.planetGenerationId < PlanetResources.Datas.Length)
                {
                    var data = PlanetResources.Datas[body.planetGenerationId];
                    planetObserved = true;
                    if (data.craterAmount == PlanetCrater)
                        Logger.LogInfo("PLANET TERRAIN PROVIDER PROBE PASS: crater amount reached the native planet table.");
                    else
                        Logger.LogError("PLANET TERRAIN PROVIDER PROBE FAIL: native crater amount was " + data.craterAmount);
                }
                if (!ringObserved && TerrainGenerationProvider.AsteroidAvailable && body.asteroidGenerationId > 0
                    && PlanetResources.AsteroidGenDatas.IsCreated && body.asteroidGenerationId < PlanetResources.AsteroidGenDatas.Length)
                {
                    var data = PlanetResources.AsteroidGenDatas[body.asteroidGenerationId];
                    ringObserved = true;
                    if (data.craterScaleFactor == RingCraterScale)
                        Logger.LogInfo("ASTEROID TERRAIN PROVIDER PROBE PASS: crater scale reached the native ring template table.");
                    else
                        Logger.LogError("ASTEROID TERRAIN PROVIDER PROBE FAIL: native crater scale was " + data.craterScaleFactor);
                }
            }
            if (planetObserved && ringObserved) PlanetarySystem.OnNewSystem -= ReadNativeTables;
        }
    }
}
