using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using World;

namespace Sevo.Example.CelestialLayout;

[BepInPlugin(Owner, "Keel Celestial Layout Probe", "0.1.0")]
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
public sealed partial class Plugin : BaseUnityPlugin
{
    private const string Owner = "example.celestial-layout";
    private const ulong MoonSeed = 0xCAB451DE014BB227;
    private const ulong PlanetSeed = 0xB892406DAA095127;
    private const float PlanetRadius = 80000;
    private const float PlanetOrbitRadius = 40000000;
    private static Plugin instance = null!;
    private StarData target;
    private List<AstronData>? stock;
    private ulong moonParentSeed;
    private bool checkedLayout;
    private bool checkedDestination;
    private bool acceptedLayout;
    private bool acceptedDestination;
    private string? destinationSignature;

    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("SEVO_KEEL_CELESTIAL_LAYOUT_PROBE") != "1") return;
        if (UnityEngine.Application.companyName != "SEVO Keel Gen Lab 0110"
            || UnityEngine.Application.productName != "SEVO WG Test") return;
        instance = this;
        var target = AccessTools.DeclaredMethod(typeof(AssetRepoGalaxy), nameof(AssetRepoGalaxy.LoadResources));
        var patch = AccessTools.DeclaredMethod(typeof(Plugin), nameof(AfterResources));
        new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(patch) { priority = Priority.Last });
        ConfigureTravelFixture();
    }

    private static void AfterResources()
    {
        if (instance.stock != null || AssetRepoGalaxy.Distribution?.StarDatas == null) return;
        try { instance.PrepareFixture(); }
        catch (Exception exception) { instance.Logger.LogError("KEEL_CELESTIAL_LAYOUT_PROBE FAIL " + exception); }
    }

    private void PrepareFixture()
    {
        foreach (var star in AssetRepoGalaxy.Distribution.StarDatas.Take(500))
        {
            if (GameIntroductionResources.IsStartingSystem(star)) continue;
            var bodies = SolarBodyGenerator.GetStarSystemBodiesData(star);
            if (bodies.Count > 6 || bodies.Any(body => body.seed == MoonSeed || body.seed == PlanetSeed)
                || bodies.Select(body => body.seed).Distinct().Count() != bodies.Count) continue;
            var planet = bodies.FirstOrDefault(body => body.orbitalBodyType == OrbitalBodyType.planet
                && !body.hasRing && body.radius <= 150000 && bodies.All(child => child.id == 0 || child.parentId != body.id));
            if (planet.id == 0) continue;
            CelestialLayoutProvider.Register(Owner, "moon", CelestialLayoutRule.AddRockyMoon(
                star.seed, planet.seed, MoonSeed, 18000, 4 * planet.radius));
            CelestialLayoutProvider.Register(Owner, "planet", CelestialLayoutRule.AddRockyPlanet(
                star.seed, PlanetSeed, PlanetRadius, PlanetOrbitRadius));
            target = star;
            stock = bodies;
            moonParentSeed = planet.seed;
            return;
        }
        Logger.LogError("KEEL_CELESTIAL_LAYOUT_PROBE FAIL no bounded stock fixture");
    }

    private void Update()
    {
        AdvanceReloadPicker();
        if (stock == null || PlayerState.sessionMode != PlayerState.SessionMode.solo
            || PlayerState.InLoadingScreen || PlanetarySystem.InSystemTransition || PlanetarySystem.SolarBodies.Count == 0) return;
        if (!checkedLayout) CheckCatalog();
        if (!checkedDestination && PlanetarySystem.CurrentStarData.seed == target.seed) CheckDestination();
        CheckDestinationSave();
    }

    private void CheckCatalog()
    {
        checkedLayout = true;
        try
        {
            var world = SolarBodyGenerator.GetStarSystemBodiesData(target);
            var lookup = typeof(MissionToolbox).GetMethod("GetCelestialBodyDatas", BindingFlags.Static | BindingFlags.NonPublic);
            var mission = (List<AstronData>)lookup!.Invoke(null, new object[] { target })!;
            VerifyCatalog(world);
            if (!world.Select(body => (body.id, body.seed, body.systemPosition))
                    .SequenceEqual(mission.Select(body => (body.id, body.seed, body.systemPosition))))
                throw new InvalidOperationException("The native mission lookup and system generator disagree.");
            int points = VerifyDeepSpacePoints(world, MissionToolbox.GetStarPoiDatas(target));
            acceptedLayout = true;
            Logger.LogInfo($"KEEL_CELESTIAL_LAYOUT_PROBE PASS stock={stock!.Count} resolved={world.Count} planet={PlanetSeed} moon={MoonSeed} points={points} system={target.seed}");
        }
        catch (Exception exception)
        {
            Logger.LogError("KEEL_CELESTIAL_LAYOUT_PROBE FAIL " + exception);
        }
    }

    private void CheckDestination()
    {
        checkedDestination = true;
        try
        {
            var catalog = PlanetarySystem.celestialBodies;
            var bodies = PlanetarySystem.SolarBodies;
            VerifyCatalog(catalog);
            if (!bodies.Select(body => (body.planetId, body.Seed, body.SystemPosition))
                    .SequenceEqual(catalog.Select(body => (body.id, body.seed, body.systemPosition)))
                || bodies[stock!.Count + 1].parent != bodies[0]
                || bodies[stock.Count + 1].instanceGo == null || bodies[stock.Count].instanceGo == null)
                throw new InvalidOperationException("The native destination did not instantiate the declared bodies in catalog order.");
            int points = VerifyDeepSpacePoints(catalog, PlanetarySystem.brickEntityPoiDatas);
            destinationSignature = CatalogSignature(catalog);
            acceptedDestination = true;
            Logger.LogInfo($"KEEL_CELESTIAL_DESTINATION_PROBE PASS stock={stock.Count} resolved={bodies.Count} planet={PlanetSeed} moon={MoonSeed} points={points} system={target.seed} layout={destinationSignature}");
        }
        catch (Exception exception)
        {
            Logger.LogError("KEEL_CELESTIAL_DESTINATION_PROBE FAIL " + exception);
        }
    }

    private static string CatalogSignature(IEnumerable<AstronData> bodies)
    {
        var rows = bodies.Select(body => FormattableString.Invariant(
            $"{body.id}:{body.parentId}:{body.seed}:{body.radius:R}:{body.orbitSemiRadius:R}:{body.systemPosition.x}:{body.systemPosition.y}:{body.systemPosition.z}"));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows)))).Replace("-", "");
    }

    private void VerifyCatalog(List<AstronData> bodies)
    {
        int count = stock!.Count;
        if (bodies.Count != count + 2 || !bodies.Take(count).Select(body => (body.id, body.seed))
                .SequenceEqual(stock.Select(body => (body.id, body.seed))))
            throw new InvalidOperationException("The stock body slots changed or an appended body is missing.");
        var moon = bodies[count];
        var planet = bodies[count + 1];
        if (moon.id != count || moon.seed != MoonSeed || moon.orbitalBodyType != OrbitalBodyType.moon
            || (uint)moon.parentId >= (uint)count || bodies[moon.parentId].seed != moonParentSeed
            || planet.id != count + 1 || planet.seed != PlanetSeed || planet.orbitalBodyType != OrbitalBodyType.planet
            || planet.parentId != 0 || planet.celestialBodyType != AstronType.telluric
            || planet.radius != PlanetRadius || planet.orbitSemiRadius != PlanetOrbitRadius)
            throw new InvalidOperationException("The appended planet or moon does not match its declaration.");
    }

    private static int VerifyDeepSpacePoints(List<AstronData> bodies, List<BrickEntityPOIData> points)
    {
        int count = 0;
        foreach (var point in points.Where(point => point.stationType is StationType.pointOfInterest or StationType.debrisField))
        {
            if ((uint)point.planetId >= (uint)bodies.Count)
                throw new InvalidOperationException("A deep-space point refers outside the body catalog.");
            var planet = bodies[point.planetId];
            float distanceInRadii = (point.systemPosition - planet.systemPosition).Magnitude / planet.radius;
            if (planet.orbitalBodyType != OrbitalBodyType.planet || distanceInRadii is < 2.19f or > 6.4f)
                throw new InvalidOperationException("A deep-space point refers to the wrong native orbit body.");
            count++;
        }
        if (count == 0) throw new InvalidOperationException("No native deep-space points were available to check.");
        return count;
    }
}
