using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class GridRecoveryAuditTests
{
    internal static void Run(ModuleDefinition module, Action<bool, string> check)
    {
        var audit = typeof(ContractAudit).GetMethod("EvaluateGridRecovery");
        check(audit != null, "Grid recovery has its own native audit");
        bool Available() => ((CapabilityResult)audit!.Invoke(null, new object[] { module })!).Available;
        check(Available(), "Native hull-gated player grid recovery is available");
        var recovery = module.GetType("VoxelEngine.ParentEntity").Methods.Single(method => method.Name == "TryRecoverFromWreck");
        var gate = recovery.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference target && target.Name == "get_CanRecoverFromWreck");
        var opcode = gate.OpCode;
        gate.OpCode = OpCodes.Nop;
        try { check(!Available(), "Removed native ownership/eligibility check disables recovery"); }
        finally { gate.OpCode = opcode; }
    }
}
