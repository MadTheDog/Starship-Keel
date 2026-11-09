using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using Unity.Mathematics;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateFlushStoragePlacement")
    ?? throw new Exception("Missing flush placement contract audit.");

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

Check(Available(), "Native collector and build commit admit creative flush placement.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try { Check(Available(), "Assembly identity does not gate flush placement."); }
finally { module.Mvid = originalMvid; }

void WithMissingCall(string type, string methodName, string callee)
{
    var instruction = module.GetType(type).Methods.Where(candidate => candidate.Name == methodName)
        .SelectMany(candidate => candidate.Body.Instructions)
        .Single(candidate => candidate.Operand is MethodReference target && target.Name == callee);
    var opcode = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { Check(!Available(), $"Missing {methodName} → {callee} disables flush placement."); }
    finally { instruction.OpCode = opcode; }
}

WithMissingCall("VoxelEngine.ActionBuild", "IsColliding", "IsOverlapping");
WithMissingCall("Manager.SkyPhysics/QueryOverlapAabb", "OnElementIntersect", "IsOverlapValid");
WithMissingCall("NetworkedBuilder", "UserCode_RpcVoxelAction", "DoAction");
WithMissingCall("VoxelEngine.ActionBuild", "ConsumeQuantityForBuild", "get_IsSurvival");
WithMissingCall("VoxelEngine.ActionSymmetry", "BuildBrick", "ConsumeQuantityForBuild");
WithMissingCall("VoxelEngine.ActionBuild", "CanBuild", "IsColliding");
var geometry = typeof(ContractAudit).Assembly.GetType("Sevo.Framework.Runtime.FlushPortGeometry", throwOnError: true)!
    .GetMethod("IsAligned", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
bool Aligned(Vector3 point, Vector3 normal)
    => (bool)geometry.Invoke(null, new object[] { point, normal, new float3(0f), new float3(1f) })!;

Check(Aligned(new Vector3(0.5f, 0f, 0.5f), new Vector3(0f, 1f, 0f)), "Port accepts the inward positive face.");
Check(Aligned(new Vector3(1f, 0.5f, 0.5f), new Vector3(-1f, 0f, 0f)), "Port accepts the inward negative face.");
Check(!Aligned(new Vector3(0.5f, 0f, 0.5f), new Vector3(0f, -1f, 0f)), "Port rejects a normal pointing away from cargo.");
Check(!Aligned(new Vector3(0.5f, 0.05f, 0.5f), new Vector3(0f, 1f, 0f)), "Port rejects placement inside cargo.");
Check(!Aligned(new Vector3(0f, 0f, 0.5f), new Vector3(0f, 1f, 0f)), "Port rejects a cargo edge.");
Check(!Aligned(new Vector3(0.5f, 0f, 0.5f), new Vector3(0f, 0.8f, 0.6f)), "Port rejects non-cardinal alignment.");
Check(!Aligned(new Vector3(float.NaN, 0f, 0.5f), new Vector3(0f, 1f, 0f)), "Port rejects a nonfinite point.");
Console.WriteLine("Flush storage placement contract and geometry checks passed.");
