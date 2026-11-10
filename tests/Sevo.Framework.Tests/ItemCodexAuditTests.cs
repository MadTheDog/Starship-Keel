using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class ItemCodexAuditTests
{
    internal static int Run()
    {
        using var native = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        int checks = 0;

        void Assert(bool expected, string name)
        {
            var result = ContractAudit.EvaluateItemCodex(native);
            if (result.Available != expected)
                throw new InvalidOperationException(name + ": " + result.Reason);
            if (!expected && !ContractAudit.EvaluateItems(native).Available)
                throw new InvalidOperationException("A Codex-only change disabled native item registration.");
            checks++;
        }

        Assert(true, "Native item Codex path");
        native.Mvid = Guid.NewGuid();
        Assert(true, "Unrelated assembly identity");

        var click = native.GetType("ItemSlot").Methods.Single(method => method.Name == "OnClick");
        var invoke = click.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "Invoke");
        var after = invoke.Next;
        var original = after.OpCode;
        after.OpCode = OpCodes.Nop;
        try { Assert(false, "Slot click continues after the browse callback"); }
        finally { after.OpCode = original; }

        var awake = native.GetType("UI.UI_Codex").Methods.Single(method => method.Name == "Awake");
        var bricks = awake.Body.Instructions.Single(instruction => instruction.Operand is FieldReference field
            && field.Name == "TABS_BLOCKS");
        var fieldReference = bricks.Operand;
        bricks.Operand = native.GetType("UI.UI_Codex").Fields.Single(field => field.Name == "TABS_TOOLS");
        try { Assert(false, "Native Bricks layout is absent"); }
        finally { bricks.Operand = fieldReference; }

        var drag = native.GetType("ItemSlot").Methods.Single(method => method.Name == "StartDraggingItem");
        var duplicate = drag.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "DuplicateItem");
        var duplicateOpcode = duplicate.OpCode;
        duplicate.OpCode = OpCodes.Nop;
        try { Assert(false, "Native Creative drag loses frozen item duplication"); }
        finally { duplicate.OpCode = duplicateOpcode; }
        return checks;
    }
}
