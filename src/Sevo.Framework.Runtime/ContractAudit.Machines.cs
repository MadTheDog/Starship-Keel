using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string MachineLoop = "Manager.GameloopManager";
        private const string MachineSystems = "VoxelEngine.SystemsDataResources";
        private const string MachineJob = MachineSystems + "/UpdateJob";
        private const string MachineFurnace = "VoxelEngine.Crafting.FurnaceCommandData";
        private const string MachineCrusher = "VoxelEngine.Crafting.CrusherCommandData";
        private const string MachineContext = "VoxelEngine.StorageContext";
        private const string MachineStorage = "VoxelEngine.StorageCommon";
        private const string MachineItem = "Game.ItemInstanceData";
        private const string MachineItemKind = "Game.WorldItemType";
        private const string MachineRequest = "VoxelEngine.ItemRequestData";
        private const string MachineState = "VoxelEngine.CommandStateData";
        private const string MachineStateArray = "Unity.Collections.NativeArray`1<" + MachineState + "/StateData>";
        private const string MachineCommands = "Unity.Collections.NativeList`1<Common.JobCommand>";
        private const string MachineInt = "System.Int32";
        private const string MachineFloat = "System.Single";
        private const string MachineBool = "System.Boolean";
        private const string MachineVoid = "System.Void";

        public static CapabilityResult EvaluateMachineRecipes(ModuleDefinition module)
        {
            const string capability = "machineRecipes";
            try
            {
                AuditMachineBoundaries(module);
                AuditMachineCommand(module, MachineFurnace, "UI.UI_Furnace", "CanDragItem", "Furnace", isFurnace: true);
                AuditMachineCommand(module, MachineCrusher, "UI.UI_Crusher", "CanDragInputItem", "Crusher", isFurnace: false);
                AuditMachineRequests(module);
                return new CapabilityResult(capability, true, "Native schedule boundary, managed completion ceiling, storage, UI and input persistence validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static MethodDefinition RequireMachineMethod(ModuleDefinition module, string type, string name, bool isStatic,
            string result, params string[] parameters)
            => Method(module, type, name, isStatic, result, parameters) is { HasBody: true } method
                ? method : throw new InvalidOperationException("Missing native machine method: " + type + "." + name);

        private static void RequireMachineField(ModuleDefinition module, string type, string name, string valueType, bool isStatic = false)
        {
            if (module.GetType(type)?.Fields.Any(field => field.Name == name && field.IsStatic == isStatic
                    && field.FieldType.FullName == valueType) != true)
                throw new InvalidOperationException("Missing native machine field: " + type + "." + name);
        }

        private static Instruction[] MachineBody(MethodDefinition method)
            => method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();

        private static bool MachineFieldAccess(Instruction instruction, string type, string name, Code opcode)
            => instruction.OpCode.Code == opcode && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == type && field.Name == name;

        private static bool MachineCall(Instruction instruction, MethodDefinition target)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference method
                && method.FullName == target.FullName;

        private static int MachineCallIndex(Instruction[] body, MethodDefinition target)
        {
            var matches = body.Select((instruction, index) => (instruction, index))
                .Where(value => MachineCall(value.instruction, target)).Select(value => value.index).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Missing or ambiguous native machine call: " + target.FullName);
            return matches[0];
        }

        private static void AuditMachineBoundaries(ModuleDefinition module)
        {
            var before = RequireMachineMethod(module, MachineLoop, "RunLateUpdateAfterMirror", false, MachineVoid);
            var schedule = RequireMachineMethod(module, MachineLoop, "ScheduleJobs", true, MachineVoid);
            var containerLock = RequireMachineMethod(module, MachineLoop, "SetContainerJobLock", true, MachineVoid, MachineBool);
            var late = MachineBody(before);
            var scheduleIndex = MachineCallIndex(late, schedule);
            var lockIndex = MachineCallIndex(late, containerLock);
            if (scheduleIndex + 2 != lockIndex || Constant(late[lockIndex - 1]) != 1)
                throw new InvalidOperationException("Machine preparation must precede job scheduling and the container lock.");
            var autoSchedule = RequireMachineMethod(module, "Manager.JobGraph", "AutoSchedule", false, MachineVoid, MachineInt);
            MachineCallIndex(MachineBody(schedule), autoSchedule);

            var rendering = MachineBody(RequireMachineMethod(module, MachineLoop, "OnAfterRendering", true, MachineVoid));
            var complete = RequireMachineMethod(module, MachineLoop, "CompleteJobs", true, MachineVoid);
            var disposal = RequireMachineMethod(module, MachineLoop, "DisposeJobs", true, MachineVoid);
            var execute = RequireMachineMethod(module, MachineLoop, "ExecuteCommandsJob", true, MachineVoid);
            if (MachineCallIndex(rendering, complete) >= MachineCallIndex(rendering, disposal)
                || MachineCallIndex(rendering, disposal) >= MachineCallIndex(rendering, execute))
                throw new InvalidOperationException("Machine commits must follow native job completion and disposal.");
            var wait = RequireMachineMethod(module, "Manager.JobGraph", "CompleteScheduledNodes", false, MachineVoid, MachineInt);
            var completeBody = MachineBody(complete);
            var unlockIndex = MachineCallIndex(completeBody, containerLock);
            if (MachineCallIndex(completeBody, wait) >= unlockIndex || Constant(completeBody[unlockIndex - 1]) != 0)
                throw new InvalidOperationException("Native storage must unlock after completing scheduled jobs.");
            RequireMachineMethod(module, MachineLoop, "get_IsMainThread", true, MachineBool);
            RequireMachineMethod(module, MachineLoop, "get_IsNativeContainerAccessUnsafe", true, MachineBool);
            RequireMachineMethod(module, MachineStorage, "get_CONTEXT", true, MachineContext);
            RequireMachineMethod(module, MachineStorage, "CanInsertItem", true, MachineBool, MachineInt, MachineInt, MachineContext);
            RequireMachineField(module, MachineJob, "context", MachineContext);
            RequireMachineField(module, MachineJob, "stateDatas", MachineStateArray);
            RequireMachineField(module, MachineState, "stateDatas", "Common.NativeRecyclableContainer`1<" + MachineState + "/StateData>", isStatic: true);
            RequireMachineField(module, MachineState, "id", MachineInt);
            RequireMachineMethod(module, MachineState, "get_IsTrue", false, MachineBool);
            foreach (var field in new[] { "id", "prefabId", "contextualDataId", "nextElementId" })
                RequireMachineField(module, MachineItem, field, MachineInt);
            RequireMachineField(module, MachineItem, "quantity", MachineFloat);
            RequireMachineField(module, MachineItem, "itemType", MachineItemKind);
            RequireMachineField(module, MachineItem, "itemFlags", "Game.ItemInstanceFlags");
            RequireMachineMethod(module, MachineItem, "HasLiveReleaseComponents", false, MachineBool);
            RequireMachineMethod(module, MachineContext, "get_ItemLength", false, MachineInt);
            RequireMachineMethod(module, MachineContext, "get_StorageLength", false, MachineInt);
            RequireMachineMethod(module, MachineContext, "GetItemData", false, MachineItem, MachineInt);
            RequireMachineMethod(module, MachineContext, "GetStorageData", false, "VoxelEngine.StorageContainerData", MachineInt);
            foreach (var field in new[] { "commandId", "brickInstanceId" })
                RequireMachineField(module, "VoxelEngine.StorageContainerData", field, MachineInt);
            RequireMachineField(module, "VoxelEngine.StorageContainerData", "type", "VoxelEngine.ItemStorageType");
            RequireMachineMethod(module, "VoxelEngine.StorageContainerData", "get_id", false, MachineInt);
            RequireMachineMethod(module, "VoxelEngine.StorageContainerData", "get_rootItemId", false, MachineInt);
            RequireMachineMethod(module, "VoxelEngine.StorageContainerData", "get_capacityAvailable", false, MachineFloat);
            RequireMachineField(module, MachineJob, "deltaTime", MachineFloat);
            RequireMachineField(module, MachineJob, "frame", MachineInt);
            RequireMachineField(module, MachineSystems, "job", MachineJob, isStatic: true);
            RequireMachineMethod(module, MachineSystems, "ScheduleFromGraph", true, "Unity.Jobs.JobHandle", "Unity.Jobs.JobHandle");
            RequireMachineMethod(module, MachineSystems, "DisposeJobCallback", true, MachineVoid);
            if (module.GetType(MachineJob)?.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute") != true)
                throw new InvalidOperationException("The shared native systems job contract changed.");
        }

        private static void AuditMachineCommand(ModuleDefinition module, string type, string ui, string dragName, string name, bool isFurnace)
        {
            var update = RequireMachineMethod(module, type, "UpdateOnJob", false, MachineVoid, MachineJob);
            var release = RequireMachineMethod(module, type, "ReleaseInputAndOutput", false, MachineVoid, MachineJob);
            var cancel = RequireMachineMethod(module, type, "CancelCraft", false, MachineVoid, MachineJob);
            var valid = RequireMachineMethod(module, type, "IsStillValid", false, MachineBool, MachineJob);
            var completion = RequireMachineMethod(module, type, "get_completion", false, MachineFloat);
            var setCompletion = RequireMachineMethod(module, type, "set_completion", false, MachineVoid, MachineFloat);
            RequireMachineMethod(module, type, "get_CommandId", false, MachineInt);
            RequireMachineMethod(module, type, "get_StorageId", false, MachineInt);
            RequireMachineMethod(module, type, "get_State", false, MachineFloat);
            RequireMachineMethod(module, type, "get_brickCoord", false, "VoxelEngine.BrickCoord");
            var dispatch = RequireMachineMethod(module, type, "DisposeJobs", true, MachineVoid, MachineCommands);
            RequireMachineField(module, type, "datas", "Common.NativeRecyclableContainer`1<" + type + ">", isStatic: true);

            foreach (var field in new[] { "storageId", "inputItemId", "inputItemIdForCraft", "outputItemId" })
                RequireMachineField(module, type, field, MachineInt);
            foreach (var field in new[] { "autoCraftInventory", "pullComponents", "pushOutput", isFurnace ? "isDirty" : "isDirtyForUI" })
                RequireMachineField(module, type, field, MachineBool);
            RequireMachineField(module, type, "inputItemData", MachineItem);
            RequireMachineField(module, type, "speed", MachineFloat);
            RequireMachineField(module, type, "<completion>k__BackingField", MachineFloat);
            RequireMachineField(module, type, isFurnace ? "isSmelting" : "isCrushing", MachineState);
            RequireMachineField(module, type, isFurnace ? "onIngotSmelted" : "onOreCrushed", MachineState);
            RequireMachineMethod(module, type, "PushItself", false, MachineVoid);
            RequireMachineField(module, MachineJob, "jobCommands" + name, MachineCommands);

            if (isFurnace)
            {
                RequireMachineField(module, type, "isCrucible", MachineBool);
                RequireMachineField(module, type, "fuelItemIdFromUI", MachineInt);
                RequireMachineField(module, type, "schematic", "VoxelEngine.CraftingSchematic");
                RequireMachineMethod(module, type, "get_IsArcFurnace", false, MachineBool);
            }
            else
            {
                foreach (var field in new[] { "drillBitItemId", "inputQuantityForCraft", "inputSchematicId" })
                    RequireMachineField(module, type, field, MachineInt);
                RequireMachineField(module, type, "processType", "VoxelEngine.CrusherProcessType");
                if (MachineEnumValue(module, "VoxelEngine.CrusherProcessType", "none") != 0
                    || MachineEnumValue(module, "VoxelEngine.CrusherProcessType", "stoneOreBits") != 1
                    || MachineEnumValue(module, "VoxelEngine.CrusherProcessType", "damagedBrick") != 2)
                    throw new InvalidOperationException("The native Crusher processing states changed.");
            }

            var getter = MachineBody(completion);
            var setter = MachineBody(setCompletion);
            if (getter.Length != 3 || !MachineFieldAccess(getter[1], type, "<completion>k__BackingField", Code.Ldfld)
                || setter.Length != 4 || !MachineFieldAccess(setter[2], type, "<completion>k__BackingField", Code.Stfld))
                throw new InvalidOperationException("The native machine progress storage changed: " + name);

            var tick = MachineBody(update);
            var releaseIndex = MachineCallIndex(tick, release);
            var validIndex = MachineCallIndex(tick, valid);
            var cancelIndex = MachineCallIndex(tick, cancel);
            var progress = MachineCallIndex(tick, setCompletion);
            if (!(releaseIndex < validIndex && validIndex < cancelIndex && cancelIndex < progress)
                || progress < 5 || tick[progress - 1].OpCode.Code != Code.Add || tick[progress - 2].OpCode.Code != Code.Mul
                || !MachineFieldAccess(tick[progress - 3], type, "speed", Code.Ldfld)
                || !tick.Take(progress).Any(instruction => MachineFieldAccess(instruction, MachineJob, "deltaTime", Code.Ldfld)))
                throw new InvalidOperationException("Zero native machine speed no longer leaves managed progress unchanged: " + name);
            RequireMachineCompletionGate(tick, releaseIndex, completion, isFurnace);

            var systemsTick = MachineBody(RequireMachineMethod(module, MachineJob, "Execute", false, MachineVoid));
            MachineCallIndex(systemsTick, update);
            var draw = Array.FindIndex(tick, instruction => MachineFieldAccess(instruction,
                "VoxelEngine.EntitySystemResources/EntitySystemData", "powerConsumedFromJobs", Code.Ldflda));
            if (draw < 0 || draw + 8 >= tick.Length || tick[draw + 1].OpCode.Code != Code.Dup
                || tick[draw + 2].OpCode.Code != Code.Ldind_R4 || tick[draw + 3].OpCode.Code != Code.Ldarg_1
                || !MachineFieldAccess(tick[draw + 4], MachineJob, "deltaTime", Code.Ldfld)
                || tick[draw + 5].OpCode.Code != Code.Ldc_R4 || tick[draw + 5].Operand is not float watts
                || !float.IsFinite(watts) || watts <= 0f || tick[draw + 6].OpCode.Code != Code.Mul
                || tick[draw + 7].OpCode.Code != Code.Add || tick[draw + 8].OpCode.Code != Code.Stind_R4)
                throw new InvalidOperationException("Native powered machine accounting changed: " + name);

            var insert = RequireMachineMethod(module, MachineStorage, "InsertItem", true, MachineBool,
                MachineInt, MachineInt, MachineContext, MachineInt, "VoxelEngine.StorageInsertCapacityPolicy", "VoxelEngine.StorageInsertMergePolicy");
            var emit = RequireMachineMethod(module, MachineState, "EmitPulse", false, MachineVoid, MachineStateArray, MachineInt);
            var releaseBody = MachineBody(release);
            var insertIndex = MachineCallIndex(releaseBody, insert);
            if (insertIndex < 3 || releaseBody[insertIndex + 1].OpCode.Code != Code.Pop
                || Constant(releaseBody[insertIndex - 2]) != MachineEnumValue(module, "VoxelEngine.StorageInsertCapacityPolicy", "ignoreCapacity")
                || Constant(releaseBody[insertIndex - 1]) != MachineEnumValue(module, "VoxelEngine.StorageInsertMergePolicy", "tryMerge")
                || insertIndex >= MachineCallIndex(releaseBody, emit))
                throw new InvalidOperationException("The native machine output handoff changed: " + name);
            if (isFurnace)
            {
                if (!releaseBody.Any(instruction => MachineFieldAccess(instruction, MachineJob, "jobCommandsFurnace", Code.Ldflda))
                    || !releaseBody.Any(instruction => MachineFieldAccess(instruction, "VoxelEngine.CraftingElement", "quantity", Code.Ldfld)))
                    throw new InvalidOperationException("Arc Furnace release no longer uses the staged recipe quantity and command list.");
            }
            else
            {
                var consume = RequireMachineMethod(module, type, "ConsumeInput", false, MachineVoid, MachineJob);
                var wear = RequireMachineMethod(module, type, "DamageDrillBit", false, MachineVoid, MachineJob);
                if (insertIndex >= MachineCallIndex(releaseBody, consume)
                    || MachineCallIndex(releaseBody, consume) >= MachineCallIndex(releaseBody, wear)
                    || !MachineBody(consume).Any(instruction => MachineFieldAccess(instruction, type, "inputQuantityForCraft", Code.Ldfld))
                    || !MachineBody(wear).Any(instruction => MachineFieldAccess(instruction, MachineJob, "jobCommandsCrusher", Code.Ldflda)))
                    throw new InvalidOperationException("Crusher release no longer consumes staged input and dispatches stock drill wear.");
            }
            if (!MachineBody(dispatch).Any(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == MachineCommands && target.Name == "Dispose" && target.Parameters.Count == 0))
                throw new InvalidOperationException("Native command dispatch no longer owns its temporary list: " + name);

            AuditMachineUi(module, type, ui, dragName);
            AuditMachineSave(module, type, name);
        }

        private static void RequireMachineCompletionGate(Instruction[] body, int releaseIndex, MethodDefinition completion, bool isFurnace)
        {
            // Both forms must skip the native commit at an exact progress of 1.
            var gate = releaseIndex - (isFurnace ? 3 : 6);
            if (gate < 5 || body[releaseIndex - 2].OpCode.Code != Code.Ldarg_0
                || body[releaseIndex - 1].OpCode.Code != Code.Ldarg_1)
                throw new InvalidOperationException("Native machine completion call shape changed.");
            if (isFurnace)
            {
                if (body[gate].OpCode.Code is not (Code.Ble_Un or Code.Ble_Un_S)
                    || body[gate].Operand is not Instruction target || Array.IndexOf(body, target) <= releaseIndex
                    || body[gate - 4].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || !ReferenceEquals(body[gate - 4].Operand, body[releaseIndex - 2])
                    || !MachineFieldAccess(body[gate - 5], MachineFurnace, "outputItemId", Code.Ldfld))
                    throw new InvalidOperationException("Arc Furnace must only commit a pending output above managed progress 1.");
            }
            else if (body[gate].OpCode.Code != Code.Cgt || MachineLocalIndex(body[gate + 1], store: true) is not int local
                || MachineLocalIndex(body[gate + 2], store: false) != local
                || body[gate + 3].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                || !ReferenceEquals(body[gate + 3].Operand, body[releaseIndex + 1]))
                throw new InvalidOperationException("Crusher must only commit a pending output above managed progress 1.");
            if (!MachineCall(body[gate - 2], completion) || body[gate - 1].OpCode.Code != Code.Ldc_R4 || !Equals(body[gate - 1].Operand, 1f))
                throw new InvalidOperationException("Native machine commit ceiling changed.");
        }

        private static int? MachineLocalIndex(Instruction instruction, bool store)
        {
            return (store, instruction.OpCode.Code) switch
            {
                (true, Code.Stloc_0) or (false, Code.Ldloc_0) => 0,
                (true, Code.Stloc_1) or (false, Code.Ldloc_1) => 1,
                (true, Code.Stloc_2) or (false, Code.Ldloc_2) => 2,
                (true, Code.Stloc_3) or (false, Code.Ldloc_3) => 3,
                (true, Code.Stloc or Code.Stloc_S) or (false, Code.Ldloc or Code.Ldloc_S)
                    => (instruction.Operand as VariableDefinition)?.Index,
                _ => null
            };
        }

        private static void AuditMachineUi(ModuleDefinition module, string type, string ui, string dragName)
        {
            var awake = MachineBody(RequireMachineMethod(module, ui, "Awake", false, MachineVoid));
            var drag = RequireMachineMethod(module, ui, dragName, false, MachineBool, MachineItem);
            var stockPredicate = RequireMachineMethod(module, type, "IsItemValidInput", false, MachineBool, MachineItem);
            var block = RequireMachineMethod(module, "ItemSlot", "SetIsBlocked", false, MachineVoid, MachineBool);
            var outputLock = MachineCallIndex(awake, block);
            if (outputLock < 2 || !MachineFieldAccess(awake[outputLock - 2], ui, "slotOutput", Code.Ldfld)
                || Constant(awake[outputLock - 1]) != 1
                || !awake.Any(instruction => instruction.OpCode.Code == Code.Ldftn
                    && instruction.Operand is MethodReference target && target.FullName == drag.FullName)
                || !awake.Any(instruction => MachineFieldAccess(instruction, "ItemSlot", "canDropItem", Code.Stfld)))
                throw new InvalidOperationException("Native manual drop or pending output lock changed: " + ui);
            MachineCallIndex(MachineBody(drag), stockPredicate);
            var change = RequireMachineMethod(module, ui, "OnInputSlotUpdated", false, MachineVoid, MachineInt, MachineInt, MachineInt, MachineInt);
            if (!MachineBody(change).Any(instruction => MachineFieldAccess(instruction, type, "inputItemId", Code.Stfld)))
                throw new InvalidOperationException("The native input slot no longer publishes the machine input item: " + ui);
            var slotClick = MachineBody(RequireMachineMethod(module, "ItemSlot", "OnClick", false, MachineVoid));
            var slotDrop = MachineBody(RequireMachineMethod(module, "ItemSlot", "DropItem", false, MachineVoid));
            if (!MachineBody(block).Any(instruction => MachineFieldAccess(instruction, "ItemSlot", "isBlocked", Code.Stfld))
                || !MachineFieldAccess(slotClick[1], "ItemSlot", "isBlocked", Code.Ldfld) || !MachineReturnsIfBlocked(slotClick, 2)
                || !MachineFieldAccess(slotDrop[3], "ItemSlot", "isBlocked", Code.Ldfld)
                || slotDrop[4].OpCode.Code != Code.Or || !MachineReturnsIfBlocked(slotDrop, 5))
                throw new InvalidOperationException("Stock slot blocking no longer guards manual changes.");
        }

        private static bool MachineReturnsIfBlocked(Instruction[] body, int branch)
            => body[branch].OpCode.Code is Code.Brfalse or Code.Brfalse_S
                && body[branch + 1].OpCode.Code == Code.Ret
                && ReferenceEquals(body[branch].Operand, body[branch + 2]);

        private static void AuditMachineSave(ModuleDefinition module, string type, string name)
        {
            string dataType = type + "/" + name + "SaveData";
            var snapshot = RequireMachineMethod(module, type, "GetSaveData", false, dataType);
            var save = MachineBody(snapshot);
            var load = MachineBody(RequireMachineMethod(module, type, "OnSaveLoaded", false, MachineVoid, "System.Byte[]", MachineBool));
            var serialize = MachineBody(RequireMachineMethod(module, type, "GetCustomData", false, "System.Byte[]", MachineBool));
            MachineCallIndex(serialize, snapshot);
            if (!serialize.Any(instruction => MachineMessagePackCall(instruction, "Serialize", dataType))
                || !load.Any(instruction => MachineMessagePackCall(instruction, "Deserialize", dataType)))
                throw new InvalidOperationException("Native machine save codec changed: " + name);
            var storeInput = RequireMachineMethod(module, dataType, "set_inputItemData", false, MachineVoid, "Game.ItemSaveData");
            var loadInput = RequireMachineMethod(module, dataType, "get_inputItemData", false, "Game.ItemSaveData");
            var storeContainer = RequireMachineMethod(module, dataType, "set_storageData", false, MachineVoid, "VoxelEngine.ContainerSaveData");
            var loadContainer = RequireMachineMethod(module, dataType, "get_storageData", false, "VoxelEngine.ContainerSaveData");
            if (!save.Any(instruction => MachineFieldAccess(instruction, type, "inputItemId", Code.Ldfld))
                || save.Any(instruction => MachineFieldAccess(instruction, type, "outputItemId", Code.Ldfld)
                    || MachineFieldAccess(instruction, type, "<completion>k__BackingField", Code.Ldfld))
                || !load.Any(instruction => MachineFieldAccess(instruction, type, "inputItemId", Code.Stfld)))
                throw new InvalidOperationException("Native machine save no longer preserves inputs separately from in-flight output: " + name);
            MachineCallIndex(save, storeInput);
            MachineCallIndex(save, storeContainer);
            MachineCallIndex(load, loadInput);
            MachineCallIndex(load, loadContainer);
        }

        private static bool MachineMessagePackCall(Instruction instruction, string name, string dataType)
            => instruction.OpCode.Code == Code.Call && instruction.Operand is GenericInstanceMethod method
                && method.DeclaringType.FullName == "MessagePack.MessagePackSerializer" && method.Name == name
                && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == dataType;

        private static int MachineEnumValue(ModuleDefinition module, string type, string member)
        {
            if (module.GetType(type)?.Fields.SingleOrDefault(field => field.Name == member && field.IsLiteral)?.Constant is int value)
                return value;
            throw new InvalidOperationException("Native machine enum member changed: " + type + "." + member);
        }

        private static void AuditMachineRequests(ModuleDefinition module)
        {
            var match = MachineBody(RequireMachineMethod(module, MachineRequest, "IsMatching", false, MachineBool, MachineItem, MachineContext));
            foreach (var field in new[] { "prefabId", "contextualDataId" })
            {
                RequireMachineField(module, MachineItem, field, MachineInt);
                RequireMachineField(module, MachineRequest, field, MachineInt);
            }
            var typeGate = Array.FindIndex(match, instruction => MachineFieldAccess(instruction, MachineItem, "itemType", Code.Ldfld));
            if (typeGate < 0 || !MachineFieldAccess(match[typeGate + 2], MachineRequest, "itemType", Code.Ldfld)
                || match[typeGate + 3].OpCode.Code is not (Code.Beq or Code.Beq_S)
                || Constant(match[typeGate + 4]) != 0 || match[typeGate + 5].OpCode.Code != Code.Ret
                || !ReferenceEquals(match[typeGate + 3].Operand, match[typeGate + 6]))
                throw new InvalidOperationException("Native conveyor requests no longer guard item kind.");

            // The compact enum dispatch leads to one prefab comparator for crafting parts and
            // a prefab followed by a contextual comparator for ore/ingots.
            var dispatch = Array.FindIndex(match, instruction => instruction.OpCode.Code == Code.Switch);
            if (dispatch < 8 || dispatch + 13 >= match.Length || match[dispatch - 1].OpCode.Code != Code.Sub
                || match[dispatch - 4].OpCode.Code is not (Code.Bgt or Code.Bgt_S)
                || !ReferenceEquals(match[dispatch - 4].Operand, match[dispatch + 5])
                || match[dispatch].Operand is not Instruction[] cases
                || match[dispatch + 4].OpCode.Code is not (Code.Br or Code.Br_S)
                || match[dispatch + 4].Operand is not Instruction fallback)
                throw new InvalidOperationException("Native conveyor item dispatch changed.");
            int caseBase = Constant(match[dispatch - 2]);
            int upperSplit = Constant(match[dispatch - 5]);
            int parts = MachineEnumValue(module, MachineItemKind, "craftingParts");
            if (parts < caseBase + cases.Length || parts >= upperSplit
                || Constant(match[dispatch + 2]) != upperSplit
                || match[dispatch + 3].OpCode.Code is not (Code.Beq or Code.Beq_S))
                throw new InvalidOperationException("Crafting parts no longer take the exact prefab conveyor path.");
            RequireMachineRequestComparison(match, Array.IndexOf(match, fallback), "prefabId");

            int ore = MachineEnumValue(module, MachineItemKind, "ore");
            int ingot = MachineEnumValue(module, MachineItemKind, "ingot");
            int bits = MachineEnumValue(module, MachineItemKind, "stoneOreBits");
            int smallOre = MachineEnumValue(module, MachineItemKind, "ore_small");
            if (ore < caseBase || ingot < caseBase || ore >= caseBase + cases.Length || ingot >= caseBase + cases.Length
                || !ReferenceEquals(cases[ore - caseBase], cases[ingot - caseBase])
                || Constant(match[dispatch + 9]) != bits || bits + 1 != smallOre
                || Constant(match[dispatch + 11]) != 1
                || match[dispatch + 12].OpCode.Code is not (Code.Ble_Un or Code.Ble_Un_S)
                || !ReferenceEquals(match[dispatch + 12].Operand, cases[ore - caseBase]))
                throw new InvalidOperationException("Ore conveyor kinds no longer share the verified contextual match.");
            int crystal = MachineEnumValue(module, MachineItemKind, "crystal");
            if (crystal <= upperSplit || crystal == Constant(match[dispatch + 6])
                || crystal == bits || crystal == smallOre
                || match[dispatch + 7].OpCode.Code is not (Code.Beq or Code.Beq_S)
                || match[dispatch + 13].OpCode.Code is not (Code.Br or Code.Br_S)
                || !ReferenceEquals(match[dispatch + 13].Operand, fallback))
                throw new InvalidOperationException("Crystals no longer take the exact prefab conveyor path.");
            int oreBlock = Array.IndexOf(match, cases[ore - caseBase]);
            RequireMachineRequestComparison(match, oreBlock, "prefabId", continues: true);
            RequireMachineRequestComparison(match, oreBlock + 10, "contextualDataId");
        }

        private static void RequireMachineRequestComparison(Instruction[] body, int start, string field, bool continues = false)
        {
            if (start < 0 || start + 10 >= body.Length || body[start].OpCode.Code != Code.Ldarg_0
                || !MachineFieldAccess(body[start + 1], MachineRequest, field, Code.Ldfld)
                || body[start + 2].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                || body[start + 3].OpCode.Code != Code.Ldarg_1
                || !MachineFieldAccess(body[start + 4], MachineItem, field, Code.Ldfld)
                || body[start + 5].OpCode.Code != Code.Ldarg_0
                || !MachineFieldAccess(body[start + 6], MachineRequest, field, Code.Ldfld))
                throw new InvalidOperationException("Native conveyor matching lost exact " + field + " comparison.");

            if (continues)
            {
                var target = body[start + 10];
                if (body[start + 7].OpCode.Code is not (Code.Beq or Code.Beq_S)
                    || !ReferenceEquals(body[start + 2].Operand, target) || !ReferenceEquals(body[start + 7].Operand, target)
                    || Constant(body[start + 8]) != 0 || body[start + 9].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Ore conveyor requests no longer check both prefab and context.");
            }
            else if (body[start + 7].OpCode.Code == Code.Ceq)
            {
                if (body[start + 8].OpCode.Code != Code.Ret
                    || !ReferenceEquals(body[start + 2].Operand, body[start + 9])
                    || Constant(body[start + 9]) != 1 || body[start + 10].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Conveyor requests no longer require exact nonzero " + field + ".");
            }
            else if (start + 11 >= body.Length || body[start + 7].OpCode.Code is not (Code.Beq or Code.Beq_S)
                || !ReferenceEquals(body[start + 2].Operand, body[start + 10])
                || !ReferenceEquals(body[start + 7].Operand, body[start + 10])
                || Constant(body[start + 8]) != 0 || body[start + 9].OpCode.Code != Code.Ret
                || Constant(body[start + 10]) != 1 || body[start + 11].OpCode.Code != Code.Ret)
                throw new InvalidOperationException("Conveyor requests no longer require exact nonzero " + field + ".");
        }
    }
}
