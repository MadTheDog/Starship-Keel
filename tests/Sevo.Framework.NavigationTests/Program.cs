using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = AssemblyDefinition.ReadAssembly(args.Single());
var module = game.MainModule;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

bool Available(string methodName)
{
    var method = typeof(ContractAudit).GetMethod(methodName);
    Check(method != null, "Missing capability audit " + methodName);
    var result = (CapabilityResult)method!.Invoke(null, new object[] { module })!;
    if (!result.Available) Console.WriteLine(result.Reason);
    return result.Available;
}

Check(Available("EvaluateNavigationMarkers"), "The stock HUD waypoint widget is available");
Check(Available("EvaluateNavigationTargets"), "The native target selection route is available");

var originalMvid = module.Mvid;
module.Mvid = Guid.NewGuid();
try
{
    Check(Available("EvaluateNavigationMarkers") && Available("EvaluateNavigationTargets"),
        "A build identity change must not block navigation hooks");
}
finally { module.Mvid = originalMvid; }

RemoveCall("UI.UI_WaypointWidget", "OnUpdate", "get_WorldPosition", () =>
{
    Check(!Available("EvaluateNavigationMarkers"), "Changed widget coordinates must disable markers");
    Check(Available("EvaluateNavigationTargets"), "Marker incompatibility must preserve targets");
});
RemoveCall("UI.HUD_TargetManager", "OnNewHolomapTarget", "SetCurrentSelected", () =>
{
    Check(!Available("EvaluateNavigationTargets"), "Changed stock target routing must disable selection");
    Check(Available("EvaluateNavigationMarkers"), "Target incompatibility must preserve markers");
});
Console.WriteLine("Navigation marker and target capability checks passed.");

void RemoveCall(string type, string method, string target, Action action)
{
    var instruction = module.GetType(type).Methods.Single(value => value.Name == method).Body.Instructions
        .Single(value => value.OpCode.Code is Code.Call or Code.Callvirt && value.Operand is MethodReference call
            && call.Name == target);
    var original = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    try { action(); }
    finally { instruction.OpCode = original; }
}
