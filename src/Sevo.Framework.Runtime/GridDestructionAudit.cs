using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        private const string GridParent = "VoxelEngine.ParentEntity";
        private const string GridEntity = "VoxelEngine.VoxelEntity";
        private const string GridIdentity = "VoxelEngine.EntityIdentity";
        private const string GridMetadata = "VoxelEngine.EntityMetaData";
        private const string GridSystems = "VoxelEngine.EntitySystems";
        private const string GridBoolean = "System.Boolean";
        private const string GridVoid = "System.Void";

        public static CapabilityResult EvaluateGridDestruction(ModuleDefinition module)
        {
            const string capability = "gridDestruction";
            try
            {
                var target = Method(module, GridParent, "OnGameplayDestructionServer", false, GridVoid);
                var getter = Method(module, GridParent, "get_isDestroyed", false, GridBoolean);
                var setter = Method(module, GridParent, "SetIsDestroyed", false, GridVoid);
                var state = Method(module, GridParent, "SetGameplayDestroyedState", false, GridVoid, GridBoolean);
                var systemsSetter = Method(module, GridSystems, "SetIsDestroyed", false, GridVoid, GridBoolean);
                var stateGetter = Method(module, GridSystems, "get_IsDestroyed", false, GridBoolean);
                var stateSetter = Method(module, GridSystems, "set_IsDestroyed", false, GridVoid, GridBoolean);
                if (target?.IsPublic != true || target.HasBody != true || getter?.IsPublic != true
                    || getter.HasBody != true || setter?.HasBody != true || state?.HasBody != true
                    || systemsSetter?.HasBody != true || stateGetter?.HasBody != true || stateSetter?.HasBody != true)
                    throw new InvalidOperationException("Grid destruction method or state boundary changed.");

                var code = target.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                var set = GridDestructionCall(target, setter);
                if (code.Length < 5 || code[0].OpCode.Code != Code.Ldarg_0 || GridDestructionCall(target, getter) != code[1]
                    || GridDestructionBranch(code[2]) != code[code.Length - 1] || code[code.Length - 1].OpCode.Code != Code.Ret
                    || code.Count(instruction => instruction.OpCode.Code == Code.Ret) != 1
                    || code[3].OpCode.Code != Code.Ldarg_0 || set != code[4])
                    throw new InvalidOperationException("The already-destroyed guard or stock state transition changed.");

                var setState = GridDestructionCall(setter, state);
                var setSystems = GridDestructionCall(state, systemsSetter);
                var setObservedState = GridDestructionCall(systemsSetter, stateSetter);
                var getObservedState = GridDestructionCall(getter, stateGetter);
                if (setState?.Previous?.OpCode.Code is not (Code.Ldc_I4_1 or Code.Ldc_I4_S or Code.Ldc_I4)
                    || Constant(setState.Previous) != 1 || setSystems?.Previous?.OpCode.Code != Code.Ldarg_1
                    || setObservedState?.Previous?.OpCode.Code != Code.Ldarg_1
                    || getObservedState?.Next?.OpCode.Code != Code.Ret || !GridDestructionStateFieldMatches(stateGetter, stateSetter))
                    throw new InvalidOperationException("Stock grid destruction no longer sets the gameplay state to true.");

                var server = Method(module, "Player_NetworkSetup", "ServerDestroyEntity", false, GridVoid, GridParent);
                var rpc = Method(module, "Player_NetworkSetup", "RpcDestroyEntity", false, GridVoid, "VoxelEngine.VoxelEntityID");
                if (server?.HasBody != true || rpc == null)
                    throw new InvalidOperationException("The authoritative grid destruction entry point changed.");
                var serverCode = server.Body.Instructions;
                var serverGate = serverCode.FirstOrDefault(instruction => instruction.OpCode.Code != Code.Nop);
                var authority = serverGate?.Next == null ? null : GridDestructionBranch(serverGate.Next);
                var nativeCall = GridDestructionCall(server, target);
                var rpcCall = GridDestructionCall(server, rpc);
                if (serverGate?.OpCode.Code != Code.Call || serverGate.Operand is not MethodReference gate
                    || gate.DeclaringType.FullName != "Mirror.NetworkServer" || gate.Name != "get_active" || gate.HasThis
                    || gate.ReturnType.FullName != GridBoolean || gate.Parameters.Count != 0
                    || authority == null || !serverCode.Take(serverCode.IndexOf(authority)).Any(instruction => instruction.OpCode.Code == Code.Ret)
                    || nativeCall == null || rpcCall == null || serverCode.IndexOf(nativeCall) < serverCode.IndexOf(authority)
                    || serverCode.IndexOf(rpcCall) <= serverCode.IndexOf(nativeCall))
                    throw new InvalidOperationException("The active server guard or destruction-before-RPC order changed.");

                if (module.GetType(GridParent)?.BaseType?.FullName != GridEntity
                    || !Field(module, GridParent, "SGUID", "System.String", false)
                    || !Field(module, GridParent, "EntityMetaData", GridMetadata, false)
                    || !Field(module, GridMetadata, "isWorldWreck", GridBoolean, false)
                    || !Field(module, "World.StarData", "seed", "System.UInt64", false)
                    || Method(module, GridParent, "get_Identity", false, GridIdentity)?.IsPublic != true
                    || Method(module, GridIdentity, "get_IsNPC", false, GridBoolean)?.IsPublic != true
                    || Method(module, GridEntity, "get_EntityName", false, "System.String")?.IsPublic != true
                    || Method(module, GridEntity, "get_SkyTransform", false, "SkyTransform")?.IsPublic != true
                    || Method(module, "SkyTransform", "get_CurrentSystem", false, "World.StarData")?.IsPublic != true
                    || Method(module, GridEntity, "get_IsInitialized", false, GridBoolean)?.IsPublic != true
                    || Method(module, GridEntity, "get_WillBeUnspawned", false, GridBoolean)?.IsPublic != true)
                    throw new InvalidOperationException("Grid identity or live-grid snapshot members changed.");
                if (Method(module, "PlayerState", "get_sessionMode", true, "PlayerState/SessionMode")?.IsPublic != true
                    || module.GetType("PlayerState/SessionMode")?.Fields.SingleOrDefault(field => field.Name == "solo" && field.IsLiteral)?.Constant is not 1
                    || Method(module, "PlayerState", "get_InLoadingScreen", true, GridBoolean)?.IsPublic != true
                    || Method(module, "PlayerState", "get_IsQuittingToDesktopOrTitle", true, GridBoolean)?.IsPublic != true)
                    throw new InvalidOperationException("The solo session or world loading guards changed.");

                return new CapabilityResult(capability, true, "Authoritative stock grid destruction transition and read-only snapshot validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static Instruction? GridDestructionCall(MethodDefinition method, MethodDefinition target)
            => method.Body.Instructions.SingleOrDefault(instruction =>
                instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference callee
                && callee.FullName == target.FullName);

        private static bool GridDestructionStateFieldMatches(MethodDefinition getter, MethodDefinition setter)
        {
            var read = getter.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Ldfld);
            var write = setter.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode.Code == Code.Stfld);
            return read?.Operand is FieldReference field && field.DeclaringType.FullName == GridSystems
                && field.FieldType.FullName == GridBoolean && read.Next?.OpCode.Code == Code.Ret
                && write?.Operand is FieldReference destination && destination.FullName == field.FullName
                && write.Previous?.OpCode.Code == Code.Ldarg_1 && write.Next?.OpCode.Code == Code.Ret;
        }

        private static Instruction? GridDestructionBranch(Instruction instruction)
            => instruction.OpCode.Code is Code.Brtrue or Code.Brtrue_S ? instruction.Operand as Instruction : null;
    }
}
