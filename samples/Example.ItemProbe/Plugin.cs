using System;
using System.Reflection;
using System.Text;
using BepInEx;
using Game;
using Manager;
using MessagePack;
using Sevo.Framework.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
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
        private bool visualProbe;
        private static readonly Color32 ProbeTint = new Color32(245, 62, 140, 255);
        private static readonly Bounds ProbeBox = new Bounds(new Vector3(0.04f, 0, 0),
            new Vector3(0.16f, 0.18f, 0.20f));

        private void Awake()
        {
            visualProbe = Environment.GetEnvironmentVariable("SEVO_KEEL_ITEM_VISUAL_PROBE") == "1";
            ItemRegistration.Register(Owner, "dust", 1, () =>
                new ItemDefinition(StockTemplate, "Probe Dust", "A custom part for testing Keel item registration.",
                    visualProbe ? CreateVisual() : null));
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
            if (visualProbe) VerifyVisual(dust);
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

        private static ItemVisualDefinition CreateVisual()
        {
            var mesh = new Mesh { name = "Keel probe component" };
            mesh.vertices = new[] { new Vector3(-0.23f, -0.09f, -0.12f), new Vector3(0.23f, -0.09f, -0.12f),
                new Vector3(0f, -0.09f, 0.18f), new Vector3(0.08f, 0.25f, 0.02f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 3, 1, 1, 3, 2, 2, 3, 0 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, new Vector2(0.5f, 0.5f) };
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            return new ItemVisualDefinition(mesh, ProbeTint, ProbeBox);
        }

        private void VerifyVisual(ItemInstanceData item)
        {
            var native = ItemInstanceRenderResources.PrefabDatas[item.prefabId];
            var stock = ItemInstanceRenderResources.PrefabDatas[(int)StockTemplate];
            if (native.renderData.clusterStart <= stock.renderData.clusterStart || native.renderData.clusterCount != 1
                || native.triangleTreeId != 0 || !native.useColliderBounds || native.mass != stock.mass
                || native.volume != stock.volume || !item.CanBePickedUp(StorageCommon.CONTEXT))
                throw new InvalidOperationException("Unexpected item mesh, pickup or physical table.");

            var player = Dependency.PlayerManager.SkyTransform;
            item.Register();
            var position = player.SystemPosition + new Vector3l(player.Data.rotation * (Vector3.forward * 8f));
            GameObject? previewObject = null;
            try
            {
                item.AllocateAllComponents(new SkyTransformData { systemPosition = position, rotation = Quaternion.identity, scale = 1f });
                var collider = WorldCollider.BVHSet.Elements[item.worldColliderId];
                var colliderBox = collider.ownBounds.ToBounds();
                if (item.rigidbodyId == 0 || collider.shape != ColliderShapeCommon.Shape.box || collider.triangleTreeId != 0
                    || (colliderBox.center - ProbeBox.center).sqrMagnitude > 0.000001f
                    || (colliderBox.size - ProbeBox.size).sqrMagnitude > 0.000001f)
                    throw new InvalidOperationException("Native dropped item did not allocate its independent box and rigidbody.");
                VerifyRender(GlobalRenderer.renderer, item.renderId1, native.renderData.clusterStart);

                previewObject = new GameObject("Keel native item preview probe");
                var preview = previewObject.AddComponent<ItemPreview>();
                preview.SetNewItem(new WorldItemPlayer(item.id));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var renderer = (EvoRenderer)typeof(ItemPreview).GetField("evoRenderer", flags)!.GetValue(preview)!;
                var previewItem = (ItemInstanceData)typeof(ItemPreview).GetField("previewItemData", flags)!.GetValue(preview)!;
                VerifyRender(renderer, previewItem.renderId1, native.renderData.clusterStart);
                preview.SetNewItem(null!);
            }
            finally
            {
                if (previewObject != null) Destroy(previewObject);
                WorldItemPlayer.WorldItems.Remove(item.id);
                item.ReleaseComponentsAndItemId(unlinkTerrain: false);
            }
            Logger.LogInfo($"KEEL_ITEM_VISUAL PASS prefab={item.prefabId} cluster={native.renderData.clusterStart} box={ProbeBox.size}");
            VerifyInventoryIcon(item.prefabId);
        }

        private void VerifyInventoryIcon(int prefabId)
        {
            var atlas = Shader.GetGlobalTexture("ITEM_ICON_TEX") as Texture2DArray;
            if (atlas == null || !Item.WorldItemIndex.TryGetValue((WorldPropCommon.WorldPropId)prefabId, out var codex)
                || codex.SpriteItemID >= atlas.depth)
                throw new InvalidOperationException("The stock icon atlas did not include the sample recipe's item.");
            var request = AsyncGPUReadback.Request(atlas, 0, 0, atlas.width, 0, atlas.height, codex.SpriteItemID, 1,
                TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("Could not read the generated item icon.");
            var pixels = request.GetData<Color32>();
            int opaque = 0;
            int tinted = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.a < 32) continue;
                opaque++;
                if (pixel.r > pixel.g + 12 && pixel.b > pixel.g + 5) tinted++;
            }
            if (opaque < 16 || tinted < opaque / 10)
                throw new InvalidOperationException($"Native icon did not render the tinted item. opaque={opaque} tinted={tinted}");
            Logger.LogInfo($"KEEL_ITEM_ICON PASS slice={codex.SpriteItemID} opaque={opaque} tinted={tinted}");
        }

        private static void VerifyRender(EvoRenderer renderer, int renderId, int cluster)
        {
            var render = renderer.GetElement(renderId);
            if (render.clusterStart != cluster || ItemInstanceRenderResources.datas[render.dataId].color != ProbeTint.GammaToLinear().Pack())
                throw new InvalidOperationException("Native item renderer did not submit the custom mesh and tint.");
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
