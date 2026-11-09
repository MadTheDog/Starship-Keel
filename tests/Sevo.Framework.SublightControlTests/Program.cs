using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
var audit = typeof(ContractAudit).GetMethod("EvaluateSublightControl")
    ?? throw new Exception("Missing sublight control audit.");

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

bool Available() => ((CapabilityResult)audit.Invoke(null, new object[] { module })!).Available;
Check(Available(), "Native sublight and stock exit completion admit the lease.");
var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try { Check(Available(), "Assembly identity must not gate sublight control."); }
finally { module.Mvid = originalMvid; }

void RemoveCall(string type, string method, string call)
{
    var instruction = module.GetType(type).Methods.Single(item => item.Name == method).Body.Instructions
        .First(item => item.Operand is MethodReference target && target.Name == call);
    var saved = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { Check(!Available(), $"Missing native {method} → {call} must disable sublight control."); }
    finally { instruction.OpCode = saved; }
}
RemoveCall("SpaceshipDrive", "UpdateShipTransform", "ComputeAndApplyForce");
RemoveCall("SpaceshipDrive", "UpdateShipTransform", "ComputeAndApplyTorque");
RemoveCall("SpaceshipDrive", "ComputeAndApplyForce", "AddForce");
RemoveCall("SpaceshipDrive", "ComputeAndApplyTorque", "AddTorque");
RemoveCall("Network.NetworkedCommandActivator", "OnUpdate", "OnPostExit");
RemoveCall("VoxelEngine.ComputerBaseVehicle", "OnPostExit", "OnPostExit");
RemoveCall("Manager.GameloopManager", "RunLateUpdateAfterMirror", "UpdatePlayerCommands");

var policy = typeof(ContractAudit).Assembly.GetType("Sevo.Framework.Runtime.SublightInputPolicy")!;
var valid = policy.GetMethod("Axis", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
bool Axis(float x, float y, float z) => (bool)valid.Invoke(null, new object[] { x, y, z })!;
Check(Axis(0f, 0f, 0f) && Axis(-1f, .5f, 1f), "Neutral and bounded analog axes are accepted.");
Check(!Axis(float.NaN, 0, 0) && !Axis(0, float.PositiveInfinity, 0) && !Axis(0, 0, 1.01f),
    "Nonfinite or excessive controls are rejected.");
Console.WriteLine("Sublight control policy and native contract checks passed.");
