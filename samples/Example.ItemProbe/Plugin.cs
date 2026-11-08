using System;
using System.Text;
using BepInEx;
using Game;
using Manager;
using MessagePack;
using Sevo.Framework.Runtime;
using VoxelEngine;
using World;

namespace Sevo.Example.ItemProbe
{
    [BepInPlugin(Owner, "Keel Item Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.7")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Owner = "example.keel-items";
        private const string DustKey = Owner + ":dust";
        private const string GritKey = Owner + ":grit";
        private const string SourceRecipe = Owner + ":make_dust";
        private const string UseRecipe = Owner + ":use_dust";
        private const WorldPropCommon.WorldPropId StockTemplate = WorldPropCommon.WorldPropId.component_construction;
        private bool checkedTables;
        private bool checkedRoundTrip;
        private bool checkedRecipes;

        private void Awake()
        {
            ItemRegistration.Register(Owner, "dust", 1, () =>
                new ItemDefinition(StockTemplate, "Probe Dust", "A custom part for testing Keel item registration."));
            ItemRegistration.Register(Owner, "grit", 1, () =>
                new ItemDefinition(StockTemplate, "Probe Grit", "A second item sharing the dust's stock template."));
            RecipeRegistration.Register(Owner, "make_dust", 1, () =>
                new RecipeDefinition(CraftingLevel.player, 3f, RequireElement(DustKey, 3f), new CraftingElement(StockTemplate, 1f)));
            RecipeRegistration.Register(Owner, "use_dust", 1, () =>
                new RecipeDefinition(CraftingLevel.player, 3f, RequireElement(GritKey, 2f), RequireElement(DustKey, 3f)));
        }

        private static CraftingElement RequireElement(string key, float quantity) =>
            ItemRegistration.TryCreateElement(key, quantity, out var element) ? element
                : throw new InvalidOperationException("Probe item is unavailable: " + key);

        private void Update()
        {
            if (!ItemRegistration.TryCreateItemData(DustKey, 3f, out var dust)
                || !ItemRegistration.TryCreateItemData(GritKey, 2f, out var grit))
                return;

            if (!checkedTables)
            {
                checkedTables = true;
                var stock = new CraftingElement(StockTemplate, 1f).ToItemData;
                var context = StorageCommon.CONTEXT;
                var table = ItemInstanceRenderResources.WorldProps;
                if (dust.id != 0 || grit.id != 0 || dust.prefabId == grit.prefabId || dust.prefabId == stock.prefabId
                    || grit.prefabId == stock.prefabId || dust.Name() != "Probe Dust" || grit.Name() != "Probe Grit"
                    || !dust.CanBePickedUp(context) || !grit.CanBePickedUp(context)
                    || dust.GetUnitaryVolumeInLiter(context) <= 0f
                    || !ItemInstanceRenderResources.PrefabDatas[dust.prefabId].renderData.IsValid
                    || table[dust.prefabId].Id != (WorldPropCommon.WorldPropId)dust.prefabId
                    || ItemInstanceCommon.CanMerge(dust, grit, context, mergeWithEmpty: false)
                    || ItemInstanceCommon.CanMerge(dust, stock, context, mergeWithEmpty: false)
                    || !ItemInstanceCommon.CanMerge(dust, dust, context, mergeWithEmpty: false)
                    || !ItemRegistration.TryCreateElement(DustKey, 1f, out var dustRecipe)
                    || !dustRecipe.IsMatching(dust, context) || dustRecipe.IsMatching(grit, context)
                    || ItemRegistration.TryCreateItemData(DustKey, 0f, out _)
                    || ItemRegistration.TryCreateItemData(DustKey, float.NaN, out _))
                    throw new InvalidOperationException("The native item tables, matching or stack descriptor failed verification.");

                Logger.LogInfo($"KEEL_ITEM_TABLE PASS dust={dust.prefabId} grit={grit.prefabId} stock={stock.prefabId}");
            }

            if (!checkedRecipes && RecipeRegistration.TryGetSchematicId(SourceRecipe, out var sourceId)
                && RecipeRegistration.TryGetSchematicId(UseRecipe, out var useId))
            {
                checkedRecipes = true;
                var source = CraftingSchematic.schematicsNativeArray[sourceId];
                var use = CraftingSchematic.schematicsNativeArray[useId];
                if (!source.IsValid || !use.IsValid || source.output.id != dust.prefabId || use.output.id != grit.prefabId
                    || !use.element0.IsMatching(dust, StorageCommon.CONTEXT) || use.element0.IsMatching(grit, StorageCommon.CONTEXT))
                    throw new InvalidOperationException("The native recipe table did not link the two distinct custom items.");
                Logger.LogInfo($"KEEL_ITEM_RECIPE PASS source={sourceId} use={useId}");
            }

            if (checkedRoundTrip || Environment.GetEnvironmentVariable("SEVO_KEEL_ITEM_SMOKE") != "1"
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.InLoadingScreen
                || GameloopManager.IsNativeContainerAccessUnsafe || Dependency.PlayerManager?.PlayerInventory == null
                || !ItemInstanceResources.DatasForContext.IsCreated)
                return;

            checkedRoundTrip = true;
            var raw = dust.SaveData;
            var saved = MessagePackSerializer.Deserialize<ItemSaveData>(MessagePackSerializer.Serialize(raw));
            var identity = saved.data == null ? Array.Empty<string>() : Encoding.UTF8.GetString(saved.data).Split('\0');
            if (raw.id != dust.prefabId || raw.itemType != dust.itemType
                || identity.Length != 4 || identity[2] != "1" || identity[3] != DustKey)
                throw new InvalidOperationException($"The native item save value has no Keel identity payload. Getter={raw.data?.Length ?? 0}, decoded={saved.data?.Length ?? 0}, parts={identity.Length}, id={raw.id}, type={raw.itemType}.");
            var stockItem = new CraftingElement(StockTemplate, 1f).ToItemData;
            var stockSave = MessagePackSerializer.Deserialize<ItemSaveData>(MessagePackSerializer.Serialize(stockItem.SaveData));
            if (stockSave.id != stockItem.prefabId || stockSave.itemType != stockItem.itemType || stockSave.data != null)
                throw new InvalidOperationException("The item save codec changed the stock item identity or payload.");
            Logger.LogInfo("KEEL_ITEM_LOAD stage=loading-stock-item-value");
            var loaded = ItemInstanceData.LoadSaveData(saved, 0);
            try
            {
                if (loaded.id == 0 || loaded.itemType != dust.itemType || loaded.prefabId != dust.prefabId || loaded.quantity != dust.quantity)
                    throw new InvalidOperationException("The native item save value lost its type, identity or quantity on load.");
            }
            finally
            {
                if (loaded.id != 0) ItemInstanceResources.ReleaseItemId(loaded.id);
            }

            var wrongId = saved;
            wrongId.id = grit.prefabId;
            RequireRejected(wrongId, "item ID");

            identity[2] = "2";
            saved.data = Encoding.UTF8.GetBytes(string.Join("\0", identity));
            RequireRejected(saved, "revision");

            Logger.LogInfo($"KEEL_ITEM_LOAD PASS dust={dust.prefabId} grit={grit.prefabId}");
        }

        private static void RequireRejected(ItemSaveData saved, string mismatch)
        {
            try
            {
                var unexpected = ItemInstanceData.LoadSaveData(saved, 0);
                if (unexpected.id != 0) ItemInstanceResources.ReleaseItemId(unexpected.id);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("does not match its loaded definition"))
            {
                return;
            }
            throw new InvalidOperationException($"The native item load admitted a mismatched {mismatch}.");
        }
    }
}
