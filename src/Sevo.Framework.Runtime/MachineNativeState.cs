using System;
using System.Reflection;
using Common;
using Game;
using HarmonyLib;
using Manager;
using Unity.Collections;
using VoxelEngine;
using VoxelEngine.Crafting;

namespace Sevo.Framework.Runtime
{
    // Only used after the machine contracts have been admitted, on the unlocked game thread.
    internal readonly struct MachineNativeState
    {
        internal MachineKind Kind { get; }
        internal int NativeIndex { get; }
        internal int CommandId => NativeIndex;
        internal int StorageId { get; }
        internal BrickCoord Placement { get; }
        internal int InputItemId { get; }
        internal int PendingOutputId { get; }
        internal float Progress { get; }
        internal bool IsIdle { get; }
        internal bool AutoInput { get; }
        internal bool PullInput { get; }
        internal bool PushOutput { get; }
        internal bool IsPoweredActive { get; }
        internal bool CanStart { get; }

        private MachineNativeState(MachineKind kind, int nativeIndex, int storageId, BrickCoord placement,
            int inputItemId, int pendingOutputId, float progress, bool idle, bool autoInput, bool pullInput, bool pushOutput,
            CommandStateData active, bool canStart)
        {
            Kind = kind;
            NativeIndex = nativeIndex;
            StorageId = storageId;
            Placement = placement;
            InputItemId = inputItemId;
            PendingOutputId = pendingOutputId;
            Progress = progress;
            IsIdle = idle;
            AutoInput = autoInput;
            PullInput = pullInput;
            PushOutput = pushOutput;
            IsPoweredActive = HasState(active) && active.IsTrue;
            var entityIndex = placement.entityID.indexID;
            var systems = EntitySystemResources.datas;
            // The native power job can clear a stall immediately before the machine job in the same graph.
            // Reserve the recipe even while stalled, but let the native signal gate any progress.
            CanStart = canStart && entityIndex > 0 && systems.IsCreated && entityIndex < systems.Length;
        }

        internal static bool TryRead(MachineKind kind, int nativeIndex, out MachineNativeState state)
        {
            state = default;
            if (kind == MachineKind.Crusher && TryCrusher(nativeIndex, out var crusher))
            {
                state = new MachineNativeState(kind, nativeIndex, crusher.StorageId, crusher.brickCoord,
                    crusher.inputItemId, crusher.outputItemId, crusher.completion,
                    crusher.outputItemId == 0 && CrusherFields.Process(ref crusher) == CrusherProcessType.none,
                    CrusherFields.AutoInput(ref crusher), CrusherFields.PullInput(ref crusher), CrusherFields.PushOutput(ref crusher),
                    crusher.isCrushing, crusher.State > 0.5f && crusher.drillBitItemId > 0);
                return true;
            }
            if (kind == MachineKind.Furnace && TryFurnace(nativeIndex, out var furnace))
            {
                state = new MachineNativeState(kind, nativeIndex, furnace.StorageId, furnace.brickCoord,
                    furnace.inputItemId, furnace.outputItemId, furnace.completion, furnace.outputItemId == 0,
                    FurnaceFields.AutoInput(ref furnace), FurnaceFields.PullInput(ref furnace), FurnaceFields.PushOutput(ref furnace),
                    furnace.isSmelting, furnace.State != 0f);
                return true;
            }
            return false;
        }

        // A false return leaves the native command unchanged.
        internal bool Stage(ItemInstanceData input, int inputCount, int pendingOutputId)
        {
            if (!IsIdle || !CanStart || input.id <= 0 || inputCount <= 0 || !float.IsFinite(input.quantity)
                || input.quantity < inputCount || pendingOutputId <= 0 || pendingOutputId == input.id
                || input.nextElementId != 0 || input.HasLiveReleaseComponents()
                || (InputItemId != 0 && InputItemId != input.id))
                return false;

            if (Kind == MachineKind.Crusher && TryCrusher(NativeIndex, out var crusher)
                && Matches(crusher.StorageId, crusher.brickCoord, crusher.inputItemId, crusher.outputItemId)
                && CrusherFields.Process(ref crusher) == CrusherProcessType.none)
            {
                crusher.inputItemId = input.id;
                crusher.inputItemIdForCraft = input.id;
                crusher.outputItemId = pendingOutputId;
                crusher.isDirtyForUI = true;
                CrusherFields.InputSnapshot(ref crusher) = input;
                CrusherFields.InputQuantity(ref crusher) = inputCount;
                CrusherFields.InputSchematicId(ref crusher) = 0;
                CrusherFields.Completion(ref crusher) = 0f;
                CrusherFields.Speed(ref crusher) = 0f;
                // The single-output state runs the regular drill and power path, including drill wear at release.
                CrusherFields.Process(ref crusher) = CrusherProcessType.stoneOreBits;
                crusher.PushItself();
                return true;
            }
            if (Kind == MachineKind.Furnace && TryFurnace(NativeIndex, out var furnace)
                && Matches(furnace.StorageId, furnace.brickCoord, furnace.inputItemId, furnace.outputItemId))
            {
                furnace.inputItemId = input.id;
                furnace.inputItemIdForCraft = input.id;
                furnace.outputItemId = pendingOutputId;
                furnace.isDirty = true;
                FurnaceFields.InputSnapshot(ref furnace) = input;
                FurnaceFields.Completion(ref furnace) = 0f;
                FurnaceFields.Speed(ref furnace) = 0f;
                // The native release routine reads only this per-command quantity. It is never registered.
                FurnaceFields.Schematic(ref furnace) = new CraftingSchematic
                {
                    element0 = new CraftingElement { quantity = inputCount }
                };
                furnace.PushItself();
                return true;
            }
            return false;
        }

        internal bool SetProgress(float completion)
        {
            if (!float.IsFinite(completion))
                return false;
            completion = Math.Clamp(completion, 0f, 1f);
            if (TryStagedCrusher(out var crusher))
            {
                CrusherFields.Completion(ref crusher) = completion;
                crusher.PushItself();
                return true;
            }
            if (TryStagedFurnace(out var furnace))
            {
                FurnaceFields.Completion(ref furnace) = completion;
                furnace.PushItself();
                return true;
            }
            return false;
        }

        // A false return is preflight rejection. Exceptions from stock completion are surfaced because the native
        // item operation may already have started and a caller must not treat it as a reversible rejection.
        internal bool Finish(StorageContext context)
        {
            if (TryStagedCrusher(out var crusher) && HasState(crusher.onOreCrushed)
                && CanWearDrillBit(crusher.drillBitItemId, context)
                && CanCommit(context, CrusherFields.InputSnapshot(ref crusher), CrusherFields.InputQuantity(ref crusher)))
            {
                var commands = new NativeList<JobCommand>(Allocator.Temp);
                var job = new SystemsDataResources.UpdateJob
                {
                    context = context, stateDatas = CommandStateData.stateDatas, jobCommandsCrusher = commands
                };
                CrusherFields.Completion(ref crusher) = 1f;
                object boxed = crusher;
                try
                {
                    CrusherFields.Release.Invoke(boxed, new object[] { job });
                }
                catch
                {
                    commands.Dispose();
                    throw;
                }
                finally
                {
                    ((CrusherCommandData)boxed).PushItself();
                }
                CrusherCommandData.DisposeJobs(commands);
                return true;
            }
            if (TryStagedFurnace(out var furnace) && HasState(furnace.onIngotSmelted)
                && CanCommit(context, FurnaceFields.InputSnapshot(ref furnace), FurnaceFields.Schematic(ref furnace).element0.quantity))
            {
                var commands = new NativeList<JobCommand>(Allocator.Temp);
                var job = new SystemsDataResources.UpdateJob
                {
                    context = context, stateDatas = CommandStateData.stateDatas, jobCommandsFurnace = commands
                };
                FurnaceFields.Completion(ref furnace) = 1f;
                object boxed = furnace;
                try
                {
                    FurnaceFields.Release.Invoke(boxed, new object[] { job });
                }
                catch
                {
                    commands.Dispose();
                    throw;
                }
                finally
                {
                    ((FurnaceCommandData)boxed).PushItself();
                }
                FurnaceCommandData.DisposeJobs(commands);
                return true;
            }
            return false;
        }

        private bool CanWearDrillBit(int id, StorageContext context)
        {
            if (id <= 0 || id >= context.ItemLength || id == InputItemId || id == PendingOutputId) return false;
            var bit = context.GetItemData(id);
            if (bit.id != id || !CrusherCommandData.IsItemValidDrillBit(bit) || bit.nextElementId != 0
                || bit.HasLiveReleaseComponents() || !ItemInstanceCommon.IsValidStackQuantity(bit.quantity)
                || bit.quantity < 1f || !float.IsFinite(bit.damage) || bit.damage < 0f
                || !context.prefabDatas.IsCreated || bit.prefabId < 0 || bit.prefabId >= context.prefabDatas.Length)
                return false;
            var health = context.prefabDatas[bit.prefabId].healthPoint;
            return float.IsFinite(health) && health > 0f;
        }

        internal bool Cancel(StorageContext context)
        {
            if (PendingOutputId <= 0 || PendingOutputId >= context.ItemLength
                || !IsDetachedOutput(context.GetItemData(PendingOutputId)))
                return false;

            var job = new SystemsDataResources.UpdateJob { context = context };
            if (TryStagedCrusher(out var crusher, requireCurrentInput: false))
            {
                object boxed = crusher;
                CrusherFields.Cancel.Invoke(boxed, new object[] { job });
                ((CrusherCommandData)boxed).PushItself();
                return true;
            }
            if (TryStagedFurnace(out var furnace, requireCurrentInput: false))
            {
                object boxed = furnace;
                FurnaceFields.Cancel.Invoke(boxed, new object[] { job });
                var canceled = (FurnaceCommandData)boxed;
                FurnaceFields.Schematic(ref canceled) = default;
                canceled.PushItself();
                return true;
            }
            return false;
        }

        // Disown a stale output ID without following or releasing it. A replacement slot item remains in place.
        internal bool Abandon()
        {
            if (TryStagedCrusher(out var crusher, requireCurrentInput: false))
            {
                crusher.outputItemId = 0;
                crusher.inputItemIdForCraft = 0;
                crusher.isDirtyForUI = true;
                CrusherFields.Process(ref crusher) = CrusherProcessType.none;
                CrusherFields.InputQuantity(ref crusher) = 0;
                CrusherFields.InputSchematicId(ref crusher) = 0;
                CrusherFields.Completion(ref crusher) = 0f;
                crusher.PushItself();
                return true;
            }
            if (TryStagedFurnace(out var furnace, requireCurrentInput: false))
            {
                furnace.outputItemId = 0;
                furnace.inputItemIdForCraft = 0;
                furnace.isDirty = true;
                FurnaceFields.Schematic(ref furnace) = default;
                FurnaceFields.Completion(ref furnace) = 0f;
                furnace.PushItself();
                return true;
            }
            return false;
        }

        private bool CanCommit(StorageContext context, ItemInstanceData reserved, float count)
        {
            if (!float.IsFinite(count) || count <= 0f || reserved.id != InputItemId || InputItemId <= 0
                || InputItemId >= context.ItemLength || PendingOutputId <= 0 || PendingOutputId >= context.ItemLength
                || StorageId <= 0 || StorageId >= context.StorageLength)
                return false;

            var input = context.GetItemData(InputItemId);
            var output = context.GetItemData(PendingOutputId);
            if (input.id != InputItemId || input.itemType != reserved.itemType || input.prefabId != reserved.prefabId
                || input.contextualDataId != reserved.contextualDataId || input.itemFlags != reserved.itemFlags
                || !float.IsFinite(input.quantity) || input.quantity < count || input.nextElementId != 0
                || input.HasLiveReleaseComponents() || !IsDetachedOutput(output))
                return false;

            // Stock inserts the output before consuming the input. Validate the chain up front so that insertion
            // cannot reject a bad link or silently reinsert an item which already belongs to this inventory.
            var storage = context.GetStorageData(StorageId);
            var expectedType = Kind == MachineKind.Crusher ? ItemStorageType.crusher : ItemStorageType.furnace;
            if (storage.id != StorageId || storage.type != expectedType || storage.brickInstanceId != Placement.brickData.instanceId)
                return false;
            int remaining = context.ItemLength;
            for (int id = storage.rootItemId; id != 0;)
            {
                if (id < 0 || id >= context.ItemLength || id == InputItemId || id == PendingOutputId || --remaining < 0)
                    return false;
                var stored = context.GetItemData(id);
                if (stored.id != id)
                    return false;
                id = stored.nextElementId;
            }
            return true;
        }

        private bool IsDetachedOutput(ItemInstanceData item) =>
            item.id == PendingOutputId && item.nextElementId == 0 && !item.HasLiveReleaseComponents()
            && float.IsFinite(item.quantity) && item.quantity > 0f;

        private static bool HasState(CommandStateData state)
        {
            var signals = CommandStateData.stateDatas;
            return signals.IsCreated && state.id > 0 && state.id < signals.Length;
        }

        private bool TryStagedCrusher(out CrusherCommandData crusher, bool requireCurrentInput = true)
        {
            crusher = default;
            return Kind == MachineKind.Crusher && PendingOutputId > 0 && TryCrusher(NativeIndex, out crusher)
                && Matches(crusher.StorageId, crusher.brickCoord, crusher.inputItemId, crusher.outputItemId, requireCurrentInput)
                && (!requireCurrentInput || crusher.inputItemIdForCraft == InputItemId)
                && CrusherFields.Speed(ref crusher) == 0f && CrusherFields.Process(ref crusher) == CrusherProcessType.stoneOreBits;
        }

        private bool TryStagedFurnace(out FurnaceCommandData furnace, bool requireCurrentInput = true)
        {
            furnace = default;
            return Kind == MachineKind.Furnace && PendingOutputId > 0 && TryFurnace(NativeIndex, out furnace)
                && Matches(furnace.StorageId, furnace.brickCoord, furnace.inputItemId, furnace.outputItemId, requireCurrentInput)
                && (!requireCurrentInput || furnace.inputItemIdForCraft == InputItemId)
                && FurnaceFields.Speed(ref furnace) == 0f;
        }

        private bool Matches(int storageId, BrickCoord placement, int input, int output, bool requireCurrentInput = true) =>
            storageId == StorageId && placement.entityID == Placement.entityID
            && placement.brickData.instanceId == Placement.brickData.instanceId && output == PendingOutputId
            && (!requireCurrentInput || input == InputItemId);

        private static bool TryCrusher(int index, out CrusherCommandData value)
        {
            var data = CrusherCommandData.datas;
            value = data.IsCreated && index > 0 && index < data.Length ? data[index] : default;
            return value.CommandId == index && value.StorageId > 0 && value.brickCoord.IsValid;
        }

        private static bool TryFurnace(int index, out FurnaceCommandData value)
        {
            var data = FurnaceCommandData.datas;
            value = data.IsCreated && index > 0 && index < data.Length ? data[index] : default;
            return value.CommandId == index && value.StorageId > 0 && value.brickCoord.IsValid && value.IsArcFurnace;
        }

        private static MethodInfo NativeAction<T>(string name) =>
            typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(SystemsDataResources.UpdateJob) }, null)
            ?? throw new MissingMethodException(typeof(T).FullName, name);

        private static class CrusherFields
        {
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, float> Speed = AccessTools.StructFieldRefAccess<CrusherCommandData, float>("speed");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, float> Completion = AccessTools.StructFieldRefAccess<CrusherCommandData, float>("<completion>k__BackingField");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, CrusherProcessType> Process = AccessTools.StructFieldRefAccess<CrusherCommandData, CrusherProcessType>("processType");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, int> InputQuantity = AccessTools.StructFieldRefAccess<CrusherCommandData, int>("inputQuantityForCraft");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, int> InputSchematicId = AccessTools.StructFieldRefAccess<CrusherCommandData, int>("inputSchematicId");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, ItemInstanceData> InputSnapshot = AccessTools.StructFieldRefAccess<CrusherCommandData, ItemInstanceData>("inputItemData");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, bool> AutoInput = AccessTools.StructFieldRefAccess<CrusherCommandData, bool>("autoCraftInventory");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, bool> PullInput = AccessTools.StructFieldRefAccess<CrusherCommandData, bool>("pullComponents");
            internal static readonly AccessTools.StructFieldRef<CrusherCommandData, bool> PushOutput = AccessTools.StructFieldRefAccess<CrusherCommandData, bool>("pushOutput");
            internal static readonly MethodInfo Release = NativeAction<CrusherCommandData>("ReleaseInputAndOutput");
            internal static readonly MethodInfo Cancel = NativeAction<CrusherCommandData>("CancelCraft");
        }

        private static class FurnaceFields
        {
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, float> Speed = AccessTools.StructFieldRefAccess<FurnaceCommandData, float>("speed");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, float> Completion = AccessTools.StructFieldRefAccess<FurnaceCommandData, float>("<completion>k__BackingField");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, CraftingSchematic> Schematic = AccessTools.StructFieldRefAccess<FurnaceCommandData, CraftingSchematic>("schematic");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, ItemInstanceData> InputSnapshot = AccessTools.StructFieldRefAccess<FurnaceCommandData, ItemInstanceData>("inputItemData");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, bool> AutoInput = AccessTools.StructFieldRefAccess<FurnaceCommandData, bool>("autoCraftInventory");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, bool> PullInput = AccessTools.StructFieldRefAccess<FurnaceCommandData, bool>("pullComponents");
            internal static readonly AccessTools.StructFieldRef<FurnaceCommandData, bool> PushOutput = AccessTools.StructFieldRefAccess<FurnaceCommandData, bool>("pushOutput");
            internal static readonly MethodInfo Release = NativeAction<FurnaceCommandData>("ReleaseInputAndOutput");
            internal static readonly MethodInfo Cancel = NativeAction<FurnaceCommandData>("CancelCraft");
        }
    }
}
