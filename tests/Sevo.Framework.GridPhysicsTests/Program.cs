using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var assembly = AssemblyDefinition.ReadAssembly(args.Single());
var module = assembly.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateGridPhysics")
    ?? throw new Exception("Expected a dedicated native grid physics audit.");
CapabilityResult Result() => (CapabilityResult)audit.Invoke(null, new object[] { module })!;
void Require(bool available, string context)
{
    var result = Result();
    if (result.Available != available) throw new Exception(context + ": " + result.Reason);
}

Require(true, "Stock physics scheduler and force adapter");
module.Mvid = Guid.NewGuid();
Require(true, "Unrelated assembly identity");
RemoveCall("Manager.GameloopManager", "RunLateUpdateAfterMirror", "CompleteJob");
RemoveCall("Manager.SkyRigidbody/Handle", "AddForceAtSystemPosition", "set_Data");
Console.WriteLine("Grid physics native phase and force contracts passed.");

void RemoveCall(string type, string method, string name)
{
    var body = module.GetType(type).Methods.Single(candidate => candidate.Name == method).Body;
    var call = body.Instructions.Single(instruction => instruction.Operand is MethodReference reference && reference.Name == name);
    var opcode = call.OpCode;
    call.OpCode = OpCodes.Nop;
    try { Require(false, method + " no longer calls " + name); }
    finally { call.OpCode = opcode; }
}
