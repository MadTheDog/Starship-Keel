using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public enum MachineKind
    {
        Crusher,
        Furnace
    }

    /// <summary>One exact input stack and one output for a supported stock processing machine.</summary>
    public sealed class MachineRecipeDefinition
    {
        public MachineKind Machine { get; }
        public float DurationSeconds { get; }
        public CraftingElement Input { get; }
        public CraftingElement Output { get; }

        public MachineRecipeDefinition(MachineKind machine, float durationSeconds, CraftingElement input,
            CraftingElement output)
        {
            if (machine != MachineKind.Crusher && machine != MachineKind.Furnace)
                throw new ArgumentOutOfRangeException(nameof(machine));
            if (!float.IsFinite(durationSeconds) || durationSeconds < 0.05f || durationSeconds > 3600f)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            Validate(input, nameof(input));
            Validate(output, nameof(output));
            if (Matches(input, output.ToItemData))
                throw new ArgumentException("A processing recipe must change the item identity.", nameof(output));
            Machine = machine;
            DurationSeconds = durationSeconds;
            Input = input;
            Output = output;
        }

        private static void Validate(CraftingElement element, string parameter)
        {
            if (element.id <= 0 || !ItemInstanceCommon.IsValidStackQuantity(element.quantity)
                || element.quantity != MathF.Truncate(element.quantity)
                || element.quantity <= 0f || element.itemFlags != ItemInstanceFlags.none || element.additionalData != 0f)
                throw new ArgumentException("Use a positive native prefab ID and whole stack count with default flags and additional data.", parameter);
            var type = element.itemType;
            bool contextual = UsesMaterialContext(type);
            bool exact = type is WorldItemType.craftingParts or WorldItemType.crystal;
            if (contextual ? element.contextualDataId <= 0 : !exact || element.contextualDataId != 0)
                throw new ArgumentException("This identity cannot be represented by an exact supported conveyor request.", parameter);
        }

        internal static bool UsesMaterialContext(WorldItemType type) =>
            type is WorldItemType.ore or WorldItemType.ore_small or WorldItemType.stoneOreBits or WorldItemType.ingot;

        internal static bool Matches(CraftingElement identity, ItemInstanceData item) =>
            item.itemType == identity.itemType && (UsesMaterialContext(identity.itemType) || item.prefabId == identity.id)
            && item.contextualDataId == identity.contextualDataId && item.itemFlags == identity.itemFlags
            && item.additionalData == identity.additionalData;
    }

    /// <summary>Register from a dependent plugin's Awake. Factories resolve live IDs after resource allocation.</summary>
    public static class MachineRecipeRegistration
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Func<MachineRecipeDefinition>> Declarations =
            new Dictionary<string, Func<MachineRecipeDefinition>>(StringComparer.Ordinal);
        private static bool sealedCatalog;
        public static bool IsAvailable => MachineRecipeResources.IsAvailable;

        public static void Register(string ownerId, string localName, int revision, Func<MachineRecipeDefinition> describeRecipe)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (describeRecipe == null) throw new ArgumentNullException(nameof(describeRecipe));
            lock (Gate)
            {
                if (sealedCatalog) throw new InvalidOperationException("Machine recipe registration is closed for this process.");
                if (!Declarations.TryAdd(key, describeRecipe)) throw new InvalidOperationException("Duplicate machine recipe: " + key);
            }
        }

        internal static KeyValuePair<string, Func<MachineRecipeDefinition>>[] Seal()
        {
            lock (Gate)
            {
                sealedCatalog = true;
                return Declarations.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            }
        }
    }
}
