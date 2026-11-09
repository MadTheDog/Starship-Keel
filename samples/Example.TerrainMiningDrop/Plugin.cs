using System;
using System.Collections.Generic;
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
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Owner = "example.terrain-mining";
        private const string Output = Owner + ":shard";
        private const string Backing = "RAW_IRON_A";
        private const int FixtureNode = 7329;
        private bool pending;

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
                if (!TerrainMiningDropRegistration.IsAvailable || !ItemRegistration.TryGetPrefabId(Output, out int output))
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
                VerifyDrop(drop, node, "replacement", 100, output, expectCustom: false, crossesSurface: false);
                VerifyDrop(drop, node, "replacement", 200, output, expectCustom: true);
                VerifyDrop(drop, node, "conflict", 300, output, expectCustom: false);
                VerifyDrop(drop, node, "missing", 400, output, expectCustom: false);
                Logger.LogInfo("KEEL_TERRAIN_MINING_PROBE PASS replacement=1 quantity=2 impulse=true conflictStock=1 missingItemStock=1 nonCrossing=0");
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

        private static void VerifyDrop(MethodInfo drop, OctreeNodeData node, string material, uint candidateSeed,
            int output, bool expectCustom, bool crossesSurface = true)
        {
            if (!TerrainMaterialRegistration.TryGetId(Owner + ":" + material, out byte native))
                throw new InvalidOperationException("The probe material was not registered: " + material);
            var materialRow = WorldTerrainResources.TerrainMaterialDatas[native];
            if (!materialRow.spawnRock || !materialRow.IsRawOre || !materialRow.IsOre)
                throw new InvalidOperationException("The stock ore backing no longer drops ore.");
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
                    throw new InvalidOperationException("Unexpected stock or replacement item count for " + material + ".");
                if (!crossesSurface) return;
                var dropItem = spawned[0];
                if (dropItem.rigidbodyId == 0 || math.lengthsq(SkyRigidbody.datas[dropItem.rigidbodyId].linearVelocity) <= 0f)
                    throw new InvalidOperationException("The drop did not receive its stock impulse.");
                if (expectCustom && (dropItem.prefabId != output || dropItem.itemType != WorldItemType.craftingParts
                    || dropItem.quantity != 2f || !dropItem.CanBePickedUp(StorageCommon.CONTEXT)))
                    throw new InvalidOperationException("The replacement did not use the registered item identity and quantity.");
                if (!expectCustom && (dropItem.itemType != WorldItemType.ore || dropItem.quantity != 1f))
                    throw new InvalidOperationException("Stock drop was not preserved for " + material + ".");
            }
            finally
            {
                foreach (var item in spawned) item.ReleaseComponentsAndItemId(unlinkTerrain: false);
            }
        }
    }
}
