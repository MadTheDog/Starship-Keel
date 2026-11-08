using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGathering(ModuleDefinition module)
        {
            const string capability = "gatheringHooks";
            const string worldLogic = "Game.ItemWorldLogic";
            const string item = "Game.ItemInstanceData";
            const string impact = "Manager.ImpactData";
            const string transform = "Manager.SkyTransformData";
            const string loop = "Manager.GameloopManager";
            const string voidType = "System.Void";
            const string boolean = "System.Boolean";
            const string integer = "System.Int32";

            try
            {
                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, type, name, isStatic, result, parameters)
                    ?? throw new InvalidOperationException("Missing gathering method contract: " + type + "." + name);

                var hit = Require(worldLogic, "OnHit", true, voidType, item, impact);
                var drop = Require(worldLogic, "DropItem", true, voidType, item, impact, "Game.PrefabData",
                    worldLogic + "/ItemWorldInteractionData", integer, "System.UInt32", transform);
                var release = Require(item, "ReleaseComponentsAndItemId", false, voidType, boolean);
                var register = Require(item, "Register", false, integer);
                var components = Require(item, "AllocateAllComponents", false, voidType, transform, boolean, boolean,
                    "System.Single", integer, boolean);
                var applyImpulse = Require(item, "ApplyImpulse", true, voidType, item, "System.Single",
                    "UnityEngine.Vector3", "System.Single");
                Require(item, "CanRenderInWorld", false, boolean);
                Require(loop, "get_IsMainThread", true, boolean);
                Require(loop, "get_IsNativeContainerAccessUnsafe", true, boolean);
                Require(loop, "ExecuteCommandsJob", true, voidType);
                Require("PlayerState", "get_sessionMode", true, "PlayerState/SessionMode");
                Require("PlayerState", "get_InLoadingScreen", true, boolean);
                Require("PlayerState", "get_IsQuittingToDesktopOrTitle", true, boolean);
                Require("Game.ItemInstanceCommon", "IsMineableWorldProp", true, boolean, "Game.WorldItemType");
                Require("Game.ItemInstanceCommon", "IsValidStackQuantity", true, boolean, "System.Single");
                if (!Field(module, item, "id", integer, false) || !Field(module, item, "prefabId", integer, false)
                    || !Field(module, item, "itemType", "Game.WorldItemType", false)
                    || !Field(module, item, "seed", "System.UInt32", false)
                    || !Field(module, item, "disposeTime", "System.Single", false)
                    || !Field(module, impact, "seed", "System.UInt32", false)
                    || !Field(module, impact, "localNormal", "UnityEngine.Vector3", false))
                    throw new InvalidOperationException("World prop item identity or impact fields changed.");

                var stateAttribute = hit.CustomAttributes.Single(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
                var stateType = ((TypeReference)stateAttribute.ConstructorArguments.Single().Value).Resolve();
                if (stateType.DeclaringType != hit.DeclaringType
                    || !hit.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Call
                        && instruction.Operand is GenericInstanceMethod builder
                        && builder.DeclaringType.FullName == "System.Runtime.CompilerServices.AsyncVoidMethodBuilder"
                        && builder.Name == "Start" && builder.GenericArguments.Single().FullName == stateType.FullName))
                    throw new InvalidOperationException("World prop hits no longer start their managed drop state machine synchronously.");

                var move = Require(stateType.FullName, "MoveNext", false, voidType);
                var code = move.Body.Instructions;
                var releaseIndex = GatheringCall(move, release);
                var dropIndex = GatheringCall(move, drop);
                var awaitIndex = code.IndexOf(code.Single(instruction => instruction.OpCode == OpCodes.Call
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == "UnityAsync.Await"
                    && method.Name == "NextUpdate" && method.Parameters.Count == 0));
                var health = code.Single(instruction => instruction.OpCode == OpCodes.Ldfld
                    && instruction.Operand is FieldReference field && field.DeclaringType.FullName == "Game.PrefabData"
                    && field.Name == "healthPoint" && field.FieldType.FullName == "System.Single");
                var lethalGate = health.Next;
                if (health.Previous.Previous.OpCode != OpCodes.Ldfld
                    || !(health.Previous.Previous.Operand is FieldReference damage)
                    || damage.DeclaringType.FullName != item || damage.Name != "damage"
                    || (lethalGate.OpCode != OpCodes.Ble_Un && lethalGate.OpCode != OpCodes.Ble_Un_S)
                    || !(lethalGate.Operand is Instruction noBreak) || code.IndexOf(noBreak) <= dropIndex
                    || code.IndexOf(lethalGate) >= releaseIndex || releaseIndex >= dropIndex || dropIndex >= awaitIndex)
                    throw new InvalidOperationException("World prop loot no longer follows the lethal hit and source release before yielding.");

                // The penultimate native argument is the drop ordinal, loaded before the transform argument.
                var ordinalLoad = code[dropIndex - 3];
                if (ordinalLoad.OpCode != OpCodes.Ldfld || !(ordinalLoad.Operand is FieldReference ordinal)
                    || ordinal.DeclaringType.FullName != stateType.FullName || ordinal.FieldType.FullName != "System.UInt32")
                    throw new InvalidOperationException("The native world prop drop no longer receives the loop ordinal.");
                var ordinalWrites = code.Where(instruction => instruction.OpCode == OpCodes.Stfld
                    && instruction.Operand is FieldReference field && field.FullName == ordinal.FullName).ToArray();
                if (ordinalWrites.Length != 2 || ordinalWrites[0].Previous.OpCode != OpCodes.Ldc_I4_0
                    || code.IndexOf(ordinalWrites[0]) <= releaseIndex || code.IndexOf(ordinalWrites[0]) >= dropIndex
                    || ordinalWrites[1].Previous.OpCode != OpCodes.Add
                    || ordinalWrites[1].Previous.Previous.OpCode != OpCodes.Ldc_I4_1
                    || code.IndexOf(ordinalWrites[1]) <= awaitIndex)
                    throw new InvalidOperationException("World prop loot no longer starts its first drop at ordinal zero.");
                if (GatheringCall(drop, register) >= GatheringCall(drop, components)
                    || GatheringCall(drop, components) >= GatheringCall(drop, applyImpulse))
                    throw new InvalidOperationException("Native world prop drop allocation order changed.");

                var after = Require(loop, "OnAfterRendering", true, voidType);
                if (UniqueCall(after, loop, "CompleteJobs") >= UniqueCall(after, loop, "DisposeJobs")
                    || UniqueCall(after, loop, "DisposeJobs") >= UniqueCall(after, loop, "ExecuteCommandsJob"))
                    throw new InvalidOperationException("The gathering dispatch no longer follows native job completion.");
                var complete = Require(loop, "CompleteJobs", true, voidType);
                var wait = GatheringCall(complete, Require("Manager.JobGraph", "CompleteScheduledNodes", false, voidType, integer));
                var unlock = GatheringCall(complete, Require(loop, "SetContainerJobLock", true, voidType, boolean));
                if (wait >= unlock || Constant(complete.Body.Instructions[unlock - 1]) != 0)
                    throw new InvalidOperationException("Native storage remains locked after job completion.");

                return new CapabilityResult(capability, true, "Validated first managed world prop drop and post-job spawn contracts.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static int GatheringCall(MethodDefinition source, MethodDefinition target)
        {
            var instructions = source.Body.Instructions;
            var call = instructions.Single(instruction => (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                && instruction.Operand is MethodReference method && method.FullName == target.FullName);
            return instructions.IndexOf(call);
        }
    }
}
