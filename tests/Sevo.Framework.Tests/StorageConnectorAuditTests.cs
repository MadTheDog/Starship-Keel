using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class StorageConnectorAuditTests
{
    internal static void Run(ModuleDefinition module, Action<bool, string> check)
    {
        var audit = typeof(ContractAudit).GetMethod("EvaluateStorageConnectors");
        check(audit != null, "Keel exposes an independent storage connector audit");
        bool Available() => ((Sevo.Framework.Sdk.CapabilityResult)audit!.Invoke(null, new object[] { module })!).Available;

        check(Available(), "Native pipe lifecycle and port lookup admit storage connectors");
        var originalMvid = module.Mvid;
        module.Mvid = Guid.NewGuid();
        try { check(Available(), "Assembly identity changes leave storage connectors available"); }
        finally { module.Mvid = originalMvid; }

        CheckMissingCall("VoxelEngine.BrickPipe", "OnRemove", "OnBrickRemoved");
        CheckMissingCall("VoxelEngine.BrickPipe", "OnEntityInitialized", "OnBrickAdded");
        CheckMissingCall("VoxelEngine.BrickPipe", "OnConstruct", "RegisterStorageData");
        CheckMissingCall("VoxelEngine.StorageConnectionLogic", "IsSystemTubeLinkValid", "GetPoints");

        CheckMissingCall("VoxelEngine.StorageAttachmentPoint", "GetPoint", "GetPoints");
        var tube = module.GetType("VoxelEngine.ItemStorageType").Fields.Single(field => field.Name == "tube");
        var originalValue = tube.Constant;
        tube.Constant = 255;
        try { check(!Available(), "Changed transport enum disables storage connectors"); }
        finally { tube.Constant = originalValue; }

        void CheckMissingCall(string type, string method, string target)
        {
            var caller = module.GetType(type).Methods.Single(candidate => candidate.Name == method);
            var call = caller.Body.Instructions.First(instruction =>
                instruction.Operand is MethodReference called && called.Name == target);
            var originalOpcode = call.OpCode;
            call.OpCode = OpCodes.Nop;
            try { check(!Available(), $"Missing {method} → {target} disables storage connectors"); }
            finally { call.OpCode = originalOpcode; }
        }
    }
}