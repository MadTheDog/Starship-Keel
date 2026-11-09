using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateStorageConnectors(ModuleDefinition module)
        {
            const string capability = "storageConnectors";
            const string pipe = "VoxelEngine.BrickPipe";
            const string points = "VoxelEngine.StorageAttachmentPoint";
            const string storage = "VoxelEngine.StorageConnectionLogic";
            const string type = "VoxelEngine.ItemStorageType";
            const string coord = "VoxelEngine.BrickCoord";
            const string ids = "VoxelEngine.BrickIds";
            const string extra = "System.Collections.Generic.Dictionary`2<System.Int32,System.Byte[]>";
            const string ports = "System.Collections.Generic.List`1<VoxelEngine.StorageAttachmentPoint>";
            try
            {
                var lookup = ConnectorMethod(module, points, "GetPoints", true, ports, "System.Int32", type);
                var construct = ConnectorMethod(module, pipe, "OnConstruct", false, ids, coord, "System.Boolean", extra);
                var remove = ConnectorMethod(module, pipe, "OnRemove", false, "System.Void", coord, ids, "VoxelEngine.BrickUnspawnMode");
                const string instance = "VoxelEngine.BrickInstanceData";
                const string entity = "VoxelEngine.VoxelEntity";
                var initialized = ConnectorMethod(module, pipe, "OnEntityInitialized", false, "System.Void",
                    instance, entity, "System.Boolean");
                var getter = ConnectorMethod(module, pipe, "GetItemStorageType", false, type, entity, "System.Int32");
                var constructor = ConnectorMethod(module, pipe, ".ctor", false, "System.Void",
                    "System.Int32", "System.String", "System.String", "VoxelEngine.ResizeMode", type, "System.Object[]");
                var add = ConnectorMethod(module, storage, "OnBrickAdded", true, "System.Void", "System.Int32", coord, type, "System.Boolean");
                var teardown = ConnectorMethod(module, storage, "OnBrickRemoved", true, "System.Void", "System.Int32", coord, "VoxelEngine.BrickUnspawnMode");
                var register = ConnectorMethod(module, "VoxelEngine.StorageResources", "RegisterStorageData", true,
                    "System.Int32", "VoxelEngine.StorageContainerData");
                var direct = ConnectorMethod(module, storage, "AreCompatible", true, "System.Boolean",
                    instance, type, instance, type, "System.Int32&", "System.Int32&");
                var system = ConnectorMethod(module, storage, "IsSystemTubeLinkValid", true, "System.Boolean",
                    instance, instance, "System.Int32&", type, type);
                var setConnector = ConnectorMethod(module, storage, "SetConnector", true, "System.Void",
                    instance, "System.Int32", "System.Int32", type, "System.Int32", type);
                var connectorBrick = ConnectorMethod(module, storage, "GetConnectorBrickData", true, instance,
                    instance, type, "System.Int32");
                var getPoint = ConnectorMethod(module, points, "GetPoint", true, points,
                    type, "System.Int32", instance);
                var compatible = ConnectorMethod(module, storage, "AreTypeCompatible", true, "System.Boolean", type, type);
                var itemStorage = ConnectorMethod(module, storage, "IsItemStorage", true, "System.Boolean", type);
                ConnectorMethod(module, storage, "IsTubeOrHose", true, "System.Boolean", type);
                RequireConnectorEnum(module, type, "tube", (int)VoxelEngine.ItemStorageType.tube);
                RequireConnectorEnum(module, type, "fuelPipe", (int)VoxelEngine.ItemStorageType.fuelPipe);
                RequireConnectorEnum(module, "VoxelEngine.DynData_Pixel", "none", (int)VoxelEngine.DynData_Pixel.none);
                RequireConnectorEnum(module, "VoxelEngine.DynData_Vert", "none", (int)VoxelEngine.DynData_Vert.none);
                RequireConnectorEnum(module, "VoxelEngine.BrickFunctionType", "itemTransport", (int)VoxelEngine.BrickFunctionType.itemTransport);
                RequireConnectorEnum(module, "VoxelEngine.BrickFunctionType", "fluidTransport", (int)VoxelEngine.BrickFunctionType.fluidTransport);
                var getterBody = getter.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                if (!lookup.IsPublic || !constructor.IsPublic || getterBody.Length != 3
                    || getterBody[0].OpCode.Code != Code.Ldarg_0 || getterBody[1].OpCode.Code != Code.Ldfld
                    || getterBody[1].Operand is not FieldReference field || field.DeclaringType.FullName != pipe
                    || field.FieldType.FullName != type || getterBody[2].OpCode.Code != Code.Ret
                    || !constructor.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Stfld
                        && instruction.Operand is FieldReference written && written.FullName == field.FullName)
                    || !ConnectorCalls(construct, lookup) || !ConnectorCalls(construct, register)
                    || !ConnectorCalls(remove, teardown) || !ConnectorCalls(initialized, add)
                    || !ConnectorCalls(add, direct) || !ConnectorCalls(direct, lookup) || !ConnectorCalls(system, lookup)
                    || !ConnectorCalls(add, setConnector) || !ConnectorCalls(setConnector, connectorBrick)
                    || !ConnectorCalls(connectorBrick, getPoint) || !ConnectorCalls(getPoint, lookup)
                    || !ConnectorCalls(add, compatible) || !ConnectorCalls(compatible, itemStorage, minimum: 2))
                    throw new InvalidOperationException("Native pipe construction, port lookup or graph lifecycle changed.");

                return new CapabilityResult(capability, true, "Native storage connector port lookup and lifecycle validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void RequireConnectorEnum(ModuleDefinition module, string type, string name, int value)
        {
            var field = module.GetType(type)?.Fields.SingleOrDefault(candidate => candidate.Name == name);
            if (field?.HasConstant != true || field.Constant == null || Convert.ToInt32(field.Constant) != value)
                throw new InvalidOperationException("Native connector enum value changed: " + type + "." + name);
        }

        private static MethodDefinition ConnectorMethod(ModuleDefinition module, string type, string name,
            bool isStatic, string returns, params string[] parameters)
            => module.GetType(type)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.HasBody && method.ReturnType.FullName == returns
                && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                ?? throw new InvalidOperationException("Missing native connector method: " + type + "." + name);

        private static bool ConnectorCalls(MethodDefinition caller, MethodDefinition target, int minimum = 1)
            => caller.HasBody && caller.Body.Instructions.Count(instruction =>
                (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference called && called.FullName == target.FullName) >= minimum;
    }
}
