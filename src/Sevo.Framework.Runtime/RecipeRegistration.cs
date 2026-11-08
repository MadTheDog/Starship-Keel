using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>Describes a recipe for the player's inventory, crafting bench, or assembler.</summary>
    public sealed class RecipeDefinition
    {
        public CraftingLevel CraftingLevel { get; }
        public float DurationSeconds { get; }
        public CraftingElement Output { get; }
        public IReadOnlyList<CraftingElement> Inputs { get; }

        public RecipeDefinition(CraftingLevel craftingLevel, float durationSeconds, CraftingElement output,
            params CraftingElement[] inputs)
        {
            if (craftingLevel != CraftingLevel.player && craftingLevel != CraftingLevel.craftingBench
                && craftingLevel != CraftingLevel.assembler)
                throw new ArgumentOutOfRangeException(nameof(craftingLevel), "This adapter supports player, bench and assembler crafting.");
            if (durationSeconds <= 0f || float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds)
                || float.IsInfinity(1f / durationSeconds))
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            ValidateElement(output);
            // Stock CraftingCommon.GetItemCrafted clears context and omits item flags.
            if (output.contextualDataId != 0 || output.itemFlags != ItemInstanceFlags.none)
                throw new ArgumentException("The selected native stations require an output with default context and flags.", nameof(output));
            if ((double)output.quantity > int.MaxValue || Math.Truncate(output.quantity) != output.quantity)
                throw new ArgumentOutOfRangeException(nameof(output), "Native crafting requires a whole output count.");
            // Stock Register() uses the first empty slot to record the input length.
            if (inputs == null || inputs.Length < 1 || inputs.Length > 5)
                throw new ArgumentException("Native recipe registration requires one to five inputs.", nameof(inputs));

            var snapshot = (CraftingElement[])inputs.Clone();
            foreach (var input in snapshot) ValidateElement(input);
            CraftingLevel = craftingLevel;
            DurationSeconds = durationSeconds;
            Output = output;
            Inputs = Array.AsReadOnly(snapshot);
        }

        private static void ValidateElement(CraftingElement element)
        {
            if (element.itemType == WorldItemType.none || !Enum.IsDefined(typeof(WorldItemType), element.itemType)
                || element.id < 0 || element.quantity <= 0f || float.IsNaN(element.quantity)
                || float.IsInfinity(element.quantity) || float.IsNaN(element.additionalData)
                || float.IsInfinity(element.additionalData))
                throw new ArgumentException("An ingredient needs a valid native item identity and a finite positive quantity.");
        }
    }

    /// <summary>Register during dependent plugin Awake. Factories run on the game thread after native recipe allocation.</summary>
    public static class RecipeRegistration
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, RecipeDeclaration> Declarations = new Dictionary<string, RecipeDeclaration>(StringComparer.Ordinal);
        private static bool sealedCatalog;
        private static IReadOnlyDictionary<string, int>? publishedIds;

        public static void Register(string ownerId, string localName, int revision, Func<RecipeDefinition> describeRecipe)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
            if (describeRecipe == null) throw new ArgumentNullException(nameof(describeRecipe));
            lock (Gate)
            {
                if (sealedCatalog) throw new InvalidOperationException("Recipe registration is closed for this game process.");
                if (Declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate recipe key: " + key);
                Declarations.Add(key, new RecipeDeclaration(key, revision, describeRecipe));
            }
        }

        /// <summary>Resolves an admitted recipe after its live native table is ready.</summary>
        public static bool TryGetSchematicId(string key, out int id)
        {
            lock (Gate)
            {
                if (key != null && publishedIds != null && publishedIds.TryGetValue(key, out id))
                    return true;
                id = 0;
                return false;
            }
        }

        internal static void PublishIds(IReadOnlyDictionary<string, int>? ids)
        {
            lock (Gate) publishedIds = ids;
        }

        internal static RecipeDeclaration[] Seal()
        {
            lock (Gate)
            {
                sealedCatalog = true;
                return Declarations.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
            }
        }
    }

    internal sealed class RecipeDeclaration
    {
        internal string Key { get; }
        internal int Revision { get; }
        internal Func<RecipeDefinition> Describe { get; }

        internal RecipeDeclaration(string key, int revision, Func<RecipeDefinition> describe)
        {
            Key = key;
            Revision = revision;
            Describe = describe;
        }
    }
}
