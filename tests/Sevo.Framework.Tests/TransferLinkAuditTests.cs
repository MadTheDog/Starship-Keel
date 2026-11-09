using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class TransferLinkAuditTests
{
    internal static void Run(ModuleDefinition module, Action<bool, string> check)
    {
        var method = typeof(ContractAudit).GetMethod("EvaluateTransferLinks");
        check(method != null, "Temporary transfer links have a native audit");
        bool Available() => ((CapabilityResult)method!.Invoke(null, new object[] { module })!).Available;
        check(Available(), "Native hose transport and teardown are available");
        var delete = module.GetType("VoxelEngine.CommandDataHose").Methods.Single(candidate => candidate.Name == "OnDelete");
        var remove = delete.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference target && target.Name == "OnBrickRemoved");
        var opcode = remove.OpCode;
        remove.OpCode = OpCodes.Nop;
        try { check(!Available(), "Missing native hose storage teardown disables temporary links"); }
        finally { remove.OpCode = opcode; }
    }
}
