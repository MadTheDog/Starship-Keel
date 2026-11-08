using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

using var game = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
var audit = typeof(ContractAudit).GetMethod("EvaluateGathering", new[] { typeof(ModuleDefinition) })
    ?? throw new InvalidOperationException("The gathering adapter has no native contract audit.");

CapabilityResult Evaluate() => (CapabilityResult)audit.Invoke(null, new object[] { game })!;

var baseline = Evaluate();
if (!baseline.Available)
    throw new InvalidOperationException("Reference gathering contract rejected: " + baseline.Reason);

game.Mvid = Guid.NewGuid();
if (!Evaluate().Available)
    throw new InvalidOperationException("An unrelated assembly identity rejected a compatible gathering contract.");

var onHit = game.GetType("Game.ItemWorldLogic").Methods.Single(method => method.Name == "OnHit");
var stateMachine = ((TypeReference)onHit.CustomAttributes.Single(attribute =>
    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute").ConstructorArguments[0].Value).Resolve();
var body = stateMachine.Methods.Single(method => method.Name == "MoveNext").Body.Instructions;

RejectMutation(body.Single(instruction => instruction.Operand is MethodReference method
    && method.Name == "ReleaseComponentsAndItemId"), "missing source release");
RejectMutation(body.Single(instruction => instruction.Operand is MethodReference method
    && method.DeclaringType.FullName == "UnityAsync.Await" && method.Name == "NextUpdate"), "missing asynchronous boundary");
RejectMutation(body.Single(instruction => instruction.OpCode == OpCodes.Ldfld
    && instruction.Operand is FieldReference field && field.Name == "healthPoint").Next, "missing lethal damage comparison");
var ordinal = body.Single(instruction => instruction.OpCode == OpCodes.Stfld
    && instruction.Operand is FieldReference field && field.FieldType.FullName == "System.UInt32"
    && instruction.Previous.OpCode == OpCodes.Ldc_I4_0);
RejectMutation(ordinal.Previous, "nonzero first drop ordinal");

void RejectMutation(Instruction instruction, string reason)
{
    var original = instruction.OpCode;
    try
    {
        instruction.OpCode = OpCodes.Ldc_I4_1;
        if (Evaluate().Available)
            throw new InvalidOperationException("Gathering audit accepted " + reason + ".");
    }
    finally
    {
        instruction.OpCode = original;
    }
}

Console.WriteLine("Gathering contract checks passed.");
