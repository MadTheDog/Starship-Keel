using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var poseAudit = typeof(ContractAudit).GetMethod("EvaluateSeatedPoseTargets")
    ?? throw new Exception("Missing seated pose contract audit.");
var exitAudit = typeof(ContractAudit).GetMethod("EvaluateDismountPoses")
    ?? throw new Exception("Missing dismount pose contract audit.");

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

Check(Available(poseAudit), "Native seat and animator contracts admit foot targets.");
Check(Available(exitAudit), "Native seat exit and collision checks admit dismount targets.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try
{
    Check(Available(poseAudit) && Available(exitAudit), "Assembly identity does not gate seat hooks.");
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

WithMissingCall("VoxelEngine.EnterCommandSeat", "OnControl", "PositionCharacterBody", () =>
    Check(!Available(poseAudit) && Available(exitAudit), "Missing stock seat pose flow disables only foot targets."));
WithMissingCall("VoxelEngine.EnterCommandSeat", "OnPostExit", "set_newSystemPosition", () =>
    Check(!Available(exitAudit) && Available(poseAudit), "Missing stock dismount flow disables only dismount targets."));
WithMissingCall("VoxelEngine.VoxelPhysics", "OffsetCapsuleMove", "PushCapsule", () =>
    Check(!Available(exitAudit), "Missing native collision checks disable dismount targets."));
Console.WriteLine("Seat interaction contract checks passed.");
