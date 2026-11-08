using BepInEx;
using Common.RNG;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using World;
using World.Terrain;

namespace Sevo.Example.OreHooks
{
    [BepInPlugin(Id, "Example Ore Hooks", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.6")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Id = "example.orehooks";
        private const float TestSize = 23.25f;
        private const float TestThreshold = 0.999f;
        private const float TestBrightness = 1.375f;
        private bool loggedScan;
        private bool loggedSnapshot;
        private bool testedGeneration;
        private bool testedMaterial;
        private bool pulseScanner;
        private string? materialName;

        private void Awake()
        {
            OreGeneration.Register(Id, "planet-vein", OreGenerationScope.Planets, 0, size: TestSize, threshold: TestThreshold);
            OreScannerSnapshots.Register(Id, "observe", groups =>
            {
                if (!loggedSnapshot)
                {
                    loggedSnapshot = true;
                    Logger.LogInfo($"ORE SCANNER PROBE snapshot: {groups.Count} copied groups.");
                }
                if (loggedScan || groups.Count == 0) return;
                loggedScan = true;
                Logger.LogInfo($"ORE SCANNER PROBE PASS: {groups.Count} copied groups. First symbol {groups[0].SymbolId}.");
            });
            materialName = Environment.GetEnvironmentVariable("SEVO_KEEL_ORE_PROBE_MATERIAL");
            if (!string.IsNullOrWhiteSpace(materialName))
                TerrainMaterialVisuals.Register(Id, "terrain-visual", materialName!, brightnessFactor: TestBrightness);
            pulseScanner = Environment.GetEnvironmentVariable("SEVO_KEEL_ORE_PROBE_SCAN") == "1";
        }

        private void Update()
        {
            if (!testedGeneration) TryNativeGeneration();
            if (!testedMaterial) TryNativeMaterial();
            if (pulseScanner) TryNativeScan();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TryNativeScan()
        {
            if (!OreScannerSnapshots.IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || PlayerState.InLoadingScreen || PlayerState.InMenu || UnityEngine.Time.timeSinceLevelLoad < 4f) return;
            pulseScanner = false;
            OreScannerResources.OnScannerKeyPressed();
            Logger.LogInfo("ORE SCANNER PROBE: requested a stock scanner pulse in the disposable solo world.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TryNativeMaterial()
        {
            var rows = WorldTerrainResources.TerrainMaterialDatas;
            if (!rows.IsCreated || rows.Length == 0) return;
            var resources = UnityEngine.Resources.FindObjectsOfTypeAll<WorldTerrainResources>()
                .Where(asset => asset.terrainTextures.Count == rows.Length).ToArray();
            if (resources.Length != 1)
            {
                testedMaterial = true;
                Logger.LogWarning("TERRAIN MATERIAL PROBE SKIP: ambiguous native material asset.");
                return;
            }
            testedMaterial = true;
            var names = resources[0].terrainTextures.Select(texture => texture.ShaderName).ToArray();
            if (materialName == null)
            {
                Logger.LogInfo("TERRAIN MATERIAL PROBE names: " + string.Join(", ", names));
                return;
            }
            var id = Array.IndexOf(names, materialName);
            bool passed = TerrainMaterialVisuals.IsAvailable && id >= 0 && rows[id].brightnessFactor == TestBrightness;
            Logger.Log(passed ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Error,
                $"TERRAIN MATERIAL PROBE {(passed ? "PASS" : "FAIL")}: {materialName}, native row {id}.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe void TryNativeGeneration()
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.solo) return;
            testedGeneration = true;
            if (!OreGeneration.IsAvailable)
            {
                Logger.LogWarning("ORE GENERATION PROBE SKIP: enable experimental ore generation in a managed lab.");
                return;
            }
            var draws = new HashDrawSet(41527u, 128);
            var planet = LandformCommon.GetOreData(draws, 18, StartingLocationType.startingPlanet, false);
            var asteroid = LandformCommon.GetOreData(draws, 18, StartingLocationType.startingPlanet, true);
            float weight = 0f;
            for (int slot = 0; slot < OreGeneration.SlotCount; slot++)
                weight += 1f - planet.oreThreshold[slot];
            bool passed = planet.oreSize[0] == TestSize && planet.oreThreshold[0] == TestThreshold
                && planet.totalWeight == weight && asteroid.oreSize[0] != TestSize;
            Logger.Log(passed ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Error,
                $"ORE GENERATION PROBE {(passed ? "PASS" : "FAIL")}: planet size={planet.oreSize[0]} threshold={planet.oreThreshold[0]} "
                + $"weight={planet.totalWeight}, asteroid size={asteroid.oreSize[0]}.");
        }
    }
}
