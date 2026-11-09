using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

using var assembly = AssemblyDefinition.ReadAssembly(args.Single());
var module = assembly.MainModule;

void Require(bool expected, string label)
{
    var result = ContractAudit.EvaluateCameraView(module);
    if (result.Available != expected) throw new Exception(label + ": " + result.Reason);
}

Require(true, "Stock camera/action/restore contract");
module.Mvid = Guid.NewGuid();
Require(true, "Unrelated assembly identity");

void RejectMutation(string type, string method, Func<Instruction, bool> find, string label)
{
    var instruction = module.GetType(type).Methods.Single(member => member.Name == method).Body.Instructions.First(find);
    var saved = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { Require(false, label); }
    finally { instruction.OpCode = saved; }
}

RejectMutation("PlayerActions", "UpdateCommand",
    instruction => instruction.Operand is MethodReference called && called.Name == "ReleaseCapturedItemInputs",
    "Early return without releasing captured native controls");
RejectMutation("Game.PlayerCameraControl", "SetCameraParent",
    instruction => instruction.Operand is MethodReference called && called.Name == "ParentOn",
    "Native player anchor restoration removed");
RejectMutation("Game.PlayerCommandManager", "OnUpdate",
    instruction => instruction.Operand is MethodReference called && called.Name == "UpdateCommand",
    "Local input dispatch moved outside the audited path");
Require(true, "Restored native contract");
Console.WriteLine("Native camera view and input gate contracts passed.");
