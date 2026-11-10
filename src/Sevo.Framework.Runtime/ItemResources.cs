using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Game;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using VoxelEngine;
using World;
using Object = UnityEngine.Object;

namespace Sevo.Framework.Runtime
{
    internal sealed class ItemResources
    {
        private static readonly byte[] SavePrefix = Encoding.ASCII.GetBytes("StarshipKeel.Item/1\0");
        private static readonly FieldInfo StockMesh = AccessTools.DeclaredField(typeof(WorldProp), "mesh");
        private static readonly FieldInfo StockMeshBuffer = AccessTools.DeclaredField(typeof(WorldPropsResources), "propMeshDataBuffer");
        private static ItemResources? installed;

        private readonly ProfileSession profile;
        private readonly Action<string> warn;
        private readonly Harmony harmony;
        private readonly CapabilityResult contextualItems;
        private readonly int mainThreadId = Environment.CurrentManagedThreadId;
        private readonly Dictionary<int, PublishedItem> owned = new Dictionary<int, PublishedItem>();
        private readonly Dictionary<string, int> publishedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private volatile IReadOnlyDictionary<int, byte[]>? saveIdentities;
        private ItemDeclaration[]? declarations;
        private bool prepared;
        private bool ready;

        private ItemResources(ProfileSession profile, CapabilityResult contextualItems, Action<string> warn)
        {
            this.profile = profile;
            this.contextualItems = contextualItems;
            this.warn = warn;
            harmony = new Harmony(Plugin.Id + ".items");
        }

        internal static void Install(ProfileSession profile, CapabilityResult capability, CapabilityResult contextualItems, Action<string> warn)
        {
            if (!capability.Available) throw new InvalidOperationException(capability.Reason);
            var resources = new ItemResources(profile, contextualItems, warn);
            try
            {
                void Patch(Type type, string method, string? prefix = null, string? postfix = null)
                {
                    var target = AccessTools.DeclaredMethod(type, method)
                        ?? throw new InvalidOperationException("Native item hook target unavailable: " + type.FullName + "." + method);
                    resources.harmony.Patch(target, prefix: prefix == null ? null : Hook(prefix),
                        postfix: postfix == null ? null : Hook(postfix));
                }

                // The native item repo calls AllocateResources after populating all stock world props.
                // Item IDs are ready by the time Keel's recipe allocation postfix runs.
                Patch(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources), prefix: nameof(PublishResources));
                Patch(typeof(ItemInstanceRenderResources), nameof(ItemInstanceRenderResources.OnGameReload),
                    prefix: nameof(Unpublish), postfix: nameof(PublishResources));
                Patch(typeof(ItemInstanceRenderResources), nameof(ItemInstanceRenderResources.ReleaseResources), prefix: nameof(Unpublish));
                if (contextualItems.Available)
                    Patch(typeof(World.Terrain.TerrainMaterialCommon), nameof(World.Terrain.TerrainMaterialCommon.GetMaterialName),
                        prefix: nameof(GetManagedMaterialName));
                var formatter = typeof(ItemInstanceData).Assembly.GetType("MessagePack.Formatters.Game.ItemSaveDataFormatter", true)!;
                Patch(formatter, "Serialize", prefix: nameof(WriteIdentity));
                Patch(typeof(ItemInstanceData), nameof(ItemInstanceData.Name), prefix: nameof(GetManagedName));
                Patch(typeof(ItemInstanceData), nameof(ItemInstanceData.Description), prefix: nameof(GetManagedDescription));
                Patch(typeof(SteamLobbyManager), nameof(SteamLobbyManager.OpenLobby), prefix: nameof(CheckLobby));
                Patch(typeof(SteamLobbyManager), nameof(SteamLobbyManager.JoinFriendsLobby), prefix: nameof(CheckLobby));
                installed = resources;
            }
            catch
            {
                resources.harmony.UnpatchSelf();
                throw;
            }
        }

        internal static void InstallSaveGuard(Harmony patches)
        {
            var load = AccessTools.DeclaredMethod(typeof(ItemInstanceData), nameof(ItemInstanceData.LoadSaveData))
                ?? throw new MissingMethodException(nameof(ItemInstanceData.LoadSaveData));
            patches.Patch(load, prefix: Hook(nameof(CheckIdentity)));
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ItemResources), name));

        internal static void Disable()
        {
            if (installed == null) return;
            if (installed.owned.Count != 0)
                throw new InvalidOperationException("Restart before disabling item registration with published definitions.");
            installed.harmony.UnpatchSelf();
            installed = null;
            ItemRegistration.Publish(null);
        }

        internal static bool IsRegistered(CraftingElement element) =>
            element.itemFlags == ItemInstanceFlags.none && element.additionalData == 0f
            && TryCreateElement(element.id, element.quantity, out var expected)
            && expected.itemType == element.itemType && expected.contextualDataId == element.contextualDataId;

        internal static bool TryCreateElement(int id, float quantity, out CraftingElement element)
        {
            element = default;
            if (!Owns(id)) return false;
            var definition = installed!.owned[id].Definition;
            if (definition.MaterialKey == null)
            {
                element = new CraftingElement(definition.Category, id, quantity);
                return true;
            }
            if (!TerrainMaterialRegistration.TryGetId(definition.MaterialKey, out byte material)
                || Enum.IsDefined(typeof(World.Terrain.MaterialGradientType), (World.Terrain.MaterialGradientType)material))
                return false;
            element = new CraftingElement(definition.Category, id, quantity, material, definition.Tint);
            return true;
        }

        private static bool GetManagedName(ref ItemInstanceData __instance, ref string __result)
        {
            if (__instance.itemType is not (WorldItemType.ore or WorldItemType.ingot)
                || !TryCreateElement(__instance.prefabId, 1, out var identity)
                || identity.itemType != __instance.itemType || identity.contextualDataId != __instance.contextualDataId)
                return true;
            __result = installed!.owned[__instance.prefabId].Definition.DisplayName;
            return false;
        }

        private static bool GetManagedDescription(ref ItemInstanceData __instance, ref string __result)
        {
            if (__instance.itemType is not (WorldItemType.ore or WorldItemType.ingot)
                || !TryCreateElement(__instance.prefabId, 1, out var identity)
                || identity.itemType != __instance.itemType || identity.contextualDataId != __instance.contextualDataId)
                return true;
            __result = installed!.owned[__instance.prefabId].Definition.Description;
            return false;
        }

        private static bool GetManagedMaterialName(int __0, int __1, ref string __result)
        {
            if (!TryCreateElement(__1, 1, out var identity) || identity.contextualDataId != __0
                || identity.itemType is not (WorldItemType.ore or WorldItemType.ingot))
                return true;
            var definition = installed!.owned[__1].Definition;
            __result = definition.MaterialSymbol ?? definition.DisplayName;
            return false;
        }

        internal static bool Owns(int id)
        {
            if (installed is not { ready: true } resources || resources.mainThreadId != Environment.CurrentManagedThreadId
                || resources.saveIdentities?.ContainsKey(id) != true
                || !resources.owned.TryGetValue(id, out var item))
                return false;
            var native = ItemInstanceRenderResources.PrefabDatas;
            return native.IsCreated && id < native.Length
                && ReferenceEquals(ItemInstanceRenderResources.WorldProps[id], item.Prop)
                && ReferenceEquals(WorldPropsResources.worldPropsIndex[id], item.Prop)
                && item.Prop != null && (int)item.Prop.Id == id && item.Prop.worldItemType == item.Definition.Category
                && native[id].Equals(item.LastPublished);
        }

        private static void Unpublish()
        {
            if (installed != null)
            {
                installed.ready = false;
                installed.saveIdentities = null;
            }
            ItemRegistration.Publish(null);
        }

        private static void PublishResources()
        {
            var resources = installed;
            if (resources == null) return;
            Unpublish();
            try { resources.Publish(); }
            catch (Exception exception)
            {
                Unpublish();
                resources.warn("Keel item registration unavailable for this resource cycle: " + exception.Message);
            }
        }

        private void Publish()
        {
            declarations ??= ItemRegistration.Seal();
            var json = prepared ? null : profile.ReadFeatureRegistry(ResourceKind.Item);
            if (declarations.Length == 0 && json == null && owned.Count == 0) return;
            if (PlayerState.sessionMode is PlayerState.SessionMode.host or PlayerState.SessionMode.client)
                throw new InvalidOperationException("Managed item registration requires a solo session.");

            var native = ItemInstanceRenderResources.PrefabDatas;
            var managed = ItemInstanceRenderResources.WorldProps;
            var index = WorldPropsResources.worldPropsIndex;
            if (!native.IsCreated || native.Length != managed.Length || native.Length != index.Length)
                throw new InvalidOperationException("Native and managed world-item tables have different capacities.");

            if (!prepared)
            {
                try { Prepare(managed, index, json); }
                catch (Exception exception) when (json == null && owned.Count == 0)
                {
                    warn("Keel item registration skipped: " + exception.Message);
                }
                prepared = true;
            }

            var rows = new List<(int Id, PrefabData OldData, WorldProp OldManaged, WorldProp OldIndex, PrefabData NewData)>();
            var visuals = new List<(int Id, ItemVisualResources.MeshRow Mesh, PrefabData Stock)>();
            foreach (var pair in owned)
            {
                var item = pair.Value;
                var id = pair.Key;
                var data = native[id];
                if ((!ReferenceEquals(managed[id], null) && !ReferenceEquals(managed[id], item.Prop))
                    || (!ReferenceEquals(index[id], null) && !ReferenceEquals(index[id], item.Prop))
                    || (!data.Equals(default(PrefabData)) && !data.Equals(item.LastPublished)))
                    throw new InvalidOperationException($"Managed item slot {id} was occupied by another producer during resource reload.");
                var template = (int)item.Definition.StockTemplate;
                var row = ReadTemplate(item.Definition, managed, index);
                if (item.Prop == null || !ReferenceEquals(StockMesh.GetValue(item.Prop), StockMesh.GetValue(managed[template])))
                    throw new InvalidOperationException($"Managed item {id} lost its stock template mesh during resource reload.");
                rows.Add((id, data, managed[id], index[id], row));
                if (item.Visual != null) visuals.Add((id, item.Visual, row));
            }
            try
            {
                ItemVisualResources.Bind(visuals);
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var visual = owned[row.Id].Visual;
                    if (visual == null) continue;
                    row.NewData = visual.Apply(row.NewData);
                    rows[i] = row;
                }
            }
            catch (Exception exception)
            {
                warn("Keel item visuals unavailable: " + exception.Message);
                rows.RemoveAll(row => owned[row.Id].Visual != null);
            }
            int written = 0;
            try
            {
                foreach (var row in rows)
                {
                    native[row.Id] = row.NewData;
                    written++;
                    var item = owned[row.Id];
                    managed[row.Id] = item.Prop;
                    index[row.Id] = item.Prop;
                }
            }
            catch
            {
                for (var i = written - 1; i >= 0; i--)
                {
                    var row = rows[i];
                    native[row.Id] = row.OldData;
                    managed[row.Id] = row.OldManaged;
                    index[row.Id] = row.OldIndex;
                }
                throw;
            }
            foreach (var row in rows) owned[row.Id].LastPublished = row.NewData;

            var active = rows.Select(row => row.Id).ToHashSet();
            var activeIds = publishedIds.Where(pair => active.Contains(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            ready = true;
            saveIdentities = owned.Where(pair => active.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.SaveIdentity);
            ItemRegistration.Publish(activeIds);
            foreach (var pair in activeIds)
                warn($"Keel registered native item '{pair.Key}' (prefab {pair.Value}).");
        }

        private void Prepare(WorldProp[] managed, WorldProp[] index, string? json)
        {
            var native = ItemInstanceRenderResources.PrefabDatas;
            var plans = new List<(ItemDeclaration Declaration, ItemDefinition Definition, ItemVisualResources.MeshRow? Visual)>();
            foreach (var declaration in declarations!)
            {
                try
                {
                    var definition = declaration.Describe() ?? throw new InvalidOperationException("Factory returned no item definition.");
                    if (definition.MaterialKey != null && !contextualItems.Available)
                        throw new InvalidOperationException(contextualItems.Reason);
                    ReadTemplate(definition, managed, index);
                    if (definition.Visual != null && !ItemVisualResources.Enabled)
                        throw new InvalidOperationException("Requires the optional item visuals capability.");
                    var visual = definition.Visual == null ? null : new ItemVisualResources.MeshRow(definition.Visual);
                    plans.Add((declaration, definition, visual));
                }
                catch (Exception exception)
                {
                    warn($"Keel item '{declaration.Key}' skipped: {exception.Message}");
                }
            }
            var conflicts = plans.Where(plan => plan.Definition.MaterialKey != null)
                .GroupBy(plan => (plan.Definition.Category, plan.Definition.MaterialKey))
                .Where(group => group.Count() > 1).SelectMany(group => group).ToArray();
            foreach (var plan in conflicts)
            {
                warn($"Keel item '{plan.Declaration.Key}' skipped because its category and material are already claimed.");
                plans.Remove(plan);
            }
            if (plans.Count == 0 && json == null) return;

            var reserved = Enum.GetValues(typeof(WorldPropCommon.WorldPropId)).Cast<WorldPropCommon.WorldPropId>()
                .Select(id => (int)id).Where(id => id >= 0 && id < native.Length).Append(0).Distinct();
            var occupied = new List<int>();
            for (var id = 0; id < native.Length; id++)
                if (!ReferenceEquals(managed[id], null) || !ReferenceEquals(index[id], null) || !native[id].Equals(default(PrefabData)))
                    occupied.Add(id);

            var space = new ResourceSpace(ResourceKind.Item, native.Length, reserved, occupied);
            var allocator = json == null
                ? new ContentAllocator(new[] { space }, profile.ProfileId)
                : ContentAllocator.FromJson(json, new[] { space });
            var assignments = allocator.Apply(plans.Select(plan =>
                new ContentRequest(plan.Declaration.Key, ResourceKind.Item, plan.Declaration.Revision)));

            var clones = new List<WorldProp>();
            try
            {
                foreach (var plan in plans)
                {
                    var id = assignments[plan.Declaration.Key];
                    var clone = Object.Instantiate(managed[(int)plan.Definition.StockTemplate]);
                    clones.Add(clone);
                    AccessTools.DeclaredField(typeof(WorldProp), "worldPropId").SetValue(clone, (WorldPropCommon.WorldPropId)id);
                    AccessTools.DeclaredField(typeof(WorldProp), "nameInShader").SetValue(clone, plan.Definition.DisplayName);
                    AccessTools.DeclaredField(typeof(WorldProp), "description").SetValue(clone, plan.Definition.Description);
                    clone.name = plan.Declaration.Key;
                    clone.worldItemType = plan.Definition.Category;
                    owned.Add(id, new PublishedItem(plan.Declaration, plan.Definition, plan.Visual, clone, profile.ProfileId));
                    publishedIds.Add(plan.Declaration.Key, id);
                }

                var next = allocator.ToJson();
                if (!string.Equals(next, json, StringComparison.Ordinal)) profile.WriteFeatureRegistry(ResourceKind.Item, next);
            }
            catch
            {
                foreach (var clone in clones) Object.Destroy(clone);
                owned.Clear();
                publishedIds.Clear();
                throw;
            }
        }

        private static PrefabData ReadTemplate(ItemDefinition definition, WorldProp[] managed, WorldProp[] index)
        {
            var template = definition.StockTemplate;
            var id = (int)template;
            if (id <= 0 || id >= managed.Length || managed[id] == null || managed[id].Id != template
                || !ReferenceEquals(managed[id], index[id]))
                throw new InvalidOperationException("Stock template is absent from the live world-item tables: " + template);

            var data = ItemInstanceRenderResources.PrefabDatas[id];
            var buffer = StockMeshBuffer.GetValue(null) as UnityEngine.ComputeBuffer;
            bool compatible = data.itemType == definition.Category
                || (definition.Category == WorldItemType.ore && data.itemType == WorldItemType.rockSmall);
            if (!compatible || !data.WorldInteractionData.canBePickedUp
                || !(StockMesh.GetValue(managed[id]) is UnityEngine.Mesh mesh) || mesh == null
                || buffer == null || !buffer.IsValid() || buffer.stride != Marshal.SizeOf<PropMeshData>()
                || !ValidRenderRange(data.renderData.clusterStart, data.renderData.clusterCount, buffer.count)
                || (data.renderData.clusterCountLOD != 0
                    && !ValidRenderRange(data.renderData.clusterStartLOD, data.renderData.clusterCountLOD, buffer.count))
                || (data.renderData2.IsValid
                    && !ValidRenderRange(data.renderData2.clusterStart, data.renderData2.clusterCount, buffer.count))
                || !PositiveFinite(data.radius) || !PositiveFinite(data.mass) || !PositiveFinite(data.volume)
                || !PositiveFinite(data.healthPoint) || (!managed[id].forceCollider && data.triangleTreeId <= 0)
                || !Enum.IsDefined(typeof(ColliderShapeCommon.Shape), data.shape))
                throw new InvalidOperationException("Stock template is not a renderable, physical, stackable item of the requested category: " + template);
            data.itemType = definition.Category;
            return data;
        }

        private static bool PositiveFinite(float value) => value > 0f && !float.IsInfinity(value);

        // Stock RenderData stores the starting triangle, with fourteen triangles per cluster.
        private static bool ValidRenderRange(int start, int clusters, int vertices) =>
            start >= 0 && clusters > 0 && (3L * start) + (42L * clusters) <= vertices;

        private static void WriteIdentity(ref ItemSaveData __1)
        {
            var resources = installed;
            var identities = resources?.saveIdentities;
            if (resources == null || identities == null
                || !identities.TryGetValue(__1.id, out var identity)
                || resources.owned[__1.id].Definition.Category != __1.itemType)
                return;
            if (resources.mainThreadId == Environment.CurrentManagedThreadId
                && (!TryCreateElement(__1.id, 1, out var expected) || expected.contextualDataId != __1.contextualDataId))
                throw new InvalidOperationException($"Managed item ID {__1.id} lost its native identity before save.");
            if (__1.data != null && __1.data.Length != 0 && !__1.data.AsSpan().SequenceEqual(identity))
                throw new InvalidOperationException("The native save already uses the custom item's identity payload.");
            __1.data = identity;
        }

        private static void CheckIdentity(ItemSaveData __0)
        {
            var resources = installed;
            var payload = __0.data;
            bool tagged = payload != null && payload.Length >= SavePrefix.Length
                && payload.AsSpan(0, SavePrefix.Length).SequenceEqual(SavePrefix);
            if (!tagged) return; // Stock also uses this path for in-memory item movement without serialization.
            if (TryCreateElement(__0.id, 1, out var expected))
            {
                if (expected.itemType != __0.itemType || expected.contextualDataId != __0.contextualDataId
                    || !payload.AsSpan().SequenceEqual(resources!.owned[__0.id].SaveIdentity))
                    throw new InvalidOperationException($"Managed item ID {__0.id} does not match its loaded definition. Restore its Keel item registry and mod.");
            }
            else
            {
                throw new InvalidOperationException($"Managed item ID {__0.id} is unavailable. Restore its Keel item registry and mod.");
            }
        }

        private static bool CheckLobby(ref Task<bool> __result)
        {
            if (installed == null || installed.owned.Count == 0) return true;
            installed.warn("Multiplayer is unavailable while managed item definitions are loaded. Restart without managed items to use lobbies.");
            __result = Task.FromResult(false);
            return false;
        }

        private sealed class PublishedItem
        {
            internal ItemDefinition Definition { get; }
            internal WorldProp Prop { get; }
            internal ItemVisualResources.MeshRow? Visual { get; }
            internal byte[] SaveIdentity { get; }
            internal PrefabData LastPublished { get; set; }

            internal PublishedItem(ItemDeclaration declaration, ItemDefinition definition, ItemVisualResources.MeshRow? visual,
                WorldProp prop, Guid profileId)
            {
                Definition = definition;
                Visual = visual;
                Prop = prop;
                var identity = FormattableString.Invariant($"{profileId:D}\0{declaration.Revision}\0{declaration.Key}");
                if (definition.MaterialKey != null) identity += $"\0{(int)definition.Category}\0{definition.MaterialKey}";
                SaveIdentity = SavePrefix.Concat(Encoding.UTF8.GetBytes(identity)).ToArray();
            }
        }
    }
}
