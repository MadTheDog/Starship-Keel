using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateLogicOutputs(ModuleDefinition module)
        {
            const string capability = "logicOutputs";
            const string gate = "VoxelEngine.CommandEventGate";
            const string resources = "VoxelEngine.CommandEventGateResources";
            const string state = "VoxelEngine.CommandStateData";
            const string stateType = state + "/StateType";
            const string attribute = state + "/CommandStateDataField";
            const string provider = "VoxelEngine.ICommandEventStateProvider";
            const string save = gate + "/EventGateDataSave";
            const string integer = "System.Int32";
            const string nothing = "System.Void";
            const string flag = "System.Boolean";

            try
            {
                bool Reserved(object? value)
                    => value != null && Convert.ToInt32(value) >= LogicOutputProvider.SignalEventId
                    && Convert.ToInt32(value) <= LogicOutputProvider.ValueEventId;

                var eventEnum = module.GetType("VoxelEngine.EventId")
                    ?? throw new InvalidOperationException("Native EventId values are missing.");
                if (eventEnum.Fields.Any(field => field.HasConstant && Reserved(field.Constant))
                    || AllLogicTypes(module.Types).Any(type => type.Fields.Any(field => field.CustomAttributes.Any(item =>
                        item.AttributeType.FullName == attribute && item.ConstructorArguments.Count >= 2
                        && Reserved(item.ConstructorArguments[1].Value)))))
                    throw new InvalidOperationException("A stock Event Gate event overlaps Keel's saved output slots.");

                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module, type, name, isStatic, result, parameters) is { HasBody: true } method ? method
                        : throw new InvalidOperationException("Missing native logic output method: " + type + "." + name);

                if (!Field(module, resources, "FieldInfos", "System.Reflection.FieldInfo[]", true)
                    || !Field(module, resources, "CommandStateDataFields", attribute + "[]", true)
                    || !Field(module, resources, "TypeFields",
                        "System.Collections.Generic.Dictionary`2<System.Type,System.Collections.Generic.List`1<System.Reflection.FieldInfo>>", true)
                    || !Field(module, state, "id", integer, false)
                    || !Field(module, state, "stateType", stateType, false))
                    throw new InvalidOperationException("Native Event Gate or state storage fields changed.");

                var constructor = Require(resources, ".cctor", true, nothing);
                int Capacity(string field)
                {
                    var store = constructor.Body.Instructions.SingleOrDefault(instruction =>
                        instruction.OpCode.Code == Code.Stsfld && instruction.Operand is FieldReference target
                        && target.DeclaringType.FullName == resources && target.Name == field);
                    return store?.Previous?.OpCode.Code == Code.Newarr ? Constant(store.Previous.Previous) : 0;
                }
                if (Capacity("FieldInfos") <= LogicOutputProvider.ValueEventId
                    || Capacity("CommandStateDataFields") <= LogicOutputProvider.ValueEventId)
                    throw new InvalidOperationException("Native Event Gate arrays no longer cover the reserved slots.");

                Require(state, "Register", false, nothing, stateType);
                Require(state, "Release", false, nothing);
                Require(state, "Set", false, nothing, "System.Single", integer);
                Require(state, "EmitPulse", false, nothing, integer);
                RequireSurfaceEnum(module, stateType, "signal", (int)CommandStateData.StateType.signal);
                RequireSurfaceEnum(module, stateType, "pulse", (int)CommandStateData.StateType.pulse);
                RequireSurfaceEnum(module, stateType, "value", (int)CommandStateData.StateType.value);
                var texture = Method(module, provider, "get_EventTextureId", false, "VoxelEngine.EventTextureId");
                if (texture == null || !texture.IsAbstract)
                    throw new InvalidOperationException("Native event provider texture contract changed.");

                var allocate = Require(resources, "OnAllocateResources", true, nothing);
                var reload = Require(resources, "OnGameReload", true, nothing);
                var binding = Require(gate, "SetStateData", false, nothing);
                var configuration = Require(gate, "OpenConfigWindow", false, nothing);
                var loaded = Require(gate, "OnEntityInitialized", false, nothing, flag);
                var writer = Require(gate, "GetCustomData", false, "System.Byte[]", flag);
                var reader = Require(gate, "OnSaveLoaded", false, nothing, "System.Byte[]", flag);
                var selection = module.GetType(save)?.Properties.SingleOrDefault(property => property.Name == "eventSelected");
                if (selection?.PropertyType.FullName != integer || !selection.CustomAttributes.Any(item =>
                        item.AttributeType.FullName == "MessagePack.KeyAttribute" && item.ConstructorArguments.Count == 1
                        && Equals(item.ConstructorArguments[0].Value, 0)))
                    throw new InvalidOperationException("The saved Event Gate selection key changed.");

                bool References(MethodDefinition method, string type, string name) => method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == Code.Ldsfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == type && field.Name == name);
                bool Calls(MethodDefinition method, string type, string name) => method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference called
                    && called.DeclaringType.FullName == type && called.Name == name);
                bool ReplacesBindings(MethodDefinition method) => method.Body.Instructions.Any(instruction =>
                    (instruction.OpCode.Code == Code.Stsfld && instruction.Operand is FieldReference field
                        && field.DeclaringType.FullName == resources && field.Name is "FieldInfos" or "CommandStateDataFields" or "TypeFields")
                    || (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference call
                        && call.Name == "Clear"));
                if (ReplacesBindings(allocate) || ReplacesBindings(reload)
                    || !Calls(allocate, "System.Reflection.Assembly", "GetTypes")
                    || !References(allocate, resources, "FieldInfos") || !References(allocate, resources, "CommandStateDataFields")
                    || !References(binding, resources, "FieldInfos") || !References(binding, resources, "TypeFields")
                    || !Calls(configuration, "System.Type", "GetFields") || !Calls(configuration, "System.Attribute", "GetCustomAttributes")
                    || !Calls(binding, save, "get_eventSelected") || !Calls(loaded, gate, "SetStateData")
                    || !Calls(writer, "MessagePack.MessagePackSerializer", "Serialize")
                    || !Calls(reader, "MessagePack.MessagePackSerializer", "Deserialize"))
                    throw new InvalidOperationException("Native Event Gate configuration or save binding changed.");

                return new CapabilityResult(capability, true, "Validated native output state, vacant stable event slots, and Event Gate save binding.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static IEnumerable<TypeDefinition> AllLogicTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in AllLogicTypes(type.NestedTypes)) yield return nested;
            }
        }
    }
}
