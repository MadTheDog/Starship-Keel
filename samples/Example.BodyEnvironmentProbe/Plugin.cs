using System;
using System.Linq;
using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Terrain;
using World.Terrain.Planet;

namespace Sevo.Example.BodyEnvironmentProbe
{
    [BepInPlugin(Id, "Example Body Environment Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Id = "example.bodyenvironmentprobe";
        private const float Water = 0.63f;
        private const float Atmosphere = 0.58f;
        private const float Oxygen = 0.29f;
        private const float MoonGravity = 0.15f;
        private ulong system;
        private ulong wetBody;
        private ulong moon;
        private bool observing;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_BODY_ENVIRONMENT_PROBE") != "1") return;

            var star = GameIntroductionResources.STARTING_SYSTEM;
            var stock = SolarBodyGenerator.GetStarSystemBodiesData(star);
            var planet = stock.FirstOrDefault(body => body.orbitalBodyType == OrbitalBodyType.planet
                && body.celestialBodyType == AstronType.telluric);
            var rockyMoon = stock.FirstOrDefault(body => body.orbitalBodyType == OrbitalBodyType.moon
                && body.celestialBodyType == AstronType.telluric);
            if (!planet.IsValid || !rockyMoon.IsValid)
            {
                Logger.LogError("KEEL_BODY_ENVIRONMENT_PROBE FAIL: Stock test system has no rocky planet or moon.");
                return;
            }

            system = star.seed;
            wetBody = planet.seed;
            moon = rockyMoon.seed;
            BodyEnvironmentProvider.Register(Id, "ocean-and-air", new BodyEnvironmentTarget(system, wetBody),
                new BodyEnvironmentOverride(waterAmount: Water, atmosphereDensity: Atmosphere, oxygenDensity: Oxygen,
                    atmosphereRgb: 0x618bc0, oceanMainRgb: 0x385b65, oceanSecondaryRgb: 0x5c8795, oceanDepthRgb: 0x173040));
            BodyEnvironmentProvider.Register(Id, "moon-gravity", new BodyEnvironmentTarget(system, moon),
                new BodyEnvironmentOverride(gravityScale: MoonGravity));
            PlanetarySystem.OnNewSystem += CheckBodies;
            observing = true;
        }

        private void OnDestroy()
        {
            if (observing) PlanetarySystem.OnNewSystem -= CheckBodies;
        }

        private void CheckBodies()
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.solo || PlanetarySystem.CurrentStarData.seed != system) return;

            PlanetarySystem.OnNewSystem -= CheckBodies;
            observing = false;
            try
            {
                Require(BodyEnvironmentProvider.IsAvailable, "The runtime did not admit the environment hook.");
                var planet = PlanetarySystem.SolarBodies.Single(body => body.Seed == wetBody);
                var rockyMoon = PlanetarySystem.SolarBodies.Single(body => body.Seed == moon);
                var data = PlanetResources.Datas[planet.planetGenerationId];
                var physics = PlanetResources.PlanetGravityDatas[planet.gravityId];
                var moonPhysics = PlanetResources.PlanetGravityDatas[rockyMoon.gravityId];
                Require(data.waterAmount == Water && data.atmosphereDensity == Atmosphere && data.oxygenDensity == Oxygen,
                    "The declared settings did not reach the native planet payload.");
                Require(planet.systemBody.planetGenerationData.waterAmount == Water
                    && planet.systemBody.planetGenerationData.atmosphereDensity == Atmosphere,
                    "The native renderer did not receive the declared settings.");
                Require(data.HasOcean && Math.Abs(data.oceanRadius - planet.radius * Mathf.Lerp(0.975f, 1.004f, Water)) < 0.1f
                    && planet.parentWaterNodeIds.All(id => id > 0 && OceanNodeData.NodeDatas[id].radius == data.oceanRadius),
                    "The native ocean nodes did not use the stock radius derived from the override.");
                Require(physics.oceanRadius == data.oceanRadius && physics.atmosphereDensity == Atmosphere
                    && physics.oxygenDensity == Oxygen && moonPhysics.g == MoonGravity
                    && Math.Abs(physics.g - Mathf.Sqrt(planet.radius / 120000.01f)) < 0.0001f,
                    "Native physics rows did not apply the declared settings and preserve untargeted gravity.");
                Require(data.planetColorPalette.atmosphereColor.Equals(new Color32(0x61, 0x8b, 0xc0, 0xff))
                    && data.planetColorPalette.oceanColorMain.Equals(new Color32(0x38, 0x5b, 0x65, 0xff))
                    && data.planetColorPalette.oceanColorSec.Equals(new Color32(0x5c, 0x87, 0x95, 0xff))
                    && data.planetColorPalette.oceanColorDepth.Equals(new Color32(0x17, 0x30, 0x40, 0xff))
                    && Math.Abs(data.atmosphereColor.x - 0x61 / 255f) < 0.0001f,
                    "Native palette colors and their derived atmosphere color diverged.");
                Logger.LogInfo($"KEEL_BODY_ENVIRONMENT_PROBE PASS planet={wetBody} moon={moon} ocean={data.oceanRadius} moonGravity={moonPhysics.g}");
            }
            catch (Exception exception)
            {
                Logger.LogError("KEEL_BODY_ENVIRONMENT_PROBE FAIL: " + exception);
            }
        }

        private static void Require(bool passed, string reason)
        {
            if (!passed) throw new InvalidOperationException(reason);
        }
    }
}
