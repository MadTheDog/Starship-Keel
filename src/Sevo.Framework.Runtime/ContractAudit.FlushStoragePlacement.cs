using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateFlushStoragePlacement(ModuleDefinition module)
        {
            const string capability = "flushStoragePlacement";
            const string build = "VoxelEngine.ActionBuild";
            const string physics = "VoxelEngine.VoxelPhysics";
            const string query = "Manager.SkyPhysics/QueryOverlapAabb";
            const string element = "VoxelEngine.BrickColliderBVH/Element";
            const string props = "VoxelEngine.BrickProperties";
            const string coord = "VoxelEngine.BrickCoord";
            const string builder = "NetworkedBuilder";
            const string action = "VoxelEngine.VoxelActionData";
            const string instance = "VoxelEngine.BrickInstanceData";
            const string boolean = "System.Boolean";
            const string integer = "System.Int32";
            const string @void = "System.Void";

            try
            {
                var preview = ContextMethod(module, build, "IsColliding", false, boolean);
                var overlap = ContextMethod(module, physics, "IsOverlapping", true, boolean, coord, boolean + "&", boolean);
                var execute = ContextMethod(module, query, "Execute", false, @void);
                var executeInternal = ContextMethod(module, query, "ExecuteInternal", false, @void);
                var collector = ContextMethod(module, query, "OnElementIntersect", false, @void, element, integer, boolean, integer);
                var predicate = ContextMethod(module, props, "IsOverlapValid", false, boolean, props, integer, integer);
                var rpc = ContextMethod(module, builder, "UserCode_RpcVoxelAction", false, @void, "System.Byte[]", boolean, integer);
                var apply = ContextMethod(module, builder, "DoAction", false, @void, action + "&", boolean, integer);
                var add = ContextMethod(module, "VoxelEngine.EntityBricks", "Add", false, integer, instance);
                var click = ContextMethod(module, build, "LeftClickUp", false, @void);
                var mirrorBuild = ContextMethod(module, "VoxelEngine.ActionSymmetry", "BuildBrick", false, @void, action, integer);
                var submit = ContextMethod(module, builder, "LocalVoxelAction", false, @void, action, boolean, integer);
                var canBuild = ContextMethod(module, build, "CanBuild", false, boolean);
                const string symmetryPlane = "VoxelEngine.SymmetryPlane";
                var symmetry = ContextMethod(module, "VoxelEngine.EntitySymmetry", "OnPlayerVoxelAction", false,
                    boolean, action + "&", symmetryPlane, symmetryPlane, symmetryPlane);
                var consume = ContextMethod(module, build, "ConsumeQuantityForBuild", true, boolean, instance, integer);
                var isSurvival = ContextMethod(module, "PlayerState", "get_IsSurvival", true, boolean);
                var tree = module.GetType("Common.BVHSet`1") ?? throw new InvalidOperationException("Missing native overlap traversal.");
                var overlapObb = tree.Methods.Single(method => method.Name == "OverlapOBB");
                var overlapAligned = tree.Methods.Single(method => method.Name == "OverlapAabbAligned");
                var initializeBricks = ContextMethod(module, "VoxelEngine.EntityBricks", "OnEntityInitialized", false, @void);
                var initializeBrick = ContextMethod(module, "VoxelEngine.Brick", "OnEntityInitialized", false, @void,
                    instance, "VoxelEngine.VoxelEntity", boolean);
                var initializePipe = ContextMethod(module, "VoxelEngine.BrickPipe", "OnEntityInitialized", false, @void,
                    instance, "VoxelEngine.VoxelEntity", boolean);
                var removePipe = ContextMethod(module, "VoxelEngine.BrickPipe", "OnRemove", false, @void, coord,
                    "VoxelEngine.BrickIds", "VoxelEngine.BrickUnspawnMode");

                if (!ContextCalls(preview, overlap) || !ContextCalls(overlap, execute) || !ContextCalls(execute, executeInternal)
                    || !ContextCalls(collector, predicate) || !HasFlushCall(overlapObb, "OnElementIntersect")
                    || !HasFlushCall(executeInternal, "OverlapAabbAligned") || !HasFlushCall(overlapAligned, "OnElementIntersect")
                    || !IsManagedFlushMethod(execute) || !IsManagedFlushMethod(executeInternal)
                    || !IsManagedFlushMethod(overlapObb) || !IsManagedFlushMethod(overlapAligned) || !IsManagedFlushMethod(collector)
                    || !ContextCalls(initializeBricks, initializeBrick) || !HasFlushCall(initializePipe, "OnBrickAdded")
                    || !HasFlushCall(removePipe, "OnBrickRemoved")
                    || !HasFlushField(predicate, "isTankJoin") || !HasFlushField(predicate, "hasTank")
                    || !HasFlushField(collector, "brickData") || !HasFlushField(collector, "transformId")
                    || !ContextCalls(rpc, apply) || !ContextCalls(apply, add)
                    || !ContextCalls(consume, isSurvival) || !FlushDebitPrecedesSubmit(click, consume, submit)
                    || !FlushDebitPrecedesSubmit(mirrorBuild, consume, submit)
                    || !ContextCalls(canBuild, preview)
                    || !canBuild.Body.Instructions.Any(instruction => IsFlushField(instruction, Code.Stsfld, build, "isColliding"))
                    || !click.Body.Instructions.Any(instruction => IsFlushField(instruction, Code.Ldsfld, build, "isColliding"))
                    || !HasFlushSymmetryGuard(symmetry))
                    throw new InvalidOperationException("Native creative placement, overlap or commit path changed.");

                return new CapabilityResult(capability, true, "Managed flush collision and creative commit paths validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool FlushDebitPrecedesSubmit(MethodDefinition method, MethodDefinition debit, MethodDefinition submit)
        {
            var calls = method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference target && (target.FullName == debit.FullName
                    || target.FullName == submit.FullName)).Select(instruction => ((MethodReference)instruction.Operand).FullName);
            return calls.SequenceEqual(new[] { debit.FullName, submit.FullName });
        }

        private static bool HasFlushSymmetryGuard(MethodDefinition method)
        {
            var guard = method.Body.Instructions.SingleOrDefault(instruction =>
                IsFlushField(instruction, Code.Ldfld, "VoxelEngine.Brick", "NoMirroring"));
            return guard?.Next?.OpCode.Code is Code.Brfalse or Code.Brfalse_S
                && guard.Next.Next?.OpCode.Code == Code.Ldc_I4_0 && guard.Next.Next.Next?.OpCode.Code == Code.Ret;
        }

        private static bool IsFlushField(Instruction instruction, Code code, string type, string name)
            => instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                && field.Name == name && field.DeclaringType.FullName == type;

        private static bool HasFlushCall(MethodDefinition method, string name)
            => method.HasBody && method.Body.Instructions.Any(instruction =>
                (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference called && called.Name == name);

        private static bool HasFlushField(MethodDefinition method, string name)
            => method.HasBody && method.Body.Instructions.Any(instruction =>
                instruction.OpCode.Code == Code.Ldfld && instruction.Operand is FieldReference field && field.Name == name);

        private static bool IsManagedFlushMethod(MethodDefinition method)
            => method.HasBody && (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL
                && !method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "BurstCompileAttribute")
                && !method.DeclaringType.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "BurstCompileAttribute");
    }
}
