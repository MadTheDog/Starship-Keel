using System;
using System.Linq;
using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

namespace Sevo.Example.GalaxyPreviewProbe
{
    [BepInPlugin("example.galaxycatalogpreview", "Example Galaxy Catalog Preview", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private bool checkedCatalog;

        private void Update()
        {
            if (checkedCatalog || Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_PREVIEW_PROBE") != "1"
                || AssetRepoGalaxy.Distribution == null || AssetRepoGalaxy.Distribution.StarDatas?.Count is not > 0)
                return;
            checkedCatalog = true;
            try
            {
                var stock = AssetRepoGalaxy.Distribution;
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
    }
}
