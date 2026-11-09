using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateContextActions(ModuleDefinition module)
        {
            const string capability = "contextActions";
            const string activator = "VoxelEngine.ActionActivate";
            const string hit = "VoxelEngine.VoxelRaycastHit";
            const string boolean = "System.Boolean";
            const string command = "VoxelEngine.ICommandManuallyActivable";
            const string player = "Game.PlayerCommandManager";
            try
            {
                var update = ContextMethod(module, "PlayerActions", "UpdateCommand", false, "System.Void");
                var activate = ContextMethod(module, activator, "TryActivate", false, boolean, hit);
                var canActivate = ContextMethod(module, activator, "get_CanActivate", false, boolean);
                var isOver = ContextMethod(module, activator, "IsOverCommand", false, boolean, hit);
                var manual = ContextMethod(module, command, "CanActivateManually", false, boolean, player);
                var brick = ContextMethod(module, "VoxelEngine.EntityBricks", "GetBrickData", false,
                    "VoxelEngine.BrickInstanceData", "System.Int32");
                if (!activate.IsPublic || !brick.IsPublic || !ContextCalls(update, activate)
                    || !ContextCalls(activate, isOver) || !ContextCalls(activate, canActivate)
                    || !ContextCalls(canActivate, manual)
                    || !isOver.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldsfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == activator
                        && field.Name == "CommandOver"))
                    throw new InvalidOperationException("Native crosshair interaction flow changed.");

                return new CapabilityResult(capability, true, "Native crosshair interaction flow validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        public static CapabilityResult EvaluateInventoryViews(ModuleDefinition module)
        {
            const string capability = "inventoryViews";
            const string storage = "VoxelEngine.StorageCommandData";
            const string ui = "UI.UI_CommandGeneric";
            const string @void = "System.Void";
            try
            {
                var onKeyUp = ContextMethod(module, storage, "OnKeyUp", false, @void, "Game.PlayerCommandManager");
                var openCargo = ContextMethod(module, storage, "OpenConfigWindow", false, @void);
                var openWindow = ContextMethod(module, ui, "OpenConfig", true, @void, ui + "/WindowsParams", "System.Boolean");
                var buildWindow = ContextMethod(module, ui, "BuildWindowContent", false, @void);
                var bindStorage = ContextMethod(module, ui, "SetStorageData", false, @void);
                var initialize = ContextMethod(module, "UI.UI_GenericStorage", "OnInitialize", false, @void);
                var instance = module.GetType(ui)?.Fields.SingleOrDefault(field => field.Name == "instance"
                    && field.IsStatic && field.FieldType.FullName == ui);
                if (instance == null || !onKeyUp.IsPublic || !ContextCalls(onKeyUp, openCargo) || !ContextCalls(openCargo, openWindow)
                    || !ContextCalls(buildWindow, bindStorage) || !ContextCalls(bindStorage, initialize)
                    || !openCargo.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == ui + "/WindowsParams"
                        && field.Name == "storageId" && field.FieldType.FullName == "System.Int32"))
                    throw new InvalidOperationException("Native cargo window flow changed.");

                return new CapabilityResult(capability, true, "Native cargo window flow validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static MethodDefinition ContextMethod(ModuleDefinition module, string type, string name,
            bool isStatic, string returns, params string[] parameters)
            => module.GetType(type)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.ReturnType.FullName == returns
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException("Missing native interaction method: " + type + "." + name);

        private static bool ContextCalls(MethodDefinition caller, MethodDefinition target)
            => caller.HasBody && caller.Body.Instructions.Any(instruction =>
                (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference called && called.FullName == target.FullName);
    }
}
