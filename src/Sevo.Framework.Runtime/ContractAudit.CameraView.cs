using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateCameraView(ModuleDefinition module)
        {
            const string capability = "cameraView";
            const string control = "Game.PlayerCameraControl";
            const string player = "Game.PlayerCommandManager";
            const string photo = "Game.PlayerPhotoMode";
            const string actions = "PlayerActions";
            const string menus = "Manager.PlayerMenuActions";
            const string transform = "UnityEngine.Transform";
            const string vector = "UnityEngine.Vector3";
            const string rotation = "UnityEngine.Quaternion";
            const string nothing = "System.Void";
            const string single = "System.Single";
            try
            {
                var drive = Require(player, "OnUpdate", false, nothing);
                var camera = Require(control, "OnUpdate", false, nothing, single);
                var parent = Require(control, "SetCameraParent", false, nothing);
                var pose = Require(control, "SetCameraTransform", false, nothing, single);
                var nativeParent = Require(control, "ParentCameraOn", false, nothing, transform, vector, rotation);
                var neutralParent = Require("TransformExtension", "ParentOn", true, nothing, transform, transform);
                var input = Require(actions, "UpdateCommand", false, nothing);
                var photoInput = Require(photo, "UpdatePhotoMode", false, "System.Boolean");
                var release = Require(actions, "ReleaseCapturedItemInputs", false, nothing);
                var menu = Require(menus, "OnUpdate", true, nothing);
                const string activator = "Network.NetworkedCommandActivator";
                if (Method(module, activator, "get_IsInEnterCommand", false, "System.Boolean")?.IsPublic != true
                    || !module.GetType(activator).Fields.Any(field => field.Name == "commandToEnter" && !field.IsStatic
                        && field.FieldType.FullName == "System.Collections.Generic.List`1<VoxelEngine.BrickCoord>"))
                    throw new InvalidOperationException("Stock command entry and its pending queue changed.");
                if (!camera.IsPublic || !nativeParent.IsPublic || !input.IsPublic || !photoInput.IsPublic || !menu.IsPublic
                    || !module.GetType(actions).Fields.Any(field => field.Name == "playerPhotoMode" && !field.IsStatic
                        && field.FieldType.FullName == photo)
                    || CallIndex(drive, camera) >= CallIndex(drive, input) || CallIndex(drive, input) >= CallIndex(drive, menu)
                    || CallIndex(camera, parent) >= CallIndex(camera, pose))
                    throw new InvalidOperationException("Stock local camera and action ordering changed.");

                var gate = input.Body.Instructions[CallIndex(input, photoInput)];
                if (gate.Previous?.Operand is not FieldReference photoField || photoField.Name != "playerPhotoMode"
                    || photoField.DeclaringType.FullName != actions || photoField.FieldType.FullName != photo
                    || gate.Previous.Previous?.OpCode.Code != Code.Ldarg_0
                    || !IsCall(gate.Previous.Previous.Previous, "VoxelEngine.ActionActivate", "CheckForExit")
                    || gate.Next?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || gate.Next.Operand != gate.Next.Next?.Next?.Next?.Next
                    || gate.Next.Next?.OpCode.Code != Code.Ldarg_0 || !Calls(gate.Next.Next.Next, release)
                    || gate.Next.Next.Next.Next?.OpCode.Code != Code.Ret
                    || !IsCallByName(input.Body.Instructions[CallIndex(input, photoInput) + 5], "PlayerState", "get_ViewLocked"))
                    throw new InvalidOperationException("Native photo input gate no longer releases and returns before world interaction.");

                var normalParent = parent.Body.Instructions[CallIndex(parent, neutralParent)];
                if (!LoadsField(normalParent.Previous, control, "playerTransform", transform)
                    || normalParent.Previous.Previous?.OpCode.Code != Code.Ldarg_0
                    || !LoadsField(normalParent.Previous.Previous.Previous, control, "anchorTransform", transform)
                    || normalParent.Next?.OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native ordinary camera no longer restores the physical player anchor.");

                var attach = nativeParent.Body.Instructions.Where(instruction =>
                    IsCallByName(instruction, transform, "SetParent")).ToArray();
                if (attach.Length != 2 || attach[0].Previous?.OpCode.Code != Code.Ldarg_1
                    || !LoadsField(attach[0].Previous.Previous, control, "anchorTransform", transform)
                    || !LoadsField(attach[1].Previous, control, "playerTransform", transform)
                    || !LoadsField(attach[1].Previous.Previous.Previous, control, "anchorTransform", transform)
                    || !nativeParent.Body.Instructions.Any(instruction => IsCallByName(instruction, "TransformExtension", "SetLocal"))
                    || !neutralParent.Body.Instructions.Any(instruction => IsCallByName(instruction, transform, "set_localPosition"))
                    || !neutralParent.Body.Instructions.Any(instruction => IsCallByName(instruction, transform, "set_localRotation"))
                    || !neutralParent.Body.Instructions.Any(instruction => IsCallByName(instruction, transform, "set_localScale"))
                    || !pose.Body.Instructions.Any(instruction => IsCallByName(instruction, transform, "set_localRotation")))
                    throw new InvalidOperationException("Stock attach or local camera pose restoration changed.");

                return new CapabilityResult(capability, true, "Validated local camera follow, action release, and native restoration paths.");

                MethodDefinition Require(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module, type, name, isStatic, result, parameters) is { HasBody: true } method ? method
                        : throw new InvalidOperationException("Missing native camera view seam " + type + "." + name);
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static int CallIndex(MethodDefinition caller, MethodDefinition callee)
        {
            var matches = caller.Body.Instructions.Select((instruction, index) => (instruction, index))
                .Where(pair => Calls(pair.instruction, callee)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Ambiguous camera path " + callee.FullName);
            return matches[0].index;
        }

        private static bool Calls(Instruction? instruction, MethodDefinition method)
            => instruction != null && (instruction.OpCode.Code is Code.Call or Code.Callvirt)
                && instruction.Operand is MethodReference called && called.FullName == method.FullName;

        private static bool IsCallByName(Instruction? instruction, string type, string name)
            => instruction != null && (instruction.OpCode.Code is Code.Call or Code.Callvirt)
                && instruction.Operand is MethodReference called && called.DeclaringType.FullName == type && called.Name == name;

        private static bool LoadsField(Instruction? instruction, string type, string name, string fieldType)
            => instruction != null && SurfaceField(instruction, Code.Ldfld, type, name, fieldType);
    }
}
