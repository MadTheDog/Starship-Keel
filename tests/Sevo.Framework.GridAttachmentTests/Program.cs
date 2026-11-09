using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateGridAttachments")
    ?? throw new Exception("Missing grid attachment contract audit.");

bool Available()
{
    var result = (CapabilityResult)audit.Invoke(null, new object[] { module })!;
    Console.WriteLine(result.Reason);
    return result.Available;
}

void Check(bool valid, string name)
{
    if (!valid) throw new Exception(name);
}

Check(Available(), "Native Mag-Lock acquisition, save and teardown admit grid attachment.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try { Check(Available(), "Assembly identity does not gate grid attachment."); }
finally { module.Mvid = originalMvid; }

void WithMissingCall(string type, string methodName, string callee)
{
    var method = module.GetType(type).Methods.Single(candidate => candidate.Name == methodName);
    var instruction = method.Body.Instructions.First(candidate => candidate.Operand is MethodReference target
        && target.Name == callee);
    var opcode = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { Check(!Available(), $"Missing {methodName} → {callee} disables the attachment hook."); }
    finally { instruction.OpCode = opcode; }
}

const string magLock = "VoxelEngine.SystemMagLock";
WithMissingCall(magLock, "Activate", "CanLock");
WithMissingCall(magLock, "CanLock", "get_isParented");
WithMissingCall(magLock, "SetLockedEntity", "ParentOn");
WithMissingCall(magLock, "UnlockCurrent", "UnParent");
WithMissingCall(magLock, "OnLoadingSequenceFinished", "SetLockedEntity");
Console.WriteLine("Grid attachment contract checks passed.");
