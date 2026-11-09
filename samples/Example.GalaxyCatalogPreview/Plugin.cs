using System;
using System.Linq;
using BepInEx;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using Manager.Save;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Galaxy;
using World.Starfield;

namespace Sevo.Example.GalaxyPreviewProbe
{
    [BepInPlugin("example.galaxycatalogpreview", "Example Galaxy Catalog Preview", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        private const string DiagnosticOwner = "example.galaxycatalogpreview.lifecycle";
        private static Plugin? active;
        private bool checkedCatalog;

        private void Awake()
        {
            SetUpPublicationProbe();
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_BINDING_PROBE") != "1") return;
            active = this;
            var harmony = new Harmony(DiagnosticOwner);
            Watch(typeof(GalaxyMap), "Awake", nameof(BeforeMapAwake));
            Watch(typeof(PlayerSave_mp), nameof(PlayerSave_mp.Load), nameof(BeforeWorldLoad));
            Watch(typeof(FactionTerritoryMap), nameof(FactionTerritoryMap.LoadFromDisk), nameof(BeforeFactionLoad));
            Watch(typeof(StarfieldParticle), "OnNewSystem", nameof(BeforeStarfield));

            void Watch(Type type, string method, string prefix)
                => harmony.Patch(AccessTools.DeclaredMethod(type, method),
                    prefix: new HarmonyMethod(typeof(Plugin), prefix) { priority = Priority.First });
        }

        private void OnDestroy()
        {
            StopPublicationHarness();
            PlanetarySystem.OnNewSystem -= ObserveLoadedBindings;
            if (active != this) return;
            new Harmony(DiagnosticOwner).UnpatchSelf();
            active = null;
        }

        private static void BeforeMapAwake()
            => active?.ObserveBindings(AssetRepoGalaxy.Distribution, "before-map-awake");

        private static void BeforeWorldLoad()
            => active?.Logger.LogInfo("KEEL_GALAXY_LIFECYCLE before-player-save-load");

        private static void BeforeFactionLoad()
            => active?.Logger.LogInfo("KEEL_GALAXY_LIFECYCLE before-faction-load");

        private static void BeforeStarfield()
            => active?.Logger.LogInfo("KEEL_GALAXY_LIFECYCLE before-starfield-enqueue");

        private void Update()
        {
            UpdatePublicationProbe();
            if (checkedCatalog || Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_PREVIEW_PROBE") != "1"
                || AssetRepoGalaxy.Distribution == null || AssetRepoGalaxy.Distribution.StarDatas?.Count is not > 0)
                return;
            checkedCatalog = true;
            try
            {
                var stock = AssetRepoGalaxy.Distribution;
                ObserveBindings(stock, "pre-entry");
                if (Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_SOURCE_PROBE") == "1")
                {
                    var source = GalaxyCatalogPreview.AuditPreEntrySource();
                    Logger.Log(source.Available ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Error,
                        "KEEL_GALAXY_SOURCE_PROBE " + (source.Available ? "PASS " : "FAIL ") + source.Reason);
                }
                PlanetarySystem.OnNewSystem += ObserveLoadedBindings;
                var intro = GameIntroductionResources.STARTING_SYSTEM;
                var start = new GalaxyStarSeed(intro.seed, intro.sector.x, intro.sector.y, intro.sector.z);
                var retained = GalaxyCatalogPreview.SelectStockSubset(star => star.SectorX == start.SectorX
                    && star.SectorZ == start.SectorZ);
                var reduced = GalaxyCatalogPreview.Compile(retained);
                var authored = new GalaxyStarSeed(987654321, 1, 1, 1);
                var candidate = GalaxyCatalogPreview.Compile(
                    new GalaxyCatalogPlan(new[] { authored, start }, new[] { new GalaxyGateRoute(start.Seed, authored.Seed) }, start));
                int index = candidate.Plan.Stars.ToList().IndexOf(start);
                if (reduced.StarRows.Count <= 1 || reduced.StarRows.Count != retained.Stars.Count
                    || candidate.StarRows.Count != 2 || candidate.GateRows.Count != 1
                    || !candidate.StarRows[index].position.Equals(intro.HolomapPosition)
                    || !candidate.GateRows[0].startPosition.Equals(intro.HolomapPosition)
                    || !ReferenceEquals(stock, AssetRepoGalaxy.Distribution) || stock.StarDatas.Count <= reduced.StarRows.Count)
                    throw new InvalidOperationException("Candidate rows or stock resource identity did not match.");
                Logger.LogInfo($"KEEL_GALAXY_PREVIEW_PROBE PASS subset={reduced.StarRows.Count} authored={candidate.StarRows.Count} stock={stock.StarDatas.Count}");
            }
            catch (Exception exception)
            {
                Logger.LogError("KEEL_GALAXY_PREVIEW_PROBE FAIL " + exception);
            }
        }

        private void ObserveLoadedBindings()
        {
            PlanetarySystem.OnNewSystem -= ObserveLoadedBindings;
            ObserveBindings(AssetRepoGalaxy.Distribution, "system-loaded");
        }

        private void ObserveBindings(GalaxyStarDistribution stock, string phase)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var mapField = typeof(GalaxyMap).GetField("starDistribution", fields);
            var rendererField = typeof(GalaxyMap).GetField("starsRenderer", fields);
            var routeField = typeof(GalaxyWarpGateRoutes).GetField("distribution", fields);
            var repoField = typeof(AssetRepoGalaxy).GetField("distribution", fields);
            var maps = Resources.FindObjectsOfTypeAll<GalaxyMap>();
            var repoMatches = Resources.FindObjectsOfTypeAll<AssetRepoGalaxy>().Count(repo => ReferenceEquals(repoField?.GetValue(repo), stock));
            var matchCount = maps.Count(map => ReferenceEquals(mapField?.GetValue(map), stock));
            var renderedCount = maps.Count(map => rendererField?.GetValue(map) != null);
            bool sameRouteCatalog = ReferenceEquals(routeField?.GetValue(AssetRepoGalaxy.WarpGateRoutes), stock);
            Logger.LogInfo($"KEEL_GALAXY_BINDINGS phase={phase} session={PlayerState.sessionMode} maps={maps.Length} matching={matchCount}"
                + $" renderers={renderedCount} repoMatches={repoMatches} routeCatalog={sameRouteCatalog}");
        }
    }
}
