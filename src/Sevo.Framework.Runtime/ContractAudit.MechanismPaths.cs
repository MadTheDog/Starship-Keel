using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStockRailTools(ModuleDefinition module)
        {
            const string capability = "stockRailTools";
            const string slider = "VoxelEngine.SliderSystem";
            const string addon = "VoxelEngine.AddonCommand";
            const string capsule = "Manager.SkyPhysics/QueryOverlapCapsuleCandidates";
            const string v = "System.Void";
            const string boolean = "System.Boolean";
            var basis = EvaluateConnectedRails(module);
            if (!basis.Available) return new CapabilityResult(capability, false, basis.Reason);
            try
            {
                var setup = ContextMethod(module, slider, "OnInitialize", false, v,
                    "VoxelEngine.BrickIds", boolean, "System.Object[]");
                var update = ContextMethod(module, slider, "OnAddonBlockUpdate", false, v, boolean);
                foreach (string field in new[] { "addonNeg", "addonPos" })
                    if (!UsesPathField(setup, Code.Stfld, slider, field, addon)
                        || !UsesPathField(update, Code.Ldfld, slider, field, addon))
                        throw new InvalidOperationException("Native slider add-on chain fields changed.");
                var stack = ContextMethod(module, "AddonBrick", "OnLinkedToCommand", false, v,
                    "VoxelEngine.BrickCoord", addon, boolean);
                if (!UsesPathField(stack, Code.Stfld, addon, "stackAddonCommand", addon)
                    || !PathCallsNamed(stack, "AddonBrick", "GetEndCommandVC"))
                    throw new InvalidOperationException("Native straight add-on chain publication changed.");

                foreach (string field in new[] { "SLIDING", "SLIDING_ADDON" })
                    if (module.GetType("VoxelEngine.TabMechanism")?.Fields.Any(candidate => candidate.IsPublic
                        && candidate.IsStatic && candidate.Name == field && candidate.FieldType.FullName == "VoxelEngine.Brick") != true)
                        throw new InvalidOperationException("Native sliding rail definitions changed.");

                var context = ContextMethod(module, "Manager.SkyPhysics", "get_CONTEXT", true, "Manager.SkyPhysics/QueryContext");
                var execute = ContextMethod(module, capsule, "Execute", false, v);
                var dispose = ContextMethod(module, capsule, "Dispose", false, v);
                var grid = ContextMethod(module, capsule, "OnElementIntersect", false, v,
                    "Manager.WorldCollider/GridElement", "System.Int32", boolean, "System.Int32");
                var world = ContextMethod(module, capsule, "OnElementIntersect", false, v,
                    "Manager.WorldCollider/Element", "System.Int32", boolean, "System.Int32");
                var child = ContextMethod(module, capsule, "OnElementIntersect", false, v,
                    "Manager.ChildrenEntityBVH/Element", "System.Int32", boolean, "System.Int32");
                var brick = ContextMethod(module, capsule, "OnElementIntersect", false, v,
                    "VoxelEngine.BrickColliderBVH/Element", "System.Int32", boolean, "System.Int32");
                var triangle = ContextMethod(module, capsule, "OnElementIntersect", false, v,
                    "World.TrianglesDataBVH/TrianglesElement", "System.Int32", boolean, "System.Int32");
                ContextMethod(module, capsule, ".ctor", false, v, "Vector3l", "System.Single",
                    "Unity.Collections.Allocator", "Manager.SkyPhysics/QueryContext", "System.Int32");
                ContextMethod(module, addon, "get_addon_bd", false, "VoxelEngine.BrickInstanceData");
                ContextMethod(module, addon, "get_totalStack", false, "System.Int32");
                ContextMethod(module, addon, "get_receiver", false, "VoxelEngine.IAddonReceiverSystem");
                ContextMethod(module, "VoxelEngine.EntityBricks", "GetBrickData", false,
                    "VoxelEngine.BrickInstanceData", "System.Int32");
                ContextMethod(module, "VoxelEngine.BrickInstanceData", "GetAabb", false, "Common.Aabb", boolean);
                ContextMethod(module, "Manager.SkyTransformData", "Transform", false, "Manager.SkyTransformData",
                    "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Single");
                ContextMethod(module, "Manager.GameloopManager", "get_IsMainThread", true, boolean);
                ContextMethod(module, "Manager.GameloopManager", "get_IsNativeContainerAccessUnsafe", true, boolean);
                if (!PathCallsNamed(context, "Manager.SkyPhysics", "BuildContext")
                    || new[] { execute, grid, world, child }.Any(method => !PathCallsNamed(method, null, "OverlapSphere"))
                    || !PathCallsNamed(world, null, "Add") || !PathCallsNamed(child, null, "Add")
                    || !PathCallsNamed(brick, null, "Add") || !PathCallsNamed(triangle, null, "Add")
                    || !PathCallsNamed(dispose, null, "Dispose")
                    || !WritesSweepList(brick, "candidateBricks") || !WritesSweepList(triangle, "candidateTriangles")
                    || !WritesSweepList(world, "candidateWorldItems") || !WritesSweepList(child, "candidateSkyColliders"))
                    throw new InvalidOperationException("Native rail sweep candidate enumeration changed.");
                return new CapabilityResult(capability, true, "Stock straight add-on chains and native collider candidate queries validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool PathCallsNamed(MethodDefinition source, string? declaring, string name)
            => source.HasBody && source.Body.Instructions.Any(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt
                && instruction.Operand is MethodReference method && method.Name == name
                && (declaring == null || method.DeclaringType.FullName == declaring));

        private static bool WritesSweepList(MethodDefinition source, string name)
            => source.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldflda
                && instruction.Operand is FieldReference field && field.Name == name
                && field.DeclaringType.FullName == source.DeclaringType.FullName);

        public static CapabilityResult EvaluateConnectedRails(ModuleDefinition module)
        {
            const string name = "connectedRails";
            const string slider = "VoxelEngine.SliderSystem";
            var basis = EvaluateMechanismPaths(module);
            if (!basis.Available) return new CapabilityResult(name, false, basis.Reason);
            var apply = Method(module, slider, "ApplyTransform", false, "System.Void");
            var instructions = apply!.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
            bool found = false;
            for (int index = 0; index + 6 < instructions.Length; index++)
            {
                if (instructions[index].Operand is not MethodReference grid || grid.FullName !=
                    "System.Single VoxelEngine.BrickInstanceData::get_GridSize()") continue;
                found |= instructions[index + 1].OpCode.Code == Code.Ldarg_0
                    && instructions[index + 2].OpCode.Code == Code.Ldfld
                    && instructions[index + 2].Operand is FieldReference negative
                    && negative.DeclaringType.FullName == slider && negative.Name == "addonCountNeg"
                    && instructions[index + 3].OpCode.Code == Code.Conv_R4
                    && instructions[index + 4].OpCode.Code == Code.Mul
                    && instructions[index + 5].OpCode.Code == Code.Sub
                    && instructions[index + 6].OpCode.Code == Code.Stfld
                    && instructions[index + 6].Operand is FieldReference distance
                    && distance.DeclaringType.FullName == slider && distance.Name == "distance";
            }
            return new CapabilityResult(name, found, found
                ? "Native slider negative extension distance and stock movement rollback validated."
                : "Native slider extension distance no longer subtracts the negative rail length.");
        }

        public static CapabilityResult EvaluateMechanismPaths(ModuleDefinition module)
        {
            const string capability = "mechanismPaths";
            const string slider = "VoxelEngine.SliderSystem";
            const string children = "VoxelEngine.ChildrenEntitySystem";
            const string v = "System.Void";
            const string single = "System.Single";
            try
            {
                var nativeApply = ContextMethod(module, children, "ApplyTransform", false, v);
                var sliderApply = ContextMethod(module, slider, "ApplyTransform", false, v);
                var nativeSpeed = ContextMethod(module, children, "GetMoveSpeed", false, single);
                var sliderSpeed = ContextMethod(module, slider, "GetMoveSpeed", false, single);
                var push = ContextMethod(module, children, "PushTransformToSkyTransform", false, v);
                var init = ContextMethod(module, children, "InitializeChildrenEntity", false, v);
                var spawn = ContextMethod(module, children, "CreateChildrenEntity", false, v);
                var load = ContextMethod(module, children, "OnSaveLoaded", false, v, "System.Byte[]", "System.Boolean");
                var sliderLoad = ContextMethod(module, slider, "OnSaveLoaded", false, v, "System.Byte[]", "System.Boolean");
                var update = ContextMethod(module, children, "UpdateTransform", false, v);
                var check = ContextMethod(module, children, "CheckForCollision", false, v);
                var restore = ContextMethod(module, children, "RestoreStateIfCollision", false, v);
                var stop = ContextMethod(module, children, "StopMovement", false, v);
                var instant = ContextMethod(module, children, "SetStateFromShipyard", false, v, single);
                var destroy = ContextMethod(module, slider, "OnDestroy", false, v, "VoxelEngine.BrickUnspawnMode");
                var state = ContextMethod(module, "VoxelEngine.CommandSystem", "set_tmpState", false, v, single);
                var addon = ContextMethod(module, slider, "OnAddonBlockUpdate", false, v, "System.Boolean");

                if (!sliderApply.IsVirtual || !sliderSpeed.IsVirtual || !destroy.IsPublic || !instant.IsPublic
                    || !ContextCalls(spawn, init) || !ContextCalls(load, init) || !ContextCalls(sliderLoad, load)
                    || !HasOrderedPathCalls(init, nativeApply, push)
                    || !HasOrderedPathCalls(update, check, restore, nativeSpeed, state, nativeApply, push)
                    || !HasOrderedPathCalls(restore, state, nativeApply, push, stop)
                    || !HasOrderedPathCalls(instant, state, nativeApply, push)
                    || !(HasOrderedPathCalls(addon, sliderApply, push) || HasOrderedPathCalls(addon, nativeApply, push))
                    || !UsesPathField(sliderApply, Code.Stfld, slider, "distance", single)
                    || !UsesPathField(sliderApply, Code.Ldfld, slider, "iniPosition", "UnityEngine.Vector3")
                    || !UsesPathField(sliderApply, Code.Ldfld, slider, "addonCountNeg", "System.Int32")
                    || !UsesPathField(addon, Code.Stfld, slider, "addonCountPlus", "System.Int32")
                    || !sliderApply.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Callvirt
                        && instruction.Operand is MethodReference setter
                        && setter.FullName == "System.Void UnityEngine.Transform::set_localPosition(UnityEngine.Vector3)"))
                    throw new InvalidOperationException("Native slider publication, movement or rollback contract changed.");

                return new CapabilityResult(capability, true, "Native slider publication, movement and rollback validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool HasOrderedPathCalls(MethodDefinition source, params MethodDefinition[] targets)
        {
            if (!source.HasBody) return false;
            int index = 0;
            foreach (var instruction in source.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                    || instruction.Operand is not MethodReference target || target.FullName != targets[index].FullName)
                    continue;
                if (++index == targets.Length) return true;
            }
            return false;
        }

        private static bool UsesPathField(MethodDefinition method, Code opcode, string declaring, string name, string fieldType)
            => method.HasBody && method.Body.Instructions.Any(instruction => instruction.OpCode.Code == opcode
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == declaring
                && field.Name == name && field.FieldType.FullName == fieldType);
    }
}
