using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using World;

namespace Sevo.Example.CelestialLayout;

[BepInPlugin(Owner, "Keel Celestial Layout Probe", "0.1.0")]
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Owner = "example.celestial-layout";
    private const ulong MoonSeed = 0xCAB451DE014BB227;
    private static Plugin instance = null!;
    private StarData target;
    private List<AstronData>? stock;
    private bool checkedLayout;

    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("SEVO_KEEL_CELESTIAL_LAYOUT_PROBE") != "1") return;
        if (UnityEngine.Application.companyName != "SEVO Keel Gen Lab 0110"
            || UnityEngine.Application.productName != "SEVO WG Test") return;
        instance = this;
        var target = AccessTools.DeclaredMethod(typeof(AssetRepoGalaxy), nameof(AssetRepoGalaxy.LoadResources));
        var patch = AccessTools.DeclaredMethod(typeof(Plugin), nameof(AfterResources));
        new Harmony(Owner).Patch(target, postfix: new HarmonyMethod(patch) { priority = Priority.Last });
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
            if (bodies.Count >= 8 || bodies.Select(body => body.seed).Distinct().Count() != bodies.Count) continue;
            var planet = bodies.FirstOrDefault(body => body.orbitalBodyType == OrbitalBodyType.planet
                && !body.hasRing && body.radius <= 150000 && bodies.All(child => child.id == 0 || child.parentId != body.id));
            if (planet.id == 0) continue;
            CelestialLayoutProvider.Register(Owner, "moon", CelestialLayoutRule.AddRockyMoon(
                star.seed, planet.seed, MoonSeed, 18000, 4 * planet.radius));
            target = star;
            stock = bodies;
            return;
        }
        Logger.LogError("KEEL_CELESTIAL_LAYOUT_PROBE FAIL no bounded stock fixture");
    }

    private void Update()
    {
        if (checkedLayout || stock == null || PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
        checkedLayout = true;
        try
        {
            var world = SolarBodyGenerator.GetStarSystemBodiesData(target);
            var lookup = typeof(MissionToolbox).GetMethod("GetCelestialBodyDatas", BindingFlags.Static | BindingFlags.NonPublic);
            var mission = (List<AstronData>)lookup.Invoke(null, new object[] { target });
            bool stable = world.Take(stock.Count).Select(body => (body.id, body.seed))
                .SequenceEqual(stock.Select(body => (body.id, body.seed)));
            if (!stable || world.Count != stock.Count + 1 || world.Last().seed != MoonSeed
                || !world.Select(body => (body.id, body.seed, body.systemPosition))
                    .SequenceEqual(mission.Select(body => (body.id, body.seed, body.systemPosition))))
                throw new InvalidOperationException("The native mission and system generator did not agree on the appended moon.");
            Logger.LogInfo($"KEEL_CELESTIAL_LAYOUT_PROBE PASS stock={stock.Count} resolved={world.Count} system={target.seed}");
        }
        catch (Exception exception)
        {
            Logger.LogError("KEEL_CELESTIAL_LAYOUT_PROBE FAIL " + exception);
        }
    }
}
