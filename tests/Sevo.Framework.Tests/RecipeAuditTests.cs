using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class RecipeAuditTests
{
    public static void Run(ModuleDefinition game)
    {
        var audit = typeof(ContractAudit).GetMethod("EvaluateRecipes", new[] { typeof(ModuleDefinition) });
        if (audit == null) throw new Exception("The recipe adapter has no native contract audit.");

        CapabilityResult Evaluate() => (CapabilityResult)audit.Invoke(null, new object[] { game })!;
        var baseline = Evaluate();
        if (!baseline.Available) throw new Exception("Reference recipe contract rejected: " + baseline.Reason);

        var method = game.GetType("VoxelEngine.CraftingSchematic").Methods
            .Single(value => value.Name == "AllocateResources" && value.IsStatic);
        var copy = method.Body.Instructions.Single(value => value.Operand is MethodReference target
            && target.Name == "set_Item" && target.DeclaringType.FullName.Contains("NativeArray"));
        var original = copy.OpCode;
        try
        {
            copy.OpCode = OpCodes.Nop;
            if (Evaluate().Available)
                throw new Exception("Recipe capability accepted a native allocation without the schematic copy.");
        }
        finally { copy.OpCode = original; }

        var originalMvid = game.Mvid;
        try
        {
            game.Mvid = Guid.NewGuid();
            if (!Evaluate().Available)
                throw new Exception("Unrelated assembly identity rejected compatible recipe contracts.");
        }
        finally { game.Mvid = originalMvid; }
    }
}
