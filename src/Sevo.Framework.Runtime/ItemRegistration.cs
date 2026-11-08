using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Sevo.Framework.Sdk;
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

        public ItemDefinition(WorldPropCommon.WorldPropId stockTemplate, string displayName, string description)
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
            element = new CraftingElement(WorldItemType.craftingParts, prefabId, quantity);
            return true;
        }

        /// <summary>Returns an unregistered native item value. Apply it through an appropriate game-thread stock operation.</summary>
        public static bool TryCreateItemData(string key, float quantity, out ItemInstanceData item)
        {
            item = default;
            if (!TryCreateElement(key, quantity, out var element)) return false;
            item = element.ToItemData;
            return true;
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
