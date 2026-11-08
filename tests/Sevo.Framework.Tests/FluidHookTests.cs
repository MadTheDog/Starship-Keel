using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class FluidHookTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }

        void Reject(Action action, string description)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException(description);
        }

        var fluid = new FluidDefinition("Coolant A", 0x33aaff, 1.2f, 0f);
        Check(fluid.DisplayName == "Coolant A" && fluid.Rgb == 0x33aaff && fluid.KilogramsPerLitre == 1.2f,
            "Fluid metadata did not round-trip.");
        Reject(() => new FluidDefinition(" ", 0, 1f, 0f), "Blank fluid names must be rejected.");
        Reject(() => new FluidDefinition("Coolant", 0x1000000, 1f, 0f), "Invalid RGB must be rejected.");
        Reject(() => new FluidDefinition("Coolant", 0, float.NaN, 0f), "Non-finite density must be rejected.");
        Reject(() => new FluidDefinition("Coolant", 0, 1f, float.PositiveInfinity), "Non-finite energy must be rejected.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool expected)
        {
            var result = ContractAudit.EvaluateFluids(module);
            Check(result.Name == "fluidHooks" && result.Available == expected, result.Reason);
        }
        Audit(true);
        module.Mvid = Guid.NewGuid();
        Audit(true);

        var afterRendering = module.GetType("Manager.GameloopManager").Methods.Single(method => method.Name == "OnAfterRendering");
        var dispose = afterRendering.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "DisposeJobs");
        var oldOp = dispose.OpCode;
        var oldOperand = dispose.Operand;
        dispose.OpCode = OpCodes.Nop;
        dispose.Operand = null;
        try { Audit(false); }
        finally { dispose.OpCode = oldOp; dispose.Operand = oldOperand; }

        var fluidId = module.GetType("VoxelEngine.ItemRequestData").Fields.Single(field => field.Name == "contextualDataId");
        fluidId.FieldType = module.TypeSystem.Int16;
        Audit(false);
        return checks;
    }
}
