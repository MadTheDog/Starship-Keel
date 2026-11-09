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
WithMissingCall(magLock, "Activate", "get_WorldUp");
WithMissingCall(magLock, "Activate", "set_entitySGUID");
WithMissingCall(magLock, "CanLock", "get_isParented");
WithMissingCall(magLock, "SetLockedEntity", "ParentOn");
WithMissingCall(magLock, "UnlockCurrent", "UnParent");
WithMissingCall(magLock, "OnLoadingSequenceFinished", "SetLockedEntity");
using var runtime = AssemblyDefinition.ReadAssembly(typeof(ContractAudit).Assembly.Location);
var service = runtime.MainModule.GetType("Sevo.Framework.Runtime.GridAttachmentService");
Check(service.Methods.Any(method => method.Name == "Enable"), "Mag-Lock setup enables the stock request service directly.");
Check(!service.Methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
    .Any(instruction => instruction.Operand is MethodReference target
        && target.DeclaringType.Namespace == "HarmonyLib"),
    "Mag-Lock setup and execution must not detour native methods.");
var attach = service.Methods.Single(method => method.Name == "TryRequestAttach");
Check(attach.Parameters[1].IsOut && attach.Parameters[1].ParameterType.FullName == "System.String&",
    "Acquisition reports the native recorded target without offering an explicit-target guarantee.");
Console.WriteLine("Grid attachment contract checks passed.");