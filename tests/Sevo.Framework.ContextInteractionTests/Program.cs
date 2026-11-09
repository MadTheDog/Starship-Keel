using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var actionAudit = typeof(ContractAudit).GetMethod("EvaluateContextActions")
    ?? throw new Exception("Missing context action contract audit.");
var inventoryAudit = typeof(ContractAudit).GetMethod("EvaluateInventoryViews")
    ?? throw new Exception("Missing inventory view contract audit.");

bool Available(System.Reflection.MethodInfo audit)
{
    var result = (CapabilityResult)audit.Invoke(null, new object[] { module })!;
    Console.WriteLine(result.Name + ": " + result.Reason);
    return result.Available;
}

void Check(bool valid, string name)
{
    if (!valid) throw new Exception(name);
}

Check(Available(actionAudit), "Native aim and manual interaction admit context actions.");
Check(Available(inventoryAudit), "Native cargo storage window admits inventory views.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try
{
    Check(Available(actionAudit) && Available(inventoryAudit), "Assembly identity does not gate interaction hooks.");
}
finally { module.Mvid = originalMvid; }

void WithMissingCall(string type, string methodName, string callee, Action check)
{
    var method = module.GetType(type).Methods.Single(candidate => candidate.Name == methodName);
    var instruction = method.Body.Instructions.First(candidate => candidate.Operand is MethodReference target
        && target.Name == callee);
    var opcode = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { check(); }
    finally { instruction.OpCode = opcode; }
}

WithMissingCall("PlayerActions", "UpdateCommand", "TryActivate", () =>
    Check(!Available(actionAudit), "Missing stock targeting flow disables context actions."));
WithMissingCall("VoxelEngine.StorageCommandData", "OpenConfigWindow", "OpenConfig", () =>
    Check(!Available(inventoryAudit) && Available(actionAudit), "Changed cargo window disables only inventory views."));
Console.WriteLine("Context interaction contract checks passed.");
