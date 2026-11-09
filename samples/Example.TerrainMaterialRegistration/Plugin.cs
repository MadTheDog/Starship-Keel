using System;
using System.IO;
using System.Linq;
using BepInEx;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;

namespace Sevo.Example.TerrainMaterialProbe
{
    [BepInPlugin(Id, "Keel Terrain Material Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Id = "example.terrainmaterial";
        private const string Ridge = "ridge";
        private const string Iron = "iron";
        private const string RidgeStock = "TEX_CLIFF_A";
        private const string IronStock = "RAW_IRON_A";
        private bool enabledProbe;
        private bool tombstone;
        private bool checkedRows;

        private void Awake()
        {
            enabledProbe = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_MATERIAL_PROBE") == "1";
            if (!enabledProbe) return;
            tombstone = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_MATERIAL_PROBE_TOMBSTONE") == "1";
            if (!tombstone) TerrainMaterialRegistration.Register(Id, Ridge, 1, RidgeStock);
            TerrainMaterialRegistration.Register(Id, Iron, 1, IronStock);
        }

        private void Update()
        {
            var rows = WorldTerrainResources.TerrainMaterialDatas;
            if (!enabledProbe || checkedRows || !rows.IsCreated || rows.Length == 0) return;
            checkedRows = true;
            try
            {
                if (!TerrainMaterialRegistration.IsAvailable
                    || !TerrainMaterialRegistration.TryGetId(Id + ":" + Iron, out byte iron))
                    throw new InvalidOperationException("A custom material ID was not published.");
                var assets = Resources.FindObjectsOfTypeAll<WorldTerrainResources>()
                    .Single(asset => asset.terrainTextures.Count < rows.Length
                        && asset.terrainTextures.Any(material => material.ShaderName == IronStock));
                int stockCount = assets.terrainTextures.Count;
                if (iron < stockCount || Shader.GetGlobalInt("_TerrainMaterialCount") != rows.Length)
                    throw new InvalidOperationException("Terrain table did not retain stock rows and publish the new material count.");
                var profile = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR")!;
                var registry = RegistryJson.Read(File.ReadAllText(Path.Combine(profile, "terrain-material-registry.json")));
                var ridge = registry.Mappings.Single(mapping => mapping.Key == Id + ":" + Ridge);
                var savedIron = registry.Mappings.Single(mapping => mapping.Key == Id + ":" + Iron);
                if (ridge.Id < stockCount || ridge.Id == iron || savedIron.Id != iron || savedIron.Tombstone
                    || ridge.Tombstone != tombstone || TerrainMaterialRegistration.TryGetId(ridge.Key, out byte liveRidge) == tombstone
                    || (!tombstone && liveRidge != ridge.Id))
                    throw new InvalidOperationException("Persisted terrain identities do not match live registrations.");
                int ridgeBacking = assets.terrainTextures.FindIndex(material => material.ShaderName == (tombstone ? "TEX_ROCK_A" : RidgeStock));
                int ironBacking = assets.terrainTextures.FindIndex(material => material.ShaderName == IronStock);
                VerifyCopy(rows[ridgeBacking], rows[ridge.Id]);
                VerifyCopy(rows[ironBacking], rows[iron]);
                Logger.LogInfo($"KEEL_TERRAIN_MATERIAL_PROBE PASS ridge={ridge.Id} iron={iron} stockCount={stockCount} rows={rows.Length} tombstone={tombstone}");
            }
            catch (Exception exception) { Logger.LogError("KEEL_TERRAIN_MATERIAL_PROBE FAIL: " + exception); }
        }

        private static void VerifyCopy(TerrainMaterialData source, TerrainMaterialData row)
        {
            if (source.textureId != row.textureId || !source.fadeValue.Equals(row.fadeValue)
                || source.surface != row.surface || source.gradientId != row.gradientId || source.IsOre != row.IsOre)
                throw new InvalidOperationException("A registered terrain row did not preserve its stock backing.");
        }
    }
}
