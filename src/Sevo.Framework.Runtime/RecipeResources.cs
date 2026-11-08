using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Game;
using HarmonyLib;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class RecipeResources
    {
        private static RecipeResources? installed;
        private static Harmony? activeHook;
        private readonly ProfileSession profile;
        private readonly Action<string> warn;
        private readonly SortedDictionary<int, CraftingSchematic> owned = new SortedDictionary<int, CraftingSchematic>();
        private bool prepared;
        private IReadOnlyDictionary<string, int>? registeredIds;

        private RecipeResources(ProfileSession profile, Action<string> warn)
        {
            this.profile = profile;
            this.warn = warn;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static CapabilityResult Install(ProfileSession profile, CapabilityResult capability, Action<string> warn)
        {
            if (!capability.Available) return capability;

            var hook = new Harmony(Plugin.Id + ".recipes");
            try
            {
                var method = AccessTools.DeclaredMethod(typeof(CraftingSchematic), nameof(CraftingSchematic.AllocateResources), Type.EmptyTypes)
                    ?? throw new InvalidOperationException("Native recipe resource callback unavailable.");
                hook.Patch(method, prefix: new HarmonyMethod(typeof(RecipeResources), nameof(BeforeAllocation)),
                    postfix: new HarmonyMethod(typeof(RecipeResources), nameof(AfterAllocation)),
                    finalizer: new HarmonyMethod(typeof(RecipeResources), nameof(AfterFailure)));
                installed = new RecipeResources(profile, warn);
                activeHook = hook;
                return capability;
            }
            catch (Exception exception)
            {
                hook.UnpatchSelf();
                return new CapabilityResult(capability.Name, false, "Recipe hook unavailable: " + exception.Message);
            }
        }

        internal static void Disable()
        {
            var hook = activeHook;
            activeHook = null;
            installed = null;
            RecipeRegistration.PublishIds(null);
            hook?.UnpatchSelf();
        }

        private static void BeforeAllocation(out bool __state)
        {
            __state = false;
            var resources = installed;
            if (resources == null || resources.owned.Count == 0) return;
            RecipeRegistration.PublishIds(null);
            var index = CraftingSchematic.Index;
            foreach (var pair in resources.owned)
                if (pair.Key >= index.Length || !index[pair.Key].Equals(pair.Value))
                    throw new InvalidOperationException("A registered schematic slot changed during this process. Restart before loading a world.");
            __state = true;
            foreach (var pair in resources.owned) index[pair.Key] = default;
        }

        private static void AfterAllocation(bool __state)
        {
            var resources = installed;
            if (resources == null) return;
            if (__state) resources.RestoreManagedRows();
            resources.PublishNativeRows();
        }

        private static Exception? AfterFailure(Exception? __exception, bool __state)
        {
            if (__exception != null && __state) installed?.RestoreManagedRows();
            return __exception;
        }

        private void RestoreManagedRows()
        {
            var index = CraftingSchematic.Index;
            foreach (var pair in owned)
                if (!index[pair.Key].Equals(default(CraftingSchematic)) && !index[pair.Key].Equals(pair.Value))
                    throw new InvalidOperationException("Another producer occupied a managed schematic slot during reload. Restart before loading a world.");
            foreach (var pair in owned) index[pair.Key] = pair.Value;
        }

        private void PublishNativeRows()
        {
            var index = CraftingSchematic.Index;
            var native = CraftingSchematic.schematicsNativeArray;
            if (!prepared)
            {
                var declarations = RecipeRegistration.Seal();
                try
                {
                    Prepare(index, declarations);
                    prepared = true;
                }
                catch (Exception exception) when (owned.Count == 0)
                {
                    prepared = true;
                    warn("Keel recipe registration skipped: " + exception.Message);
                }
            }

            if (owned.Count == 0) return;
            if (!native.IsCreated || native.Length != index.Length)
                throw new InvalidOperationException("Native schematic table changed after admission. Restart before loading a world.");
            foreach (var pair in owned) native[pair.Key] = pair.Value;
            RecipeRegistration.PublishIds(registeredIds);
        }

        private void Prepare(CraftingSchematic[] index, RecipeDeclaration[] declarations)
        {
            var json = profile.ReadFeatureRegistry(ResourceKind.Schematic);
            if (declarations.Length == 0 && json == null) return;

            var native = CraftingSchematic.schematicsNativeArray;
            if (!native.IsCreated || native.Length != index.Length)
                throw new InvalidOperationException("Managed and native schematic capacities differ.");
            var occupied = Enumerable.Range(0, index.Length).Where(id => !index[id].Equals(default(CraftingSchematic))).ToArray();
            var outputIdentities = index.Where(value => value.IsValid).Select(value => value.output).ToArray();
            var inputIdentities = index.Where(value => value.IsValid)
                .SelectMany(value => Enumerable.Range(0, value.InputsLength).Select(i => value[i]))
                .Concat(outputIdentities).ToArray();
            var plans = new List<(RecipeDeclaration declaration, CraftingSchematic row)>();
            foreach (var declaration in declarations)
            {
                try
                {
                    var definition = declaration.Describe()
                        ?? throw new InvalidOperationException("Recipe factory returned no definition.");
                    plans.Add((declaration, CreateRow(definition, index, inputIdentities, outputIdentities)));
                }
                catch (Exception exception)
                {
                    warn("Keel recipe '" + declaration.Key + "' skipped: " + exception.Message);
                }
            }

            // Dynamic stock occupancy determines the additive ID range. Never give a replacement
            // recipe precedence over a live stock recipe or a prior producer in an existing ID.
            var minModId = occupied.Length == 0 ? 1 : Math.Max(1, occupied[occupied.Length - 1] + 1);
            var savedIds = json == null ? Array.Empty<int>() : RegistryJson.Read(json).Mappings
                .Where(mapping => mapping.Id > 0).Select(mapping => mapping.Id).ToArray();
            var reserved = Enumerable.Range(0, minModId).Except(savedIds);
            var space = new ResourceSpace(ResourceKind.Schematic, index.Length, reserved, occupied);
            var allocator = json == null
                ? new ContentAllocator(new[] { space }, profile.ProfileId)
                : ContentAllocator.FromJson(json, new[] { space });
            var assignments = allocator.Apply(plans.Select(plan =>
                new ContentRequest(plan.declaration.Key, ResourceKind.Schematic, plan.declaration.Revision)));
            foreach (var plan in plans)
            {
                var id = assignments[plan.declaration.Key];
                if (!index[id].Equals(default(CraftingSchematic)))
                    throw new InvalidOperationException("Schematic slot was occupied before publication: " + plan.declaration.Key);
                ValidateBrickRecipePriority(plan.row.output, id);
            }

            var next = allocator.ToJson();
            if (!string.Equals(json, next, StringComparison.Ordinal))
                profile.WriteFeatureRegistry(ResourceKind.Schematic, next);

            foreach (var plan in plans)
            {
                var row = plan.row;
                row.id = assignments[plan.declaration.Key];


                // Stock Register updates Index, Codex lists and output links. If it throws after
                // mutation, failing the resource callback is safer than continuing into a world.
                owned.Add(row.id, row);
                var registered = row.Register();
                var indexed = index[row.id];
                if (!registered.IsValid || registered.InputsLength != CountInputs(plan.row)
                    || !indexed.IsValid || indexed.id != row.id)
                    throw new InvalidOperationException("Native schematic registration did not publish the planned recipe: " + plan.declaration.Key);
                owned[row.id] = indexed;
                warn("Keel registered native recipe '" + plan.declaration.Key + "' (schematic " + row.id + ").");
            }
            registeredIds = assignments;
        }

        private static CraftingSchematic CreateRow(RecipeDefinition definition, CraftingSchematic[] index,
            CraftingElement[] inputIdentities, CraftingElement[] outputIdentities)
        {
            var output = definition.Output;
            ValidateLiveIdentity(output, outputIdentities);
            const int gridLevel = 3;
            if (output.itemType == WorldItemType.brick && Brick.BRICK_INDEX[output.id] is Brick outputBrick
                && (double)output.quantity * BrickCommon.GetQuantityFactor(outputBrick, gridLevel) > int.MaxValue)
                throw new InvalidOperationException("The scaled output count exceeds the native integer range.");
            var inputs = definition.Inputs.Select(input =>
            {
                ValidateLiveIdentity(input, inputIdentities);
                input.schematicId = input.itemType == WorldItemType.craftingParts
                    ? Array.FindIndex(index, row => row.IsValid && row.output.itemType == input.itemType && row.output.id == input.id)
                    : 0;
                if (input.schematicId < 0)
                {
                    if (!ItemResources.IsRegistered(input))
                        throw new InvalidOperationException("A crafting part has no native source schematic.");
                    // Gathered parts can be supplied directly. A zero ID prevents the assembler from queueing a precursor.
                    input.schematicId = 0;
                }
                return input;
            }).ToArray();
            // Stock availability checks each row independently. Overlapping entries could
            // count one stack twice and allow crafting without consuming all declared inputs.
            var storage = StorageCommon.CONTEXT;
            for (int i = 0; i < inputs.Length; i++)
                for (int j = i + 1; j < inputs.Length; j++)
                    if (inputs[i].IsMatching(inputs[j].ToItemData, storage)
                        || inputs[j].IsMatching(inputs[i].ToItemData, storage))
                        throw new InvalidOperationException("Overlapping ingredients must be combined into one quantity.");

            return new CraftingSchematic
            {
                craftingLevel = definition.CraftingLevel,
                output = output,
                duration = definition.DurationSeconds,
                scale = 1f,
                gridLevel = gridLevel,
                rotation = Quaternion.identity,
                element0 = inputs[0],
                element1 = inputs.Length > 1 ? inputs[1] : default,
                element2 = inputs.Length > 2 ? inputs[2] : default,
                element3 = inputs.Length > 3 ? inputs[3] : default,
                element4 = inputs.Length > 4 ? inputs[4] : default
            };
        }

        private static void ValidateBrickRecipePriority(CraftingElement output, int id)
        {
            if (output.itemType != WorldItemType.brick) return;
            var properties = Brick.BrickProperties;
            if (!properties.IsCreated || Brick.BRICK_INDEX[output.id] is not Brick brick)
                throw new InvalidOperationException("The output brick has no live native recipe properties.");
            foreach (var related in brick.GetRelatedBricks())
            {
                if (related.brickID >= properties.Length)
                    throw new InvalidOperationException("An output shape has no live native recipe properties.");
                var existing = properties[related.brickID].schematicId;
                if (existing != 0 && id < existing)
                    throw new InvalidOperationException("The persisted recipe ID would take precedence over a stock brick recipe.");
            }
        }

        private static int CountInputs(CraftingSchematic row)
        {
            var count = 0;
            while (count < 5 && row[count].quantity > 0f) count++;
            return count;
        }

        private static void ValidateLiveIdentity(CraftingElement element, IEnumerable<CraftingElement> live)
        {
            if (ItemResources.IsRegistered(element)) return;
            if (element.itemType == WorldItemType.brick)
            {
                if (element.id > 1 && element.id < Brick.BRICK_INDEX.Length
                    && Brick.BRICK_INDEX[element.id] is Brick brick && brick.brickID == element.id)
                    return;
            }
            else if (live.Any(candidate => candidate.id == element.id && candidate.itemType == element.itemType
                && candidate.contextualDataId == element.contextualDataId && candidate.itemFlags == element.itemFlags
                && candidate.additionalData == element.additionalData))
                return;

            throw new InvalidOperationException("An ingredient or output has no compatible live native item identity.");
        }
    }
}
