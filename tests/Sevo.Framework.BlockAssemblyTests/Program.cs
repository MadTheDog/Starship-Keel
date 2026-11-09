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

Check(Available("EvaluateBlockAssemblies"), "Live brick lookup is available");
var getter = module.GetType("VoxelEngine.EntityBricks").Methods.Single(method => method.Name == "GetBrickData");
var indexRead = getter.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Call &&
    instruction.Operand is MethodReference reference && reference.Name == "get_Item");
var original = indexRead.OpCode;
indexRead.OpCode = OpCodes.Nop;
try { Check(!Available("EvaluateBlockAssemblies"), "A missing indexed read must disable block assemblies"); }
finally { indexRead.OpCode = original; }

Check(Available("EvaluateShipyardAssemblyEligibility"), "Blueprint commit can be gated before stock charging");
var shipyard = module.GetType("VoxelEngine.CommandShipyard");
var spawn = shipyard.Methods.Single(method => method.Name == "SpawnBlueprintFromBlueprintSelection");
var stateMachine = ((TypeReference)spawn.CustomAttributes.Single(attribute =>
    attribute.AttributeType.Name == "AsyncStateMachineAttribute").ConstructorArguments[0].Value).Resolve();
var moveNext = stateMachine.Methods.Single(method => method.Name == "MoveNext");
var load = moveNext.Body.Instructions.Single(instruction => instruction.Operand is MethodReference reference &&
    reference.Name == "CmdLoadBlueprint");
original = load.OpCode;
load.OpCode = OpCodes.Nop;
try { Check(!Available("EvaluateShipyardAssemblyEligibility"), "A missing blueprint commit must disable the shipyard guard"); }
finally { load.OpCode = original; }

Console.WriteLine("Block assembly and shipyard capability checks passed.");
