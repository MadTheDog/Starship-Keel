using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateBlockAssemblies(ModuleDefinition module)
        {
            const string capability = "blockAssemblies";
            const string brick = "VoxelEngine.BrickInstanceData";
            const string bricks = "VoxelEngine.EntityBricks";
            const string entity = "VoxelEngine.VoxelEntity";
            const string entityId = "VoxelEngine.VoxelEntityID";
            try
            {
                var getter = AssemblyMethod(module, bricks, "GetBrickData", false, brick, "System.Int32");
                var resolve = AssemblyMethod(module, entityId, "get_Entity", false, entity);
                var findEntity = AssemblyMethod(module, "BlockEntityManager", "Get", false, entity, entityId);
                var source = AssemblyMethod(module, entity, "get_Bricks", false, bricks);
                var valid = AssemblyMethod(module, brick, "get_IsValid", false, "System.Boolean");
                AssemblyMethod(module, "Manager.GameloopManager", "get_IsNativeContainerAccessUnsafe", true, "System.Boolean");
                var fields = module.GetType(brick).Fields;
                RequireAssemblyField("instanceId", "System.Int32");
                RequireAssemblyField("brickId", "System.UInt16");
                RequireAssemblyField("gridLevel", "System.Byte");
                RequireAssemblyField("gridPosition", "Vector3i");
                RequireAssemblyField("rotation", "System.Byte");
                RequireAssemblyField("scale", "System.UInt16");
                RequireAssemblyField("healthScore", "System.Byte");

                var body = getter.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                if (!getter.IsPublic || !resolve.IsPublic || !source.IsPublic || !valid.IsPublic || body.Length != 17
                    || body[0].OpCode.Code != Code.Ldarg_1 || body[1].OpCode.Code != Code.Ldc_I4_0
                    || body[2].OpCode.Code is not (Code.Ble or Code.Ble_S) || !ReferenceEquals(body[2].Operand, body[13])
                    || body[3].OpCode.Code != Code.Ldarg_1 || body[4].OpCode.Code != Code.Ldarg_0
                    || !UsesBrickData(body[5]) || !AssemblyCall(body[6], "get_Length", "Common.NativeRecyclableContainer")
                    || body[7].OpCode.Code is not (Code.Bge or Code.Bge_S) || !ReferenceEquals(body[7].Operand, body[13])
                    || body[8].OpCode.Code != Code.Ldarg_0 || !UsesBrickData(body[9]) || body[10].OpCode.Code != Code.Ldarg_1
                    || !AssemblyCall(body[11], "get_Item", "Common.NativeRecyclableContainer")
                    || body[12].OpCode.Code != Code.Ret || body[14].OpCode.Code != Code.Initobj
                    || body[14].Operand is not TypeReference fallback || fallback.FullName != brick || body[16].OpCode.Code != Code.Ret
                    || !resolve.Body.Instructions.Any(instruction => AssemblyCall(instruction, findEntity.Name, findEntity.DeclaringType.FullName)))
                    throw new InvalidOperationException("The bounded native brick lookup or live entity resolver changed.");

                return new CapabilityResult(capability, true, "Bounded brick lookup and live entity resolution validated.");

                void RequireAssemblyField(string name, string type)
                {
                    if (!fields.Any(field => field.Name == name && field.IsPublic && field.FieldType.FullName == type))
                        throw new InvalidOperationException("Block assembly brick identity field changed: " + name);
                }

                bool UsesBrickData(Instruction instruction)
                    => instruction.OpCode.Code == Code.Ldflda && instruction.Operand is FieldReference field
                        && field.DeclaringType.FullName == bricks && field.Name == "brickDatas"
                        && field.FieldType.FullName == "Common.NativeRecyclableContainer`1<" + brick + ">";
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateShipyardAssemblyEligibility(ModuleDefinition module)
        {
            const string capability = "shipyardAssemblyEligibility";
            const string yard = "VoxelEngine.CommandShipyard";
            const string metadata = "VoxelEngine.EntityMetaData";
            const string quantities = "System.Collections.Generic.Dictionary`2<System.Int32,System.Int32>";
            try
            {
                var preview = AssemblyMethod(module, yard, "CanSpawnBlueprint", false, "System.Boolean",
                    metadata, quantities, "System.Text.StringBuilder");
                var lookup = AssemblyMethod(module, "VoxelEngine.BrickCoord", "get_Command", false, "VoxelEngine.ICommand");
                var spawn = AssemblyMethod(module, yard, "SpawnBlueprintFromBlueprintSelection", false, "System.Void",
                    metadata, "System.String", "System.String");
                var asyncType = spawn.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    ?.ConstructorArguments.Single().Value as TypeReference;
                var stateMachine = asyncType?.Resolve()?.Methods.SingleOrDefault(method => method.Name == "MoveNext");
                if (!preview.IsPublic || !spawn.IsPublic || !lookup.IsPublic || stateMachine == null || !stateMachine.HasBody)
                    throw new InvalidOperationException("Native blueprint spawn entry point changed.");
                var instructions = stateMachine.Body.Instructions.ToArray();
                int consume = Array.FindIndex(instructions, instruction => AssemblyCall(instruction, "ConsumeBlueprintBricks", yard));
                int charge = Array.FindIndex(instructions, instruction => AssemblyCall(instruction, "AddCash"));
                int dispatch = Array.FindIndex(instructions, instruction => AssemblyCall(instruction, "CmdLoadBlueprint"));
                int firstAwait = Array.FindIndex(instructions, instruction =>
                    AssemblyCall(instruction, "AwaitOnCompleted", "System.Runtime.CompilerServices.AsyncVoidMethodBuilder")
                    || AssemblyCall(instruction, "AwaitUnsafeOnCompleted", "System.Runtime.CompilerServices.AsyncVoidMethodBuilder"));
                if (consume < 0 || charge < 0 || dispatch <= consume || dispatch <= charge || firstAwait <= dispatch)
                    throw new InvalidOperationException("Native blueprint charging and dispatch no longer precede the first suspension.");

                return new CapabilityResult(capability, true, "Blueprint entry, charging and dispatch order validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static MethodDefinition AssemblyMethod(ModuleDefinition module, string owner, string name, bool isStatic,
            string result, params string[] parameters)
            => module.GetType(owner)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.HasBody && method.ReturnType.FullName == result
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException("Missing native block assembly method: " + owner + "." + name);

        private static bool AssemblyCall(Instruction instruction, string name, string? owner = null)
            => instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference called
                && called.Name == name && (owner == null || called.DeclaringType.GetElementType().FullName == owner
                    || called.DeclaringType.GetElementType().FullName == owner + "`1");
    }
}
