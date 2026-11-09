using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateNativeBlockVariants(ModuleDefinition module)
        {
            const string capability = "nativeBlockVariants";
            try
            {
                var brick = module.Types.Single(type => type.Name == "CommandBrick`1");
                var args = brick.Fields.Single(field => field.Name == "commandArgs"
                    && field.FieldType.FullName == "System.Object[]" && !field.IsStatic);
                var setter = brick.Methods.Single(method => method.Name == "SetCommandArgs" && method.HasBody
                    && method.IsPublic && method.Parameters.Count == 1
                    && method.Parameters[0].ParameterType.FullName == "System.Object[]");
                var construct = brick.Methods.Single(method => method.Name == "OnConstruct" && method.HasBody);
                var remove = brick.Methods.Single(method => method.Name == "OnRemove" && method.HasBody);
                if (!Uses(setter, Code.Stfld) || !Uses(construct, Code.Ldfld)
                    || !VariantCalls(construct, "Initialize") || !VariantCalls(remove, "OnDestroy"))
                    throw new InvalidOperationException("Native command initialization or cleanup changed.");

                RequireVariantMethods(module, "VoxelEngine.SystemMagLock", "OnInitialize", "OnSaveLoaded",
                    "OnLoadingSequenceFinished", "OnDestroy");
                RequireVariantMethods(module, "VoxelEngine.EnterCommandSeat", "OnInitialize", "OnEnter", "OnExit", "OnDestroy");
                return new CapabilityResult(capability, true, "Native command arguments and lifecycle validated.");

                bool Uses(MethodDefinition method, Code opcode) => method.Body.Instructions.Any(instruction =>
                    instruction.OpCode.Code == opcode && instruction.Operand is FieldReference reference
                    && reference.Name == args.Name && reference.FieldType.FullName == args.FieldType.FullName);
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void RequireVariantMethods(ModuleDefinition module, string name, params string[] methods)
        {
            var type = module.GetType(name);
            if (type == null || methods.Any(required => !type.Methods.Any(method => method.Name == required && method.HasBody)))
                throw new InvalidOperationException("Native variant contract changed: " + name);
        }

        private static bool VariantCalls(MethodDefinition method, string name) =>
            method.Body.Instructions.Any(instruction => (instruction.OpCode.Code == Code.Call
                || instruction.OpCode.Code == Code.Callvirt) && instruction.Operand is MethodReference target && target.Name == name);
    }
}
