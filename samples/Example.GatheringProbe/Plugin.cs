using System;
using BepInEx;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.GatheringProbe
{
    [BepInPlugin(Owner, "Keel Gathering Lab Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.7")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Owner = "example.gathering-probe";
        private const string Output = Owner + ":test-shard";
        private const uint SourceSeed = 0x34dc2514;
        private const uint ImpactSeed = 0x612a4281;
        private const uint OutputSeed = SourceSeed ^ ImpactSeed;
        private static readonly (WorldItemType Type, WorldPropCommon.WorldPropId Id)[] Sources =
        {
            (WorldItemType.rockLarge, WorldPropCommon.WorldPropId.rockLargeA),
            (WorldItemType.rockLarge, WorldPropCommon.WorldPropId.rockLargeB),
            (WorldItemType.rockLarge, WorldPropCommon.WorldPropId.rockLargeC),
            (WorldItemType.crystal, WorldPropCommon.WorldPropId.crystal_quartz_01),
            (WorldItemType.crystal, WorldPropCommon.WorldPropId.crystal_quartz_02),
            (WorldItemType.crystal, WorldPropCommon.WorldPropId.crystal_sunstone_a)
        };

        private enum Stage { Inactive, Ready, Nonlethal, Lethal }
        private Stage stage;
        private int startFrame;
        private int sourceId;
        private int sourcePrefab;
        private int stockPrefab;
        private int outputPrefab;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_GATHERING_PROBE") != "1")
                return;
            try
            {
                var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
                var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                if (!Guid.TryParseExact(session, "N", out _) || state == null
                    || Application.companyName != "SEVO Keel Hooks Lab016" || Application.productName != "SEVO HK Test")
                    throw new InvalidOperationException("The disposable Keel hooks lab is required.");
                ProfileProtocol.Open(state, Paths.GameRootPath, session!);
                ItemRegistration.Register(Owner, "test-shard", 1, () => new ItemDefinition(
                    WorldPropCommon.WorldPropId.component_construction, "Probe Shard", "A Keel gathering lab item."));
                foreach (var source in Sources)
                    GatheringRegistration.RegisterDrop(Owner, "break-" + (int)source.Id, source.Type, (int)source.Id, Output, 2);
                stage = Stage.Ready;
            }
            catch (Exception exception)
            {
                Logger.LogError("KEEL_GATHERING_PROBE FAIL " + exception);
            }
        }

        private void Update()
        {
            if (stage == Stage.Inactive || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle
                || GameloopManager.IsNativeContainerAccessUnsafe || Dependency.PlayerManager?.SkyTransform == null)
                return;

            try
            {
                if (stage == Stage.Ready)
                {
                    if (GatheringRegistration.IsAvailable && ItemRegistration.TryGetPrefabId(Output, out outputPrefab))
                        Begin();
                }
                else if (stage == Stage.Nonlethal && Time.frameCount > startFrame + 2)
                {
                    var result = CheckDrops(cleanup: false);
                    if (result.Bonus != 0 || result.Stock != 0)
                        throw new InvalidOperationException("A nonlethal hit produced loot.");
                    var source = ItemInstanceResources.GetItemData(sourceId);
                    if (source.id != sourceId || source.seed != SourceSeed || source.transformId == 0)
                        throw new InvalidOperationException("The source did not survive a nonlethal hit.");
                    source.OnImpact(Impact(ItemInstanceRenderResources.PrefabDatas[source.prefabId].healthPoint * 2f));
                    startFrame = Time.frameCount;
                    stage = Stage.Lethal;
                }
                else if (stage == Stage.Lethal && Time.frameCount > startFrame + 12)
                {
                    var result = CheckDrops(cleanup: true);
                    if (result.Bonus != 1 || result.InvalidBonus || result.Stock == 0 || result.Source != 0)
                        throw new InvalidOperationException($"Unexpected drops bonus={result.Bonus} invalidBonus={result.InvalidBonus} stock={result.Stock} source={result.Source}");
                    stage = Stage.Inactive;
                    Logger.LogInfo($"KEEL_GATHERING_PROBE PASS bonusStacks={result.Bonus} bonusQuantity=2 stockStacks={result.Stock}");
                }
            }
            catch (Exception exception)
            {
                stage = Stage.Inactive;
                Logger.LogError("KEEL_GATHERING_PROBE FAIL " + exception);
                if (sourceId != 0)
                    CheckDrops(cleanup: true);
            }
        }

        private void Begin()
        {
            var table = ItemInstanceRenderResources.PrefabDatas;
            foreach (var candidate in Sources)
            {
                var prefab = table[(int)candidate.Id];
                var stock = (int)prefab.WorldInteractionData.spawnWorldPropId;
                if (prefab.itemType != candidate.Type || !prefab.renderData.IsValid || prefab.mass <= 0f
                    || !float.IsFinite(prefab.healthPoint) || prefab.healthPoint <= 0f
                    || stock <= 0 || stock >= table.Length || stock == (int)candidate.Id
                    || prefab.WorldInteractionData.minSpawn < 1)
                    continue;

                sourcePrefab = (int)candidate.Id;
                stockPrefab = stock;
                var source = new ItemInstanceData
                {
                    itemType = candidate.Type, prefabId = sourcePrefab, quantity = 1f, seed = SourceSeed, scale = 1f
                };
                sourceId = source.Register();
                var player = Dependency.PlayerManager.SkyTransform;
                try
                {
                    source.AllocateAllComponents(new SkyTransformData
                    {
                        systemPosition = player.SystemPosition + new Vector3l(player.Data.rotation * (Vector3.forward * 8f)),
                        rotation = Quaternion.identity,
                        scale = 1f
                    }, useCollider: false);
                }
                catch
                {
                    source.ReleaseComponentsAndItemId(unlinkTerrain: false);
                    sourceId = 0;
                    throw;
                }
                source.OnImpact(Impact(prefab.healthPoint * 0.25f));
                startFrame = Time.frameCount;
                stage = Stage.Nonlethal;
                return;
            }
            throw new InvalidOperationException("No live stock source in the probe catalog emits native loot.");
        }

        private ImpactData Impact(float damage) => new ImpactData
        {
            dataId = sourceId, seed = ImpactSeed, hitPoint = damage,
            type = WorldColliderType.worldItem, localNormal = Vector3.up
        };

        private (int Bonus, bool InvalidBonus, int Stock, int Source) CheckDrops(bool cleanup)
        {
            int bonus = 0;
            bool invalidBonus = false;
            int stock = 0;
            int sources = 0;
            int length = ItemInstanceResources.DatasForContext.Length;
            for (int id = 1; id < length; id++)
            {
                var item = ItemInstanceResources.GetItemData(id);
                if (item.id != id || item.transformId == 0)
                    continue;
                bool isSource = id == sourceId && item.seed == SourceSeed && item.prefabId == sourcePrefab;
                bool isBonus = item.prefabId == outputPrefab && item.itemType == WorldItemType.craftingParts && item.seed == OutputSeed;
                bool isStock = !isSource && item.prefabId == stockPrefab && unchecked(item.seed - SourceSeed) < 8;
                if (isBonus)
                {
                    bonus++;
                    invalidBonus |= item.worldColliderId == 0 || item.quantity != 2f || !item.CanBePickedUp(StorageCommon.CONTEXT);
                }
                if (isStock)
                    stock++;
                if (isSource)
                    sources++;
                if (cleanup && (isBonus || isStock || isSource))
                    item.ReleaseComponentsAndItemId(unlinkTerrain: false);
            }
            return (bonus, invalidBonus, stock, sources);
        }
    }
}
