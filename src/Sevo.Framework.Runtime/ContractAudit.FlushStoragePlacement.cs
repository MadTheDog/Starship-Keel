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
                var consume = module.GetType(build)?.Methods.SingleOrDefault(method => method.Name == "ConsumeQuantityForBuild");
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
                    || consume == null || !ContextCalls(click, consume) || !ContextCalls(consume, isSurvival)
                    || !HasFlushCall(click, "LocalVoxelAction"))
                    throw new InvalidOperationException("Native creative placement, overlap or commit path changed.");

                return new CapabilityResult(capability, true, "Managed flush collision and creative commit paths validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

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
