using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using Common.RNG;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using Unity.Mathematics;
using UnityEngine;
using VoxelEngine;
using World;
using World.Terrain;

namespace Sevo.Example.TerrainMiningDrop
{
    [BepInPlugin(Owner, "Keel Terrain Mining Drop Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Owner = "example.terrain-mining";
        private const string Output = Owner + ":shard";
        private const string Backing = "RAW_IRON_A";
        private const int FixtureNode = 7329;
        private static readonly string[] RockBackings = { "TEX_ROCK_A", "DIRTY_ROCK_A", "LARGE_ROCK_A" };
        private bool pending;
        private bool aluminium;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_MINING_PROBE") != "1") return;
            try
            {
                string? session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
                string? profile = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                bool legacyLab = Application.companyName == "SEVO Keel Hooks Lab016" && Application.productName == "SEVO HK Test";
                bool worldLab = Application.companyName == "SEVO Keel Gen Lab 0110" && Application.productName == "SEVO WG Test";
                if (!Guid.TryParseExact(session, "N", out _) || profile == null || (!legacyLab && !worldLab))
                    throw new InvalidOperationException("The disposable Keel hooks lab is required.");
                ProfileProtocol.Open(profile, Paths.GameRootPath, session!);
                ItemRegistration.Register(Owner, "shard", 1, () => new ItemDefinition(
                    WorldPropCommon.WorldPropId.component_construction, "Probe Shard", "A Keel terrain mining lab item."));
                TerrainMaterialRegistration.Register(Owner, "replacement", 1, Backing);
                TerrainMaterialRegistration.Register(Owner, "conflict", 1, Backing);
                TerrainMaterialRegistration.Register(Owner, "missing", 1, Backing);
                TerrainMiningDropRegistration.Register(Owner, "replace", Owner + ":replacement", Output, 2f);
                TerrainMiningDropRegistration.Register(Owner, "conflict-one", Owner + ":conflict", Output);
                TerrainMiningDropRegistration.Register("example.other-terrain-mining", "conflict-two", Owner + ":conflict", Output);
                TerrainMiningDropRegistration.Register(Owner, "missing-item", Owner + ":missing", Owner + ":unregistered");
                for (int i = 0; i < RockBackings.Length; i++)
                {
                    string localName = "rock-" + i;
                    TerrainMaterialRegistration.Register(Owner, localName, 1, RockBackings[i]);
                    TerrainMiningDropRegistration.Register(Owner, localName, Owner + ":" + localName, Output, 2f);
                }
                aluminium = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_MINING_ALUMINIUM") == "1";
                pending = true;
            }
            catch (Exception exception)
            {
                Logger.LogError("KEEL_TERRAIN_MINING_PROBE FAIL " + exception);
            }
        }

        private void Update()
        {
            if (!pending || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || PlayerState.gameMode != PlayerState.GameMode.creative || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || !GameloopManager.IsMainThread
                || GameloopManager.IsNativeContainerAccessUnsafe || Dependency.PlayerManager?.SkyTransform == null)
                return;
            pending = false;
            int transform = 0;
            try
            {
                if (!TerrainMiningDropRegistration.IsAvailable || !ItemRegistration.TryCreateItemData(Output, 2f, out var output))
                    throw new InvalidOperationException("The terrain mining or item hook is unavailable.");
                var drop = typeof(OctreeNodeResources).GetMethod("CheckIfDropRock", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("OctreeNodeResources.CheckIfDropRock");
                var player = Dependency.PlayerManager.SkyTransform;
                transform = SkyTransformData.datas.GetIdAndSetFast(new SkyTransformData
                {
                    systemPosition = player.SystemPosition + new Vector3l(new Vector3(8f, 3f, 8f)),
                    rotation = quaternion.identity,
                    localScale = 1f,
                    scale = 1f
                });
                var node = new OctreeNodeData { size = 16f, transformId = transform };
                int rockPrefabCount = (typeof(OctreeNodeResources).GetField("ROCKS", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.GetValue(null) as WorldPropCommon.WorldPropId[])?.Length
                    ?? throw new InvalidOperationException("The stock rock prefab table is unavailable.");
                VerifyDrop(drop, node, Material("replacement"), 100, rockPrefabCount, true, null, crossesSurface: false);
                VerifyDrop(drop, node, Material("replacement"), 200, rockPrefabCount, true, output);
                VerifyDrop(drop, node, Material("conflict"), 300, rockPrefabCount, true, null);
                VerifyDrop(drop, node, Material("missing"), 400, rockPrefabCount, true, null);

                var rows = WorldTerrainResources.TerrainMaterialDatas;
                byte[] regularRock = Enumerable.Range(0, RockBackings.Length)
                    .Select(i => Material("rock-" + i)).Where(id => rows[id].spawnRock && !rows[id].IsOre).ToArray();
                if (regularRock.Length == 0) throw new InvalidOperationException("No stock backing in the probe admits ordinary rock drops.");
                float rockScale = VerifyDrop(drop, node, regularRock[0], 500, rockPrefabCount, false, output);

                if (aluminium)
                {
                    if (!TerrainMaterialRegistration.TryGetId("sevo.aluminium:terrain", out byte al)
                        || !ItemRegistration.TryCreateItemData("sevo.aluminium:ore", 1f, out var alItem))
                        throw new InvalidOperationException("The Aluminium fixture is unavailable.");
                    var assets = Resources.FindObjectsOfTypeAll<WorldTerrainResources>()
                        .Where(asset => asset.terrainTextures.Count <= rows.Length).ToArray();
                    int silver = assets.Length == 1 ? assets[0].terrainTextures.FindIndex(row => row.ShaderName == "RAW_SILVER_A") : -1;
                    if (silver < 0 || silver >= rows.Length)
                        throw new InvalidOperationException("The stock raw Silver terrain row is unavailable.");
                    float aluminiumScale = VerifyDrop(drop, node, al, 600, rockPrefabCount, true, alItem, pickup: true);
                    float silverScale = VerifyDrop(drop, node, (byte)silver, 600, rockPrefabCount, true, null);
                    if (aluminiumScale != silverScale) throw new InvalidOperationException("Aluminium and Silver native mining scales differ.");
                    Logger.LogInfo($"KEEL_TERRAIN_MINING_ALUMINIUM PASS aluminiumScale={aluminiumScale:R} silverScale={silverScale:R}");
                }
                Logger.LogInfo($"KEEL_TERRAIN_MINING_PROBE PASS replacement=1 quantity=2 impulse=true rockScale={rockScale:R} conflictStock=1 missingItemStock=1 nonCrossing=0");
            }
            catch (Exception exception)
            {
                Logger.LogError("KEEL_TERRAIN_MINING_PROBE FAIL " + exception);
            }
            finally
            {
                if (transform != 0) SkyTransformData.datas.ReleaseId(transform);
            }
        }

        private static byte Material(string key) =>
            TerrainMaterialRegistration.TryGetId(Owner + ":" + key, out byte id) ? id
                : throw new InvalidOperationException("The probe material was not registered: " + key);

        private float VerifyDrop(MethodInfo drop, OctreeNodeData node, byte native, uint candidateSeed,
            int rockPrefabCount, bool expectOre, ItemInstanceData? expectedItem, bool crossesSurface = true, bool pickup = false)
        {
            var materialRow = WorldTerrainResources.TerrainMaterialDatas[native];
            if (!materialRow.spawnRock || materialRow.IsOre != expectOre || (expectOre && !materialRow.IsRawOre))
                throw new InvalidOperationException("The selected backing does not use the expected stock drop branch.");
            var index = new int3(10, 10, 10);
            uint indexHash = unchecked((uint)index.GetHashCode());
            while (new SkeetoHash(unchecked(candidateSeed + FixtureNode + indexHash)).GetFloat() > 0.5f
                || ItemInstanceResources.TryGetItemDataBySeed(unchecked(candidateSeed + indexHash), out _))
                candidateSeed++;
            uint seed = unchecked(candidateSeed + indexHash);
            var impact = new ImpactData { dataId = FixtureNode, seed = candidateSeed, localNormal = Vector3.up };
            var voxel = new VoxelNodeData { texId0 = native, texId1 = native, density = 0 };
            var spawned = new List<ItemInstanceData>();
            try
            {
                drop.Invoke(null, new object[] { FixtureNode, impact, voxel, crossesSurface, node, index });
                for (int id = 1; id < ItemInstanceResources.DatasForContext.Length; id++)
                {
                    var item = ItemInstanceResources.GetItemData(id);
                    if (item.id == id && item.seed == seed && item.transformId != 0) spawned.Add(item);
                }
                if (spawned.Count != (crossesSurface ? 1 : 0))
                    throw new InvalidOperationException("Unexpected stock or replacement item count for " + native + ".");
                if (!crossesSurface) return 0;
                var dropItem = spawned[0];
                if (dropItem.rigidbodyId == 0 || math.lengthsq(SkyRigidbody.datas[dropItem.rigidbodyId].linearVelocity) <= 0f)
                    throw new InvalidOperationException("The drop did not receive its stock impulse.");
                var random = new SkeetoHash(seed);
                _ = random.GetIntRange(0, rockPrefabCount - 1);
                float scale = expectOre ? random.GetFloatRange(0.5f, 0.5f) : random.GetFloatRange(0.45f, 0.55f);
                var actualTransform = SkyTransformData.datas[dropItem.transformId];
                if (dropItem.scale != scale || actualTransform.scale != scale || actualTransform.localScale != scale)
                    throw new InvalidOperationException($"Expected stock scale {scale:R}; item={dropItem.scale:R}, transform={actualTransform.scale:R}/{actualTransform.localScale:R}.");
                if (expectedItem is ItemInstanceData expected)
                {
                    if (dropItem.prefabId != expected.prefabId || dropItem.itemType != expected.itemType
                        || dropItem.contextualDataId != expected.contextualDataId || dropItem.quantity != expected.quantity
                        || !dropItem.color.Equals(expected.color) || !dropItem.CanBePickedUp(StorageCommon.CONTEXT))
                        throw new InvalidOperationException("The replacement did not retain the registered identity, tint and quantity.");
                }
                else if (dropItem.itemType != WorldItemType.ore || dropItem.quantity != 1f
                    || dropItem.contextualDataId != (int)materialRow.GradientTypeOreSpawn)
                    throw new InvalidOperationException("The native stock drop was not preserved.");
                var context = StorageCommon.CONTEXT;
                var render = dropItem.GetRenderBounds(context, true).Size;
                var collider = dropItem.GetColliderBounds(context, true).Size;
                if (math.lengthsq(render) <= 0 || math.lengthsq(collider) <= 0
                    || math.distancesq(render, dropItem.GetRenderBounds(context, false).Size * scale) > 1e-8f
                    || math.distancesq(collider, dropItem.GetColliderBounds(context, false).Size * scale) > 1e-8f)
                    throw new InvalidOperationException("Native render or collider bounds did not follow the drop scale.");
                if (pickup)
                {
                    var inventory = Dependency.PlayerManager.PlayerInventory;
                    float Count() => inventory.GetItemSaveDatas().Where(data => data.id == dropItem.prefabId
                        && data.itemType == dropItem.itemType && data.contextualDataId == dropItem.contextualDataId)
                        .Sum(data => data.quantity);
                    float before = Count();
                    // The native pickup can merge or move the original ID into the player inventory.
                    // The disposable fixture must not release that ID after handing it to stock.
                    spawned.Clear();
                    dropItem.TryPickUpItem(false);
                    if (Count() - before != dropItem.quantity)
                        throw new InvalidOperationException("Native pickup did not retain the ore identity and quantity.");
                }
                Logger.LogInfo(
                    $"KEEL_DROP_BOUNDS type={dropItem.itemType} scale={scale:R} render={render} collider={collider} pickup={pickup}");
                return scale;
            }
            finally
            {
                foreach (var item in spawned) item.ReleaseComponentsAndItemId(unlinkTerrain: false);
            }
        }
    }
}
