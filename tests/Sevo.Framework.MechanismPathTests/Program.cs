using System.Numerics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateMechanismPaths")
    ?? throw new Exception("Missing mechanism path contract audit.");

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

Check(Available(), "Native slider motion and rollback admit path routing.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try { Check(Available(), "Assembly identity does not gate mechanism paths."); }
finally { module.Mvid = originalMvid; }

void WithMissingCall(string type, string methodName, string callee)
{
    var method = module.GetType(type).Methods.Single(candidate => candidate.Name == methodName);
    var instruction = method.Body.Instructions.First(candidate => candidate.Operand is MethodReference target
        && target.Name == callee);
    var opcode = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { Check(!Available(), $"Missing {methodName} → {callee} disables the path hook."); }
    finally { instruction.OpCode = opcode; }
}

WithMissingCall("VoxelEngine.ChildrenEntitySystem", "UpdateTransform", "PushTransformToSkyTransform");
WithMissingCall("VoxelEngine.ChildrenEntitySystem", "RestoreStateIfCollision", "ApplyTransform");
WithMissingCall("VoxelEngine.ChildrenEntitySystem", "InitializeChildrenEntity", "ApplyTransform");
WithMissingCall("VoxelEngine.ChildrenEntitySystem", "UpdateTransform", "GetMoveSpeed");

var pathType = typeof(ContractAudit).Assembly.GetType("Sevo.Framework.Runtime.MechanismPath", throwOnError: true)!;
var points = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX + Vector3.UnitZ };
var path = Activator.CreateInstance(pathType, new object[] { points })!;
var sample = pathType.GetMethod("TryGetOffset")!;
var step = pathType.GetMethod("LimitStep", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
points[1] = Vector3.UnitY;

Vector3 Sample(float distance)
{
    var parameters = new object[] { distance, Vector3.Zero };
    Check((bool)sample.Invoke(path, parameters)!, "Valid distance resolves a path point.");
    return (Vector3)parameters[1];
}

Check(Sample(0.5f) == new Vector3(0.5f, 0f, 0f), "Path snapshots caller-owned vertices.");
Check(Sample(1f) == Vector3.UnitX, "Corner lies on the first straight.");
Check(Sample(1.5f) == new Vector3(1f, 0f, 0.5f), "Second segment moves sideways.");
Check(!(bool)sample.Invoke(path, new object[] { float.NaN, Vector3.Zero })!, "Reject a nonfinite distance.");
Check(!(bool)sample.Invoke(path, new object[] { 2.5f, Vector3.Zero })!, "Reject distances beyond the path.");

float Step(float current, float target, float maximum)
    => (float)step.Invoke(path, new object[] { current, target, maximum })!;
Check(Math.Abs(Step(0.98f, 2f, 0.05f) - 0.02f) < 0.00001f, "Forward step stops at the corner.");
Check(Math.Abs(Step(1.03f, 0f, 0.05f) - 0.03f) < 0.00001f, "Reverse step stops at the corner.");
Check(Step(1f, 2f, 0.05f) == 0.05f, "Movement resumes from the corner.");
Console.WriteLine("Mechanism path contract and geometry checks passed.");
