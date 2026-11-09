using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using HarmonyLib;
using Manager;
using VoxelEngine;
using VoxelEngine.Crafting;

namespace Sevo.Framework.Runtime
{
    internal static partial class MachineRecipeResources
    {
        private const int Operation = 7618;
        private static readonly FieldInfo SystemsJob = AccessTools.DeclaredField(typeof(SystemsDataResources), "job");
        private static readonly Dictionary<(MachineKind Kind, int Index), Work> Active =
            new Dictionary<(MachineKind, int), Work>();
        private static KeyValuePair<string, Func<MachineRecipeDefinition>>[]? declarations;
        private static MachineRecipeDefinition[][]? recipes;
        private static Action<string>? warning;
        private static bool enabled;
        private static bool failed;
        private static float completedDelta;
        private static uint pendingSeed;

        internal static bool IsAvailable => enabled && !failed && recipes?.Any(list => list.Length != 0) == true;

        internal static void Install(Harmony harmony, Action<string> log)
        {
            void Patch(Type type, string method, string hook, bool prefix = false)
            {
                var target = AccessTools.DeclaredMethod(type, method)
                    ?? throw new InvalidOperationException("Missing machine recipe target: " + type.FullName + "." + method);
                var patch = new HarmonyMethod(typeof(MachineRecipeResources), hook);
                harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
            }
            Patch(typeof(GameloopManager), "ScheduleJobs", nameof(BeforeSchedule), prefix: true);
            Patch(typeof(SystemsDataResources), nameof(SystemsDataResources.DisposeJobCallback), nameof(CaptureCompletedTick), prefix: true);
            Patch(typeof(GameloopManager), "ExecuteCommandsJob", nameof(AfterCommands));
            Patch(typeof(CrusherCommandData), nameof(CrusherCommandData.IsItemValidInput), nameof(AllowCrusherInput));
            Patch(typeof(FurnaceCommandData), nameof(FurnaceCommandData.IsItemValidInput), nameof(AllowFurnaceInput));
            Patch(typeof(CrusherCommandData), nameof(CrusherCommandData.OnGameReload), nameof(ResetCycle));
            warning = log;
            enabled = true;
        }

        internal static void Disable()
        {
            enabled = false;
            if (GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
                && (Active.Count != 0 || OwnedRoutes.Count != 0))
                StopOwnedWork(StorageCommon.CONTEXT);
        }

        private static void ResetCycle()
        {
            MachineWorkRateModifier.ResetWorld();
            recipes = null;
            completedDelta = 0f;
            Active.Clear();
            OwnedRoutes.Clear();
        }

        private static bool InSafeWorld() => enabled && GameloopManager.IsMainThread
            && !GameloopManager.IsNativeContainerAccessUnsafe && !PlayerState.InLoadingScreen
            && !PlayerState.IsQuittingToDesktopOrTitle && PlayerState.sessionMode == PlayerState.SessionMode.solo;

        private static void PrepareRecipes(StorageContext context)
        {
            if (recipes != null) return;
            declarations ??= MachineRecipeRegistration.Seal();
            var lists = new[] { new List<MachineRecipeDefinition>(), new List<MachineRecipeDefinition>() };
            foreach (var declaration in declarations)
            {
                try
                {
                    var recipe = declaration.Value() ?? throw new InvalidOperationException("Factory returned no definition.");
                    ValidateIdentity(recipe.Input, context);
                    ValidateIdentity(recipe.Output, context);
                    var list = lists[(int)recipe.Machine];
                    if (list.Any(value => MachineRecipeDefinition.Matches(value.Input, recipe.Input.ToItemData)))
                        throw new InvalidOperationException("Another recipe already claims this exact input on the selected machine.");
                    list.Add(recipe);
                    warning?.Invoke("Keel registered machine recipe '" + declaration.Key + "'.");
                }
                catch (Exception exception)
                {
                    warning?.Invoke("Keel machine recipe '" + declaration.Key + "' skipped: " + exception.Message);
                }
            }
            recipes = lists.Select(list => list.ToArray()).ToArray();
        }

        private static void ValidateIdentity(CraftingElement element, StorageContext context)
        {
            var item = element.ToItemData;
            if (!context.prefabDatas.IsCreated || element.id >= context.prefabDatas.Length)
                throw new InvalidOperationException("Item prefab is outside the live native table.");
            var prefab = context.prefabDatas[element.id];
            bool known = ItemResources.IsRegistered(element)
                || (element.contextualDataId == 0 && prefab.itemType == element.itemType);
            if (!known && CraftingSchematic.Index != null)
                foreach (var stock in CraftingSchematic.Index)
                {
                    if (!stock.IsValid) continue;
                    known = IsStockIdentity(stock.output, item, prefab);
                    for (int i = 0; !known && i < stock.InputsLength; i++)
                        known = IsStockIdentity(stock[i], item, prefab);
                    if (known) break;
                }
            var volume = item.GetVolumeInLiter(context);
            if (!known || !prefab.renderData.IsValid || !item.CanBePickedUp(context)
                || volume <= 0f || !float.IsFinite(volume))
                throw new InvalidOperationException("Recipe requires an exact live, storable stock or Keel item.");
        }

        private static bool IsStockIdentity(CraftingElement stock, ItemInstanceData item, PrefabData prefab)
        {
            if (!MachineRecipeDefinition.Matches(stock, item)) return false;
            if (stock.id == item.prefabId) return true;
            // Ore carrier variants merge in stock storage. Match their material and require a real carrier prefab.
            return MachineRecipeDefinition.UsesMaterialContext(item.itemType)
                && (prefab.itemType == item.itemType || (item.itemType != WorldItemType.ingot
                    && prefab.itemType is WorldItemType.rockSmall or WorldItemType.ore or WorldItemType.ore_small
                        or WorldItemType.stoneOreBits));
        }

        private static void AllowCrusherInput(ItemInstanceData __0, ref bool __result)
        {
            if (!__result && IsAvailable) __result = FindRecipe(recipes![(int)MachineKind.Crusher], __0) != null;
        }

        private static void AllowFurnaceInput(ref FurnaceCommandData __instance, ItemInstanceData __0, ref bool __result)
        {
            if (!__result && IsAvailable && !__instance.isCrucible)
                __result = FindRecipe(recipes![(int)MachineKind.Furnace], __0) != null;
        }

        private static MachineRecipeDefinition? FindRecipe(MachineRecipeDefinition[] list, ItemInstanceData item)
        {
            if (!ItemInstanceCommon.IsValidStackQuantity(item.quantity) || item.quantity <= 0f)
                return null;
            foreach (var recipe in list)
                if (MachineRecipeDefinition.Matches(recipe.Input, item)) return recipe;
            return null;
        }

        private static void BeforeSchedule()
        {
            completedDelta = 0f;
            if (!InSafeWorld()) return;
            try
            {
                var context = StorageCommon.CONTEXT;
                if (failed)
                {
                    StopOwnedWork(context);
                    return;
                }
                PrepareRecipes(context);
                if (!IsAvailable) return;
                ProcessKind(MachineKind.Crusher, CrusherCommandData.datas.Length, context);
                ProcessKind(MachineKind.Furnace, FurnaceCommandData.datas.Length, context);
                PruneRoutes(context);
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void CaptureCompletedTick()
        {
            if (!enabled || Active.Count == 0) return;
            try
            {
                var job = (SystemsDataResources.UpdateJob)SystemsJob.GetValue(null)!;
                if (job.reactorDatas.IsCreated && float.IsFinite(job.deltaTime) && job.deltaTime > 0f)
                    completedDelta = job.deltaTime;
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void AfterCommands()
        {
            var delta = completedDelta;
            completedDelta = 0f;
            if (!InSafeWorld() || Active.Count == 0) return;
            try
            {
                var context = StorageCommon.CONTEXT;
                if (failed) StopOwnedWork(context);
                else if (delta > 0f)
                    foreach (var key in Active.Keys.ToArray())
                    {
                        var work = Active[key];
                        if (!MachineNativeState.TryRead(key.Kind, key.Index, out var machine)
                            || !work.Owns(machine) || machine.PendingOutputId != work.Output.id)
                        {
                            Active.Remove(key);
                            continue;
                        }
                        if (!ValidateWork(machine, work, context))
                        {
                            Abort(machine, work, context);
                            Active.Remove(key);
                            continue;
                        }
                        var rate = machine.IsPoweredActive ? MachineWorkRateModifier.GetRate(machine) : 1f;
                        var step = work.Progress.Advance(machine.IsPoweredActive, delta, work.Recipe.DurationSeconds, rate);
                        if (step == MachineWorkStep.None) continue;
                        if (step == MachineWorkStep.Progressed)
                        {
                            if (!machine.SetProgress(work.Progress.Value))
                                throw new InvalidOperationException("Could not update native machine progress.");
                            continue;
                        }
                        if (!machine.Finish(context))
                            Abort(machine, work, context);
                        Active.Remove(key);
                    }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void ProcessKind(MachineKind kind, int length, StorageContext context)
        {
            var list = recipes![(int)kind];
            if (list.Length == 0) return;
            for (int index = 1; index < length; index++)
            {
                if (!MachineNativeState.TryRead(kind, index, out var machine)) continue;
                var key = (kind, index);
                if (Active.TryGetValue(key, out var work))
                {
                    if (work.Owns(machine) && machine.PendingOutputId == work.Output.id)
                    {
                        if (ValidateWork(machine, work, context)) continue;
                        Abort(machine, work, context);
                        Active.Remove(key);
                        continue;
                    }
                    Active.Remove(key);
                }
                if (!TryReadStorage(machine, context, out var storage)) continue;
                if (machine.IsIdle && machine.CanStart && TryStage(machine, list, storage, context))
                    ReconcileRoutes(machine, Array.Empty<ItemRequestData>(), context);
                else
                    ReconcileRoutes(machine, BuildRoutes(machine, list, storage, context), context);
            }
        }

        private static bool TryStage(MachineNativeState machine, MachineRecipeDefinition[] list,
            StorageContainerData storage, StorageContext context)
        {
            bool automatic = machine.InputItemId == 0;
            if (automatic && !machine.AutoInput) return false;
            if (!ReadInventory(storage, context, out var items)) return false;
            var input = automatic ? default : ReadItem(machine.InputItemId, context);
            var recipe = automatic ? null : FindRecipe(list, input);
            if (automatic)
                foreach (var candidate in items)
                {
                    var matching = FindRecipe(list, candidate);
                    if (matching == null || candidate.quantity < matching.Input.quantity
                        || !CanFit(storage, matching.Output.ToItemData, context,
                            matching.Input.quantity * candidate.GetUnitaryVolumeInLiter(context)))
                        continue;
                    input = candidate;
                    recipe = matching;
                    break;
                }
            if (recipe == null || input.quantity < recipe.Input.quantity || input.HasLiveReleaseComponents()) return false;
            var output = recipe.Output.ToItemData;
            if (!automatic && (!CanFit(storage, output, context) || items.Any(item => item.id == input.id))) return false;
            output.seed = unchecked(++pendingSeed ^ 0x6b65656cU);
            output.RegisterAndPushItself(context, checkIfAlreadyRegistered: false);
            try
            {
                if (automatic)
                    input = StorageCommon.PopupItemQuantity(storage.id, input.id, recipe.Input.quantity, context);
                if (input.id <= 0 || !machine.Stage(input, (int)recipe.Input.quantity, output.id))
                    throw new InvalidOperationException("Could not reserve the native machine input.");
                Active.Add((machine.Kind, machine.NativeIndex), new Work(machine, recipe, input, output));
            }
            catch
            {
                if (MachineNativeState.TryRead(machine.Kind, machine.NativeIndex, out var live)
                    && live.PendingOutputId == output.id)
                    live.Cancel(context);
                else
                {
                    context.ReleaseItemId(output.id, Operation);
                    if (automatic && input.id != 0 && input.nextElementId == 0
                        && !StorageCommon.ContainItem(storage.id, input.id, context))
                        StorageCommon.InsertItem(storage.id, input.id, context, Operation,
                            StorageInsertCapacityPolicy.ignoreCapacity, StorageInsertMergePolicy.dontTryMerge);
                }
                throw;
            }
            return true;
        }

        private static bool ValidateWork(MachineNativeState machine, Work work, StorageContext context)
        {
            if (!TryReadStorage(machine, context, out var storage) || !ReadInventory(storage, context, out var items))
                return false;
            var input = ReadItem(machine.InputItemId, context);
            var output = ReadItem(machine.PendingOutputId, context);
            return input.id == work.Input.id && input.prefabId == work.Input.prefabId && input.seed == work.Input.seed
                && !input.HasLiveReleaseComponents()
                && input.nextElementId == 0 && MachineRecipeDefinition.Matches(work.Recipe.Input, input)
                && ItemInstanceCommon.IsValidStackQuantity(input.quantity) && input.quantity >= work.Recipe.Input.quantity
                && OwnsOutput(work, output) && !items.Any(item => item.id == input.id || item.id == output.id)
                && CanFit(storage, output, context);
        }

        private static bool TryReadStorage(MachineNativeState machine, StorageContext context, out StorageContainerData storage)
        {
            storage = default;
            if (machine.StorageId <= 0 || machine.StorageId >= context.StorageLength) return false;
            storage = context.GetStorageData(machine.StorageId);
            var type = machine.Kind == MachineKind.Crusher ? ItemStorageType.crusher : ItemStorageType.furnace;
            return storage.id == machine.StorageId && storage.type == type
                && storage.brickInstanceId == machine.Placement.brickData.instanceId
                && float.IsFinite(storage.capacityAvailable);
        }

        private static bool ReadInventory(StorageContainerData storage, StorageContext context, out List<ItemInstanceData> items)
        {
            items = new List<ItemInstanceData>();
            var seen = new HashSet<int>();
            int id = storage.rootItemId;
            while (id != 0)
            {
                var item = ReadItem(id, context);
                if (item.id != id || !seen.Add(id) || !ItemInstanceCommon.IsValidStackQuantity(item.quantity)) return false;
                items.Add(item);
                id = item.nextElementId;
            }
            return true;
        }

        private static bool CanFit(StorageContainerData storage, ItemInstanceData output, StorageContext context, float freed = 0f)
        {
            var volume = output.GetVolumeInLiter(context);
            return float.IsFinite(volume) && volume > 0f && volume <= storage.capacityAvailable + freed;
        }

        private static ItemInstanceData ReadItem(int id, StorageContext context) =>
            id > 0 && id < context.ItemLength ? context.GetItemData(id) : default;

        private static bool OwnsOutput(Work work, ItemInstanceData output) =>
            output.id == work.Output.id && output.prefabId == work.Output.prefabId
            && output.seed == work.Output.seed && output.quantity == work.Output.quantity
            && !output.HasLiveReleaseComponents() && output.nextElementId == 0
            && MachineRecipeDefinition.Matches(work.Recipe.Output, output);

        private static void Abort(MachineNativeState machine, Work work, StorageContext context)
        {
            var output = ReadItem(machine.PendingOutputId, context);
            // The tail of a storage chain also has nextElementId == 0. Do not release a linked item.
            if (!OwnsOutput(work, output) || !TryReadStorage(machine, context, out var storage)
                || !ReadInventory(storage, context, out var items) || items.Any(item => item.id == output.id)
                || !machine.Cancel(context))
                machine.Abandon();
        }

        private static void StopOwnedWork(StorageContext context)
        {
            foreach (var pair in Active)
                if (MachineNativeState.TryRead(pair.Key.Kind, pair.Key.Index, out var machine)
                    && pair.Value.Owns(machine) && machine.PendingOutputId == pair.Value.Output.id)
                    Abort(machine, pair.Value, context);
            Active.Clear();
            ClearOwnedRoutes(context);
        }

        private static void Fail(Exception exception)
        {
            failed = true;
            warning?.Invoke("Machine recipes disabled: " + exception);
        }

        private sealed class Work
        {
            internal readonly MachineRecipeDefinition Recipe;
            internal readonly ItemInstanceData Input;
            internal readonly ItemInstanceData Output;
            private readonly int command;
            private readonly int storage;
            private readonly BrickCoord placement;
            internal MachineWorkProgress Progress;

            internal Work(MachineNativeState machine, MachineRecipeDefinition recipe, ItemInstanceData input, ItemInstanceData output)
            {
                Recipe = recipe;
                Input = input;
                Output = output;
                command = machine.CommandId;
                storage = machine.StorageId;
                placement = machine.Placement;
            }

            internal bool Owns(MachineNativeState machine) =>
                machine.CommandId == command && machine.StorageId == storage && machine.Placement.Equals(placement);
        }
    }
}
