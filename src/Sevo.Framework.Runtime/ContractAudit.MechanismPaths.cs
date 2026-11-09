using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
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
