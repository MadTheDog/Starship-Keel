using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(!VehicleAssistPolicy.IsAxis(float.NaN) && !VehicleAssistPolicy.IsAxis(float.PositiveInfinity)
    && !VehicleAssistPolicy.IsAxis(-1.1f) && VehicleAssistPolicy.IsAxis(1f), "Axes must be finite and normalized");
Check(VehicleAssistPolicy.CanApply(false, 20, 20) && VehicleAssistPolicy.CanApply(false, 20, 21),
    "A current manual-branch request should be eligible");
Check(!VehicleAssistPolicy.CanApply(true, 20, 20) && !VehicleAssistPolicy.CanApply(false, 20, 22)
    && !VehicleAssistPolicy.CanApply(false, 20, 19), "Stock input and expiry must win");
Check(!VehicleAssistPolicy.ContinueApproach(float.NaN, 100f) && !VehicleAssistPolicy.ContinueApproach(99f, 100f)
    && VehicleAssistPolicy.ContinueApproach(101f, 100f), "Approach must end at the stop boundary");

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;
Check(Audit("EvaluateVehicleControlProvider") && Audit("EvaluateWaypointFlight"), "Vehicle capabilities unavailable");
RemoveCall("Spaceship", "Control", "FromPlayerInput", () =>
{
    Check(!Audit("EvaluateVehicleControlProvider"), "Changed manual branch must disable vehicle input");
    Check(Audit("EvaluateWaypointFlight"), "Manual branch incompatibility must preserve waypoint flight");
});
RemoveCall("Game.SpaceshipAutoPilot", "CancelCurrentMode", "Reset", () =>
{
    Check(!Audit("EvaluateWaypointFlight"), "Changed native cancellation must disable waypoint flight");
    Check(Audit("EvaluateVehicleControlProvider"), "Waypoint incompatibility must preserve vehicle input");
});
Console.WriteLine("Vehicle input precedence, expiry, and native waypoint capability checks passed.");

bool Audit(string name)
{
    var method = typeof(ContractAudit).GetMethod(name);
    Check(method != null, "Missing capability audit " + name);
    var result = (CapabilityResult)method!.Invoke(null, new object[] { module })!;
    if (!result.Available) Console.WriteLine(result.Reason);
    return result.Available;
}

void RemoveCall(string type, string method, string target, Action test)
{
    var instruction = module.GetType(type).Methods.Single(value => value.Name == method).Body.Instructions
        .Single(value => value.OpCode.Code is Code.Call or Code.Callvirt && value.Operand is MethodReference call
            && call.Name == target);
    var original = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { test(); }
    finally { instruction.OpCode = original; }
}
