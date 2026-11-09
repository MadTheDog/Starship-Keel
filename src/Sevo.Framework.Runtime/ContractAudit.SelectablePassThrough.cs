using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateSelectablePassThrough(ModuleDefinition module)
        {
            const string id = "selectablePassThrough";
            const string brick = "VoxelEngine.Brick";
            const string props = "VoxelEngine.BrickProperties";
            const string collider = "VoxelEngine.BrickCollider";
            const string data = collider + "/Data";
            const string read = collider + "/ReadContext";
            const string shape = "VoxelEngine.SubShape";
            const string brickData = "VoxelEngine.BrickInstanceData";
            const string element = "VoxelEngine.BrickColliderBVH/Element";
            const string sky = "Manager.SkyPhysics";
            const string queryContext = sky + "/QueryContext";
            const string hit = "VoxelEngine.VoxelRaycastHit&";
            const string raycast = "VoxelEngine.VoxelRaycast";
            const string parent = "VoxelEngine.ParentEntity";
            const string ray = "UnityEngine.Ray";
            const string vec = "UnityEngine.Vector3";
            const string intType = "System.Int32";
            const string single = "System.Single";
            const string boolean = "System.Boolean";
            const string @void = "System.Void";
            try
            {
                var setBrick = ContextMethod(module, collider, "SetBrick", true, @void, brick);
                var addData = ContextMethod(module, collider, "AddColliderData", true, @void, intType, single, shape, intType,
                    boolean, boolean, boolean, brick);
                var propertyCtor = ContextMethod(module, props, ".ctor", false, @void, brick);
                var load = ContextMethod(module, brick, "LoadResourcesRuntime", false, @void);
                var shapeFlag = PassFieldInstruction(addData, Code.Stfld, data, "skipCollision");
                var propertyFlag = PassFieldInstruction(propertyCtor, Code.Stfld, props, "isCollisionOff");
                var offSource = PassFieldInstruction(setBrick, Code.Ldfld, brick, "IsCollisionOff");
                var colliderCalls = setBrick.Body.Instructions.Where(instruction => PassCall(instruction, addData)).ToArray();
                int skipLocal = PassStoreLocal(offSource?.Next?.Next);
                var propertyArray = PassFieldInstruction(load, Code.Ldsflda, brick, "BrickProperties");
                if (!Field(module, brick, "IsCollisionOff", boolean, false)
                    || !Field(module, props, "isCollisionOff", boolean, false)
                    || !Field(module, data, "skipCollision", boolean, false)
                    || offSource?.Next?.OpCode.Code != Code.Or || skipLocal < 0 || colliderCalls.Length < 1
                    || colliderCalls.Any(call => call.Previous?.OpCode.Code != Code.Ldarg_0
                        || PassLoadLocal(call.Previous.Previous) != skipLocal)
                    || shapeFlag?.Previous?.OpCode.Code != Code.Or
                    || !PassField(shapeFlag.Previous.Previous, Code.Ldfld, shape, "collisionsOff")
                    || !PassArgument(shapeFlag.Previous.Previous.Previous.Previous, addData.Parameters[6])
                    || !PassField(propertyFlag?.Previous, Code.Ldfld, brick, "IsCollisionOff")
                    || propertyFlag?.Previous?.Previous?.OpCode.Code != Code.Ldarg_1
                    || !ContextCalls(load, setBrick) || propertyArray == null
                    || !PassField(propertyArray.Next?.Next, Code.Ldfld, brick, "brickID")
                    || propertyArray.Next?.Next?.Next?.Next is not { OpCode.Code: Code.Newobj, Operand: MethodReference constructor }
                    || constructor.FullName != propertyCtor.FullName
                    || !PassArrayCall(propertyArray.Next.Next.Next.Next.Next, props, "set_Item"))
                    throw new InvalidOperationException("Native selectable collision-off definition or row publication changed.");

                var penetrate = ContextMethod(module, data, "PenetrationPoint", false, boolean, queryContext, brickData, vec, intType, single + "&");
                var projected = ContextMethod(module, data, "PenetrationPointProjected", false, boolean, queryContext, brickData,
                    vec, vec, sky + "/PenetrationData&", intType);
                var closest = ContextMethod(module, data, "ClosestPoint", false, "Unity.Mathematics.float3", queryContext, brickData,
                    "Unity.Mathematics.float3", intType, "Common.PhysicsQueryClosestPoint/FeatureType&");
                var sourceRay = ContextMethod(module, sky + "/QueryRaycastOnEntity", "OnElementIntersect", false, @void, element, single + "&");
                var sourceExecute = ContextMethod(module, sky + "/QueryRaycastOnEntity", "Execute", false, boolean, ray, single + "&", vec + "&");
                var pointJob = module.GetType("VoxelEngine.CollisionPointsBVHResources/ComputePointsJob").Methods
                    .Single(method => method.Name == "SearchForDepth");
                var capsule = ContextMethod(module, sky + "/QueryOverlapCapsuleCandidates", "OnElementIntersect",
                    false, @void, element, intType, boolean, intType);
                var mechanism = ContextMethod(module, "Manager.QueryPenetrationOnEntityResources/ExecuteQueryJob", "OnElementIntersect",
                    false, @void, element, intType, "VoxelEngine.CollisionPointsBVH/Element", intType);
                var closestFlag = PassFieldInstruction(closest, Code.Ldfld, data, "skipCollision");
                if (!PassFalseGuard(PassFieldInstruction(penetrate, Code.Ldfld, data, "skipCollision"), false)
                    || !PassFalseGuard(PassFieldInstruction(projected, Code.Ldfld, data, "skipCollision"), false)
                    || closestFlag?.Next?.Operand is not Instruction closestContinuation
                    || !PassBranchesFalse(closestFlag.Next)
                    || !closest.Body.Instructions.Skip(closest.Body.Instructions.IndexOf(closestFlag) + 2)
                        .TakeWhile(instruction => !ReferenceEquals(instruction, closestContinuation))
                        .Any(instruction => instruction.OpCode.Code == Code.Ldc_R4 && instruction.Operand is float distance && distance >= 1000000f)
                    || !PassFalseGuard(PassFieldInstruction(sourceRay, Code.Ldfld, props, "isCollisionOff"), true)
                    || pointJob.Body.Instructions.Count(instruction => PassCall(instruction, sourceExecute)) != 4
                    || !PassFalseGuard(PassFieldInstruction(capsule, Code.Ldfld, props, "isCollisionOff"), true)
                    || !ContextCalls(mechanism, penetrate) || PassFieldInstruction(mechanism, Code.Ldfld, data, "skipCollision") == null)
                    throw new InvalidOperationException("Native collision-off contact or source-point suppression changed.");

                var general = ContextMethod(module, sky + "/QueryRaycast", "OnElementIntersect", false, @void, element, single + "&");
                var generalExecute = ContextMethod(module, sky + "/QueryRaycast", "Execute", false, boolean, hit);
                var shapeRay = ContextMethod(module, data, "Raycast", false, boolean, ray, brickData, "Common.SkyRaycastHit&", queryContext);
                var shapeRayRead = ContextMethod(module, data, "Raycast", false, boolean, ray, brickData, "Common.SkyRaycastHit&", read);
                var rayBounds = ContextMethod(module, data, "GetBounds", false, @void, brickData, "Common.Aabb&", single + "&");
                var nativeRay = ContextMethod(module, raycast, "Raycast", false, boolean, ray, hit, single, parent, intType);
                var nativeVectorRay = ContextMethod(module, raycast, "Raycast", false, boolean, vec, vec, hit, single, parent, intType);
                var playerRay = ContextMethod(module, "PlayerActions", "RaycastFromPlayerPerspective", false, boolean, single, hit, boolean, boolean, boolean);
                if (!ContextCalls(playerRay, nativeRay) || !ContextCalls(nativeRay, nativeVectorRay) || !ContextCalls(nativeVectorRay, generalExecute)
                    || !ContextCalls(general, shapeRay) || !ContextCalls(shapeRay, shapeRayRead) || !ContextCalls(shapeRayRead, rayBounds)
                    || !PassCalls(shapeRayRead, "Common.PhysicsQueryRaycast", "RaycastColliderShape")
                    || new[] { general, shapeRay, shapeRayRead, rayBounds }.Any(method =>
                        PassFieldInstruction(method, Code.Ldfld, props, "isCollisionOff") != null
                        || PassFieldInstruction(method, Code.Ldfld, data, "skipCollision") != null))
                    throw new InvalidOperationException("Native player targeting no longer retains selectable collision-off geometry.");

                var overlap = ContextMethod(module, "VoxelEngine.VoxelPhysics", "IsOverlapping", true, boolean,
                    "VoxelEngine.BrickCoord", boolean + "&", boolean);
                var predicate = ContextMethod(module, props, "IsOverlapValid", false, boolean, props, intType, intType);
                var collector = ContextMethod(module, sky + "/QueryOverlapAabb", "OnElementIntersect", false, @void,
                    element, intType, boolean, intType);
                var geometric = ContextMethod(module, data, "IsOverlaping", true, boolean, brickData, intType, "Manager.SkyTransformData",
                    brickData, intType, "Manager.SkyTransformData", queryContext);
                var overlapBounds = ContextMethod(module, data, "GetBoundsAndSetTransform", true, "Common.Aabb",
                    brickData, intType, "Manager.SkyTransformData&", queryContext);
                var proposalFlag = PassFieldInstruction(overlap, Code.Ldfld, brick, "IsCollisionOff");
                var existingFlag = PassFieldInstruction(predicate, Code.Ldfld, props, "isCollisionOff");
                if (!PassFalseGuard(proposalFlag, false) || !PassCalls(overlap, sky + "/QueryOverlapAabb", "Execute")
                    || existingFlag?.Previous?.OpCode.Code != Code.Ldarg_1 || existingFlag.Next?.OpCode.Code != Code.Or
                    || !PassFalseGuard(existingFlag.Next, false)
                    || !PassCollector(collector, predicate, props, brickData, element, queryContext)
                    || !ContextCalls(collector, geometric)
                    || geometric.Body.Instructions.Count(instruction => PassCall(instruction, overlapBounds)) != 2
                    || !PassCalls(geometric, "Common.PhysicsQueryOverlap", "IsOverlapping")
                    || PassFieldInstruction(geometric, Code.Ldfld, data, "skipCollision") != null
                    || PassFieldInstruction(overlapBounds, Code.Ldfld, data, "skipCollision") != null)
                    throw new InvalidOperationException("Native placement bypass, argument shape or collider overlap changed.");

                return new CapabilityResult(id, true, "Validated stock collision-off publication, contact suppression, selection and placement gates.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(id, false, exception.Message);
            }
        }

        private static bool PassCollector(MethodDefinition collector, MethodDefinition predicate, string props, string brickData,
            string element, string queryContext)
        {
            string query = collector.DeclaringType.FullName;
            var body = collector.Body.Instructions;
            var call = body.SingleOrDefault(instruction => PassCall(instruction, predicate));
            int index = body.IndexOf(call);
            if (index < 24 || !PassBranchesFalse(call?.Next)
                || body[index - 7].OpCode.Code is not (Code.Ldloca or Code.Ldloca_S)
                || body[index - 7].Operand is not VariableDefinition proposed
                || body[index - 5].OpCode.Code != Code.Ldarg_0
                || !PassField(body[index - 4], Code.Ldfld, query, "transformId")
                || body[index - 3].OpCode.Code != Code.Ldarg_0
                || !PassField(body[index - 2], Code.Ldflda, query, "entityData")
                || !PassField(body[index - 1], Code.Ldfld, "VoxelEngine.BrickEntityResources/Data", "transformId"))
                return false;
            var other = body[index - 8];
            var proposedStore = body[index - 15];
            return PassStoreLocal(other) >= 0 && PassStoreLocal(other) == PassLoadLocal(body[index - 6])
                && PassStoreLocal(proposedStore) == proposed.Index
                && PassArrayCall(other.Previous, props, "get_Item") && PassArrayCall(proposedStore.Previous, props, "get_Item")
                && PassField(other.Previous?.Previous, Code.Ldfld, brickData, "brickId")
                && PassLoadLocal(other.Previous?.Previous?.Previous) == PassStoreLocal(body[2])
                && PassField(body[1], Code.Ldfld, element, "brickData") && body[0].OpCode.Code == Code.Ldarg_1
                && PassField(body[index - 12], Code.Ldflda, queryContext, "brickProperties")
                && PassField(body[index - 13], Code.Ldflda, query, "context") && body[index - 14].OpCode.Code == Code.Ldarg_0
                && PassField(body[index - 19], Code.Ldflda, queryContext, "brickProperties")
                && PassField(body[index - 20], Code.Ldflda, query, "context") && body[index - 21].OpCode.Code == Code.Ldarg_0
                && PassField(proposedStore.Previous?.Previous, Code.Ldfld, query, "brickId")
                && proposedStore.Previous?.Previous?.Previous?.OpCode.Code == Code.Ldarg_0;
        }

        private static bool PassFalseGuard(Instruction? read, bool returnsVoid)
        {
            var branch = read?.Next;
            if (!PassBranchesFalse(branch) || branch!.Operand is not Instruction continuation) return false;
            var first = branch.Next;
            var ret = returnsVoid ? first : first?.Next;
            return (returnsVoid || first?.OpCode.Code == Code.Ldc_I4_0)
                && ret?.OpCode.Code == Code.Ret && ReferenceEquals(ret.Next, continuation);
        }

        private static bool PassBranchesFalse(Instruction? instruction)
            => instruction?.OpCode.Code is Code.Brfalse or Code.Brfalse_S;

        private static Instruction? PassFieldInstruction(MethodDefinition method, Code code, string type, string name)
            => method.Body.Instructions.FirstOrDefault(instruction => PassField(instruction, code, type, name));

        private static bool PassField(Instruction? instruction, Code code, string type, string name)
            => instruction?.OpCode.Code == code && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == type && field.Name == name;

        private static bool PassCall(Instruction? instruction, MethodDefinition target)
            => instruction?.OpCode.Code is (Code.Call or Code.Callvirt) && instruction.Operand is MethodReference method
                && method.FullName == target.FullName;

        private static bool PassCalls(MethodDefinition method, string type, string name)
            => method.Body.Instructions.Any(instruction => instruction.OpCode.Code is (Code.Call or Code.Callvirt)
                && instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name);

        private static bool PassArrayCall(Instruction? instruction, string element, string name)
            => instruction?.OpCode.Code == Code.Call && instruction.Operand is MethodReference method && method.Name == name
                && method.DeclaringType.FullName == "Unity.Collections.NativeArray`1<" + element + ">";

        private static bool PassArgument(Instruction? instruction, ParameterDefinition parameter)
            => instruction?.OpCode.Code is (Code.Ldarg or Code.Ldarg_S) && ReferenceEquals(instruction.Operand, parameter);

        private static int PassStoreLocal(Instruction? instruction) => instruction?.OpCode.Code switch
        {
            Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
            Code.Stloc or Code.Stloc_S => ((VariableDefinition)instruction.Operand).Index, _ => -1
        };

        private static int PassLoadLocal(Instruction? instruction) => instruction?.OpCode.Code switch
        {
            Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3,
            Code.Ldloc or Code.Ldloc_S => ((VariableDefinition)instruction.Operand).Index, _ => -1
        };
    }
}
