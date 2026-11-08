using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateFluids(ModuleDefinition module)
        {
            const string capability = "fluidHooks";
            const string loop = "Manager.GameloopManager";
            const string fuel = "VoxelEngine.FuelCommon";
            const string fluidId = fuel + "/FuelType";
            const string storageContext = "VoxelEngine.StorageContext";
            const string tank = "VoxelEngine.StorageCommandData";
            const string item = "Game.ItemInstanceData";
            const string request = "VoxelEngine.ItemRequestData";
            const string tankSave = "VoxelEngine.ContainerSaveData";
            const string itemSave = "Game.ItemSaveData";
            const string storage = "VoxelEngine.StorageContainerData";
            const string int32 = "System.Int32";
            const string single = "System.Single";
            const string boolean = "System.Boolean";
            const string voidType = "System.Void";

            try
            {
                MethodDefinition Require(string type, string method, bool isStatic, string result, params string[] parameters) =>
                    Method(module, type, method, isStatic, result, parameters)
                    ?? throw new InvalidOperationException($"Missing fluid method contract: {type}.{method}.");

                var after = Require(loop, "OnAfterRendering", true, voidType);
                Require(loop, "get_IsMainThread", true, boolean);
                Require(loop, "get_IsNativeContainerAccessUnsafe", true, boolean);
                var completion = UniqueCall(after, loop, "CompleteJobs");
                var disposal = UniqueCall(after, loop, "DisposeJobs");
                if (completion >= disposal || disposal >= UniqueCall(after, loop, "ExecuteCommandsJob"))
                    throw new InvalidOperationException("The fluid callback no longer follows native job completion and disposal.");

                var complete = Require(loop, "CompleteJobs", true, voidType);
                var wait = Require("Manager.JobGraph", "CompleteScheduledNodes", false, voidType, int32);
                var unlock = Require(loop, "SetContainerJobLock", true, voidType, boolean);
                var waitIndex = FluidCall(complete, wait);
                var unlockIndex = FluidCall(complete, unlock);
                if (waitIndex >= unlockIndex || Constant(complete.Body.Instructions[unlockIndex - 1]) != 0)
                    throw new InvalidOperationException("The game job graph no longer unlocks storage after waiting for the frame.");

                if (!Field(module, item, "contextualDataId", int32, false)
                    || !Field(module, request, "contextualDataId", int32, false)
                    || !Field(module, itemSave, "contextualDataId", int32, false)
                    || !Field(module, storage, "fuelType", fluidId, false)
                    || !Field(module, storage, "requestId", int32, false)
                    || module.GetType(fluidId)?.Fields.SingleOrDefault(field => field.Name == "value__")?.FieldType.FullName != int32
                    || module.GetType(tank)?.Fields.SingleOrDefault(field => field.Name == "fuelType")?.FieldType.FullName != fluidId)
                    throw new InvalidOperationException("The fluid identity is no longer an integer in items, requests and saved tanks.");

                Require(tankSave, "get_fuelType", false, int32);
                Require(tankSave, "set_fuelType", false, voidType, int32);
                Require(tank, "OpenConfigWindow", false, voidType);
                Require(tank, "GetSaveData", false, tankSave);
                Require(tank, "get_StorageId", false, int32);
                var applyTank = Require(tank, "OnGenericParamsUpdated", false, voidType, "System.Object", boolean, boolean);
                FluidCall(applyTank, Require(fuel, "SetFuelTypeAndClean", true, voidType, int32, fluidId, storageContext));
                Require(fuel, "GetFuelLabel", true, "System.String", fluidId, boolean, boolean);
                Require(fuel, "GetFuel", true, single, int32, fluidId, storageContext);
                var fill = Require(fuel, "FillTank", true, single, int32, single, fluidId, storageContext);
                var refill = Require(fuel, "RequestTankRefill", true, voidType, int32, fluidId, storageContext);
                var consume = Require(fuel, "ConsumeFuelSafe", true, single, int32, single, fluidId, storageContext);
                var match = Require(request, "IsMatching", false, boolean, item, storageContext);
                if (!FluidFieldAccess(fill, item, "contextualDataId", Code.Stfld)
                    || !FluidFieldAccess(refill, request, "contextualDataId", Code.Stfld)
                    || !FluidFieldAccess(consume, "VoxelEngine.CraftingElement", "contextualDataId", Code.Stfld)
                    || !FluidFieldAccess(match, item, "contextualDataId", Code.Ldfld)
                    || !FluidFieldAccess(match, request, "contextualDataId", Code.Ldfld))
                    throw new InvalidOperationException("The native fluid read/write or transfer path stopped using contextual identity.");

                return new CapabilityResult(capability, true,
                    "Validated post-job command boundary and native contextual fluid identity contracts.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static int FluidCall(MethodDefinition source, MethodDefinition target)
        {
            var instructions = source.Body.Instructions;
            int index = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                if ((instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt)
                    || !(instruction.Operand is MethodReference method) || method.FullName != target.FullName)
                    continue;
                if (index >= 0)
                    throw new InvalidOperationException("Ambiguous fluid call relationship: " + target.FullName);
                index = i;
            }
            if (index < 0)
                throw new InvalidOperationException("Missing fluid call relationship: " + target.FullName);
            return index;
        }

        private static bool FluidFieldAccess(MethodDefinition method, string type, string name, Code opcode) =>
            method.Body.Instructions.Any(instruction => instruction.OpCode.Code == opcode
                && instruction.Operand is FieldReference field && field.Name == name && field.DeclaringType.FullName == type);
    }
}
