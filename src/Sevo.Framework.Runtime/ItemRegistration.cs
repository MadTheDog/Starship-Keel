using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>A stackable native crafting part using the appearance and physics of a live stock part.</summary>
    public sealed class ItemDefinition
    {
        public WorldPropCommon.WorldPropId StockTemplate { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public ItemVisualDefinition? Visual { get; }
        public WorldItemType Category { get; }
        public string? MaterialKey { get; }
        public string? MaterialSymbol { get; }
        public Color32 Tint { get; }

        public ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description)
            : this(stockTemplate, displayName, description, null)
        {
        }

        public ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description,
            ItemVisualDefinition? visual)
            : this(stockTemplate, displayName, description, visual, WorldItemType.craftingParts, null, default)
        {
        }

        public ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description,
            WorldItemType category, string materialKey, Color32 tint)
            : this(stockTemplate, displayName, description, category, materialKey, tint, null)
        {
        }

        /// <summary>Creates a contextual item with an optional compact symbol for native inventory slots.</summary>
        public ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description,
            WorldItemType category, string materialKey, Color32 tint, string? materialSymbol)
            : this(stockTemplate, displayName, description, null, category, materialKey, tint)
        {
            if (category is not (WorldItemType.ore or WorldItemType.ingot))
                throw new ArgumentOutOfRangeException(nameof(category));
            ContentAllocator.ValidateKey(materialKey);
            if (materialSymbol != null && (materialSymbol.Length is < 1 or > 4
                || materialSymbol.Any(character => character is not (>= 'a' and <= 'z'
                    or >= 'A' and <= 'Z' or >= '0' and <= '9'))))
                throw new ArgumentException("Use 1 to 4 ASCII letters or digits for the material symbol.", nameof(materialSymbol));
            MaterialSymbol = materialSymbol;
        }

        private ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description,
            ItemVisualDefinition? visual, WorldItemType category, string? materialKey, Color32 tint)
        {
            if ((int)stockTemplate <= 0 || !Enum.IsDefined(typeof(WorldPropCommon.WorldPropId), stockTemplate))
                throw new ArgumentOutOfRangeException(nameof(stockTemplate));
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
                throw new ArgumentException("An item needs a display name of at most 128 characters.", nameof(displayName));
            if (description == null || description.Length > 2048)
                throw new ArgumentException("Supply a description of at most 2048 characters.", nameof(description));

            StockTemplate = stockTemplate;
            DisplayName = displayName;
            Description = description;
            Visual = visual;
            Category = category;
            MaterialKey = materialKey;
            Tint = new Color32(tint.r, tint.g, tint.b, 255);
        }
    }

    public enum ItemSurfaceMode
    {
        StockMaterial,
        Tinted
    }

    /// <summary>A readable mesh in local metres with a native surface and independent box collider.</summary>
    public sealed class ItemVisualDefinition
    {
        public Mesh Mesh { get; }
        public Color32 Tint { get; }
        public Bounds ColliderBox { get; }
        public ItemSurfaceMode Surface { get; }

        public ItemVisualDefinition(Mesh mesh, Color32 tint, Bounds colliderBox)
            : this(mesh, tint, colliderBox, ItemSurfaceMode.StockMaterial)
        {
        }

        public ItemVisualDefinition(Mesh mesh, Color32 tint, Bounds colliderBox, ItemSurfaceMode surface)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (surface != ItemSurfaceMode.StockMaterial && surface != ItemSurfaceMode.Tinted)
                throw new ArgumentOutOfRangeException(nameof(surface));
            Mesh = mesh;
            Tint = new Color32(tint.r, tint.g, tint.b, 255);
            ColliderBox = colliderBox;
            Surface = surface;
        }
    }

    /// <summary>Declare items from dependent plugin Awake. Factories run on the game thread when stock item resources are ready.</summary>
    public static class ItemRegistration
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ItemDeclaration> Declarations = new Dictionary<string, ItemDeclaration>(StringComparer.Ordinal);
        private static IReadOnlyDictionary<string, int>? published;
        private static bool sealedCatalog;

        /// <summary>Whether the current resource cycle published a nonempty item catalog.</summary>
        public static bool IsAvailable
        {
            get { lock (Gate) return published is { Count: > 0 }; }
        }

        public static void Register(string ownerId, string localName, int revision, Func<ItemDefinition> describeItem)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
            if (describeItem == null) throw new ArgumentNullException(nameof(describeItem));
            lock (Gate)
            {
                if (sealedCatalog) throw new InvalidOperationException("Item registration is closed for this game process.");
                if (Declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate item key: " + key);
                Declarations.Add(key, new ItemDeclaration(key, revision, describeItem));
            }
        }

        /// <summary>Resolves a published item in the current resource cycle.</summary>
        public static bool TryGetPrefabId(string key, out int prefabId)
        {
            lock (Gate)
            {
                if (key != null && published != null && published.TryGetValue(key, out prefabId)
                    && ItemResources.Owns(prefabId))
                    return true;
                prefabId = 0;
                return false;
            }
        }

        /// <summary>Constructs a native crafting element for a published item. This does not add items to a world.</summary>
        public static bool TryCreateElement(string key, float quantity, out CraftingElement element)
        {
            element = default;
            if (quantity <= 0f || !ItemInstanceCommon.IsValidStackQuantity(quantity) || !TryGetPrefabId(key, out var prefabId))
                return false;
            return ItemResources.TryCreateElement(prefabId, quantity, out element);
        }

        /// <summary>Returns an unregistered native item value. Apply it through an appropriate game-thread stock operation.</summary>
        public static bool TryCreateItemData(string key, float quantity, out ItemInstanceData item)
        {
            item = default;
            if (!TryCreateElement(key, quantity, out var element)) return false;
            item = element.ToItemData;
            return true;
        }

        internal static string[] GetPublishedKeys()
        {
            lock (Gate)
                return published?.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        }
        internal static ItemDeclaration[] Seal()
        {
            lock (Gate)
            {
                sealedCatalog = true;
                return Declarations.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
            }
        }

        internal static void Publish(IReadOnlyDictionary<string, int>? ids)
        {
            lock (Gate) published = ids;
        }
    }

    internal sealed class ItemDeclaration
    {
        internal string Key { get; }
        internal int Revision { get; }
        internal Func<ItemDefinition> Describe { get; }

        internal ItemDeclaration(string key, int revision, Func<ItemDefinition> describe)
        {
            Key = key;
            Revision = revision;
            Describe = describe;
        }
    }
}
