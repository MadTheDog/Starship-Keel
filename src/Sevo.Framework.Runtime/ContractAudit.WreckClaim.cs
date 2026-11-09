using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateWreckClaim(ModuleDefinition module)
        {
            const string name = "wreckClaim";
            const string parent = "VoxelEngine.ParentEntity";
            const string manager = "BlockEntityManager";
            const string index = "Manager.Save.EntityInstanceSystem_mp";
            const string star = "World.StarData";
            const string text = "System.String";
            const string flag = "System.Boolean";
            const string none = "System.Void";
            try
            {
                var stage = ContextMethod(module, parent, "SaveToDiskAtPath", false, none, text, flag);
                var decode = ContextMethod(module, "VoxelEngine.BrickEntity_mp", "GetSaveFromFile", true,
                    "VoxelEngine.BrickEntity_mp", "System.IO.FileStream", flag);
                var metadata = ContextMethod(module, parent, "GetMetaData", true, "VoxelEngine.EntityMetaData", text);
                var remove = ContextMethod(module, manager, "RemoveAndDestroy", false, none, parent, flag);
                var destroy = ContextMethod(module, "BlockEntitySync", "OnDestroy", false, none);
                var unregister = ContextMethod(module, "Manager.NPCManager", "UnregisterNPC", true, none, parent);
                var load = ContextMethod(module, "Player_NetworkSetup", "ServerLoadBlueprint", false, parent,
                    "EntityInstanceData", "WorldTransform", star);
                var initialize = ContextMethod(module, "BlockEntitySync", "SetInstanceData", false, none, "EntityInstanceData");
                var shell = ContextMethod(module, manager, "CreateEntity", false, parent, "WorldTransform", star);
                var readIndex = ContextMethod(module, index, "LoadFromDisk", true, index, star, text, flag);
                var saveIndex = ContextMethod(module, index, "SaveSystemDataToDisk", true, none, index, star, text);
                var getBody = ContextMethod(module, index, "GetPlayerInstanceFullPath", true, text, text, text);
                var savePath = ContextMethod(module, index, "SavePath", true, text, star, text, flag);
                var loadSaved = ContextMethod(module, index, "LoadEntityAtPath", true, flag, text, "WorldTransform", star, parent + "&");
                var atomic = ContextMethod(module, "SaveTools", "WriteAtomically", true, none, text, "System.Byte[]");
                var map = ContextMethod(module, "Manager.EntitiesLocationMapping", "UpdateEntityLocation", true, none, text, star);
                var asyncType = remove.CustomAttributes.Single(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    .ConstructorArguments.Single().Value as TypeReference;
                var removal = asyncType?.Resolve().Methods.Single(method => method.Name == "MoveNext");

                if (!new[] { stage, decode, metadata, remove, load, readIndex, saveIndex, getBody, map }.All(method => method.IsPublic)
                    || !ContextCalls(stage, atomic) || !CallsClaimDependency(stage, parent, "GetMessagePack")
                    || !CallsClaimDependency(stage, parent, "GetBlueprintHeaderFullPath")
                    || removal == null || !CallsClaimDependency(removal, "VoxelEngine.VoxelEntity", "OnWillUnspawnEntity")
                    || !CallsClaimDependency(removal, "Mirror.NetworkServer", "Destroy")
                    || !CallsClaimDependency(removal, "Manager.EntitiesLocationMapping", "RemoveEntityLocation")
                    || !ContextCalls(destroy, unregister) || !ContextCalls(load, shell) || !ContextCalls(load, initialize)
                    || !ContextCalls(readIndex, savePath) || !ContextCalls(saveIndex, savePath) || !ContextCalls(saveIndex, atomic)
                    || !ContextCalls(loadSaved, metadata) || !ContextCalls(loadSaved, load)
                    || !new[] { "ownerPlayerNetID", "npcBehavior", "saveInWorld", "buildPermission" }.All(field =>
                        loadSaved.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                            && instruction.Operand is FieldReference member && member.DeclaringType.FullName == "EntityInstanceData"
                            && member.Name == field)))
                    throw new InvalidOperationException("Native wreck snapshot, retirement, reload or index publication changed.");

                return new CapabilityResult(name, true, "Validated native wreck snapshot, retirement and indexed reload contracts.");
            }
            catch (Exception exception) { return new CapabilityResult(name, false, exception.Message); }
        }

        private static bool CallsClaimDependency(MethodDefinition caller, string type, string name)
            => caller.HasBody && caller.Body.Instructions.Any(instruction =>
                (instruction.OpCode.Code is Code.Call or Code.Callvirt)
                && instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name);
    }
}
