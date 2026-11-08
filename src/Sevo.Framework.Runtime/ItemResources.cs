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
        private readonly int mainThreadId = Environment.CurrentManagedThreadId;
        private readonly Dictionary<int, PublishedItem> owned = new Dictionary<int, PublishedItem>();
        private readonly Dictionary<string, int> publishedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private volatile IReadOnlyDictionary<int, byte[]>? saveIdentities;
        private ItemDeclaration[]? declarations;
        private bool prepared;
        private bool ready;

        private ItemResources(ProfileSession profile, Action<string> warn)
        {
            this.profile = profile;
            this.warn = warn;
            harmony = new Harmony(Plugin.Id + ".items");
        }

        internal static void Install(ProfileSession profile, CapabilityResult capability, Action<string> warn)
        {
            if (!capability.Available) throw new InvalidOperationException(capability.Reason);
            var resources = new ItemResources(profile, warn);
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
                var formatter = typeof(ItemInstanceData).Assembly.GetType("MessagePack.Formatters.Game.ItemSaveDataFormatter", true)!;
                Patch(formatter, "Serialize", prefix: nameof(WriteIdentity));
                Patch(typeof(ItemInstanceData), nameof(ItemInstanceData.LoadSaveData), prefix: nameof(CheckIdentity));
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
            element.itemType == WorldItemType.craftingParts && element.contextualDataId == 0
            && element.itemFlags == ItemInstanceFlags.none && element.additionalData == 0f
            && Owns(element.id);

        internal static bool Owns(int id)
        {
            if (installed is not { ready: true } resources || resources.mainThreadId != Environment.CurrentManagedThreadId
                || !resources.owned.TryGetValue(id, out var item))
                return false;
            var native = ItemInstanceRenderResources.PrefabDatas;
            return native.IsCreated && id < native.Length
                && ReferenceEquals(ItemInstanceRenderResources.WorldProps[id], item.Prop)
                && ReferenceEquals(WorldPropsResources.worldPropsIndex[id], item.Prop)
                && item.Prop != null && (int)item.Prop.Id == id && item.Prop.worldItemType == WorldItemType.craftingParts
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
                var row = ReadTemplate(item.Definition.StockTemplate, managed, index);
                if (item.Prop == null || !ReferenceEquals(StockMesh.GetValue(item.Prop), StockMesh.GetValue(managed[template])))
                    throw new InvalidOperationException($"Managed item {id} lost its stock template mesh during resource reload.");
                rows.Add((id, data, managed[id], index[id], row));
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

            ready = true;
            saveIdentities = owned.ToDictionary(pair => pair.Key, pair => pair.Value.SaveIdentity);
            ItemRegistration.Publish(publishedIds);
            foreach (var pair in publishedIds)
                warn($"Keel registered native item '{pair.Key}' (prefab {pair.Value}).");
        }

        private void Prepare(WorldProp[] managed, WorldProp[] index, string? json)
        {
            var native = ItemInstanceRenderResources.PrefabDatas;
            var plans = new List<(ItemDeclaration Declaration, ItemDefinition Definition)>();
            foreach (var declaration in declarations!)
            {
                try
                {
                    var definition = declaration.Describe() ?? throw new InvalidOperationException("Factory returned no item definition.");
                    ReadTemplate(definition.StockTemplate, managed, index);
                    plans.Add((declaration, definition));
                }
                catch (Exception exception)
                {
                    warn($"Keel item '{declaration.Key}' skipped: {exception.Message}");
                }
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
                    clone.worldItemType = WorldItemType.craftingParts;
                    owned.Add(id, new PublishedItem(plan.Declaration, plan.Definition, clone, profile.ProfileId));
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

        private static PrefabData ReadTemplate(WorldPropCommon.WorldPropId template, WorldProp[] managed, WorldProp[] index)
        {
            var id = (int)template;
            if (id <= 0 || id >= managed.Length || managed[id] == null || managed[id].Id != template
                || !ReferenceEquals(managed[id], index[id]))
                throw new InvalidOperationException("Stock template is absent from the live world-item tables: " + template);

            var data = ItemInstanceRenderResources.PrefabDatas[id];
            var buffer = StockMeshBuffer.GetValue(null) as UnityEngine.ComputeBuffer;
            if (data.itemType != WorldItemType.craftingParts || !data.WorldInteractionData.canBePickedUp
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
                throw new InvalidOperationException("Stock template is not a renderable, physical, stackable crafting part: " + template);
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
            if (__1.itemType != WorldItemType.craftingParts || resources == null || identities == null
                || !identities.TryGetValue(__1.id, out var identity)
                || (resources.mainThreadId == Environment.CurrentManagedThreadId && !Owns(__1.id)))
                return;
            if (__1.data != null && __1.data.Length != 0 && !__1.data.AsSpan().SequenceEqual(identity))
                throw new InvalidOperationException("The native save already uses the custom item's identity payload.");
            __1.data = identity;
        }

        private static void CheckIdentity(ItemSaveData __0)
        {
            var resources = installed;
            if (resources == null || __0.itemType != WorldItemType.craftingParts) return;
            var payload = __0.data;
            bool tagged = payload != null && payload.Length >= SavePrefix.Length
                && payload.AsSpan(0, SavePrefix.Length).SequenceEqual(SavePrefix);
            if (!tagged) return; // Stock also uses this path for in-memory item movement without serialization.
            if (Owns(__0.id))
            {
                if (!payload.AsSpan().SequenceEqual(resources.owned[__0.id].SaveIdentity))
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
            internal byte[] SaveIdentity { get; }
            internal PrefabData LastPublished { get; set; }

            internal PublishedItem(ItemDeclaration declaration, ItemDefinition definition, WorldProp prop, Guid profileId)
            {
                Definition = definition;
                Prop = prop;
                var identity = FormattableString.Invariant($"{profileId:D}\0{declaration.Revision}\0{declaration.Key}");
                SaveIdentity = SavePrefix.Concat(Encoding.UTF8.GetBytes(identity)).ToArray();
            }
        }
    }
}
