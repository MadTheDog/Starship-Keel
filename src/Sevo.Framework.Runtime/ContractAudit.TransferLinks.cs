using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateTransferLinks(ModuleDefinition module)
        {
            const string capability = "transferLinks";
            const string hose = "VoxelEngine.CommandDataHose";
            const string command = "VoxelEngine.ICommand";
            const string connection = "VoxelEngine.StorageConnectionLogic";
            const string storage = "VoxelEngine.StorageResources";
            const string data = "VoxelEngine.StorageContainerData";
            const string type = "VoxelEngine.ItemStorageType";
            const string integer = "System.Int32", boolean = "System.Boolean", nothing = "System.Void";
            try
            {
                var add = Require(hose, "OnTargetAdded", false, nothing, command, boolean);
                var remove = Require(hose, "OnTargetRemoved", false, nothing, command, boolean);
                var destroy = Require(hose, "OnDelete", false, nothing, "VoxelEngine.BrickUnspawnMode");
                var link = Require(connection, "LinkPipe", true, boolean, type, integer, integer, integer);
                var unlink = Require(connection, "Unlink", true, nothing, integer, integer);
                var storageRemoved = Require(connection, "OnBrickRemoved", true, nothing,
                    integer, "VoxelEngine.BrickCoord", "VoxelEngine.BrickUnspawnMode");
                var set = Require(storage, "SetStorageData", true, nothing, data, integer);
                Require(storage, "GetStorageData", true, data, integer);
                Require(hose, "CanLinkToCommand", false, boolean, command);
                Require(hose, "CanBeLinkTargetFromCommand", false, boolean, command);
                Require("Manager.GameloopManager", "OnAfterRendering", true, nothing);
                Require("Manager.GameloopManager", "get_IsNativeContainerAccessUnsafe", true, boolean);
                if (!Uses(add, link) || !Uses(remove, unlink) || !Uses(destroy, storageRemoved)
                    || !Uses(link, set) || !Uses(unlink, set) || !Uses(storageRemoved, unlink)
                    || !module.GetType(data).Fields.Any(field => field.Name == "neighbors"
                        && field.CustomAttributes.Any(attribute =>
                            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.FixedBufferAttribute"
                            && attribute.ConstructorArguments.Count == 2
                            && attribute.ConstructorArguments[0].Value is TypeReference element && element.FullName == integer
                            && Convert.ToInt32(attribute.ConstructorArguments[1].Value) == 6)))
                    throw new InvalidOperationException("Native hose graph, fixed neighbor slots or deletion cleanup changed.");
                return new CapabilityResult(capability, true, "Native hose graph and safe frame cleanup validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }

            MethodDefinition Require(string owner, string name, bool isStatic, string returns, params string[] args)
                => Method(module, owner, name, isStatic, returns, args) is { HasBody: true } method
                    ? method : throw new InvalidOperationException("Missing native transfer method: " + owner + "." + name);
        }

        private static bool Uses(MethodDefinition method, MethodDefinition target)
            => method.Body.Instructions.Any(instruction =>
                (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference called && called.FullName == target.FullName);
    }
}
