using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateGridAttachments(ModuleDefinition module)
        {
            const string capability = "gridAttachments";
            const string magLock = "VoxelEngine.SystemMagLock";
            const string parent = "VoxelEngine.ParentEntity";
            const string rigidbody = "VoxelEngine.EntityRigidbody";
            const string entity = "VoxelEngine.VoxelEntity";
            const string boolean = "System.Boolean";
            const string @void = "System.Void";
            try
            {
                var data = module.GetType(magLock)?.Fields.SingleOrDefault(field =>
                    field.Name == "magLockData" && !field.IsStatic && field.FieldType.FullName == magLock + "/MagLockData");
                var target = module.GetType(magLock)?.Fields.SingleOrDefault(field =>
                    field.Name == "entityLocked" && !field.IsStatic && field.FieldType.FullName == parent);
                var activate = ContextMethod(module, magLock, "Activate", false, @void, boolean,
                    "Game.PlayerCommandManager", "System.Single");
                var canLock = ContextMethod(module, magLock, "CanLock", false, boolean, parent);
                var apply = ContextMethod(module, magLock, "SetLockedEntity", false, @void, boolean);
                var unlock = ContextMethod(module, magLock, "UnlockCurrent", false, @void);
                var destroy = ContextMethod(module, magLock, "OnDestroy", false, @void, "VoxelEngine.BrickUnspawnMode");
                var restore = ContextMethod(module, magLock, "OnSaveLoaded", false, @void, "System.Byte[]", boolean);
                var loaded = ContextMethod(module, magLock, "OnLoadingSequenceFinished", false, @void);
                var save = ContextMethod(module, magLock, "GetCustomData", false, "System.Byte[]", boolean);
                var edited = ContextMethod(module, "VoxelEngine.Command", "OnEdited", false, @void);
                var raycast = ContextMethod(module, "VoxelEngine.VoxelRaycast", "Raycast", false, boolean,
                    "UnityEngine.Ray", "VoxelEngine.VoxelRaycastHit&", "System.Single", parent, "System.Int32");
                var worldPosition = ContextMethod(module, "VoxelEngine.BrickCoord", "get_WorldPosition", false, "UnityEngine.Vector3");
                var worldUp = ContextMethod(module, "VoxelEngine.BrickCoord", "get_WorldUp", false, "UnityEngine.Vector3");
                var setGridId = ContextMethod(module, magLock + "/MagLockData", "set_entitySGUID", false, @void, "System.String");
                var parentOn = ContextMethod(module, rigidbody, "ParentOn", false, @void, entity,
                    "UnityEngine.Vector3", "UnityEngine.Quaternion");
                var unparent = ContextMethod(module, rigidbody, "UnParent", false, @void);
                var isParented = ContextMethod(module, rigidbody, "get_isParented", false, boolean);
                var mass = ContextMethod(module, rigidbody, "get_Mass", false, "System.Single");
                if (data == null || target == null || !activate.IsPublic || !raycast.IsPublic
                    || !ContextCalls(activate, raycast) || !ContextCalls(activate, canLock) || !ContextCalls(activate, edited)
                    || !ContextCalls(activate, worldPosition) || !ContextCalls(activate, worldUp)
                    || !ContextCalls(activate, setGridId)
                    || !UsesAttachmentField(activate, Code.Ldsfld, "VoxelEngine.VoxelPhysics", "BuildItem")
                    || !UsesAttachmentField(activate, Code.Ldfld, parent, "SGUID")
                    || !activate.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldc_R4
                        && instruction.Operand is float range && range == 8f)
                    || !ContextCalls(canLock, isParented) || !ContextCalls(canLock, mass)
                    || !UsesAttachmentField(canLock, Code.Ldfld, rigidbody, "dockedEntities")
                    || !UsesAttachmentField(activate, Code.Stfld, magLock, data.Name)
                    || !UsesAttachmentField(save, Code.Ldfld, magLock, data.Name)
                    || !UsesAttachmentField(restore, Code.Stfld, magLock, data.Name)
                    || !UsesAttachmentField(apply, Code.Stfld, magLock, target.Name)
                    || !UsesAttachmentField(apply, Code.Stfld, rigidbody, "parentedOnMagLock")
                    || !UsesAttachmentField(unlock, Code.Stfld, rigidbody, "parentedOnMagLock")
                    || !ContextCalls(apply, parentOn) || !ContextCalls(apply, unlock) || !ContextCalls(unlock, unparent)
                    || !ContextCalls(destroy, unlock) || !ContextCalls(restore, apply) || !ContextCalls(loaded, apply))
                    throw new InvalidOperationException("Native Mag-Lock acquisition, save or teardown changed.");

                return new CapabilityResult(capability, true, "Native Mag-Lock acquisition, save and teardown validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool UsesAttachmentField(MethodDefinition method, Code opcode, string type, string name)
            => method.HasBody && method.Body.Instructions.Any(instruction => instruction.OpCode.Code == opcode
                && instruction.Operand is FieldReference field && field.DeclaringType.FullName == type && field.Name == name);
    }
}
