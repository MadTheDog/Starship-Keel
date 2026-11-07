using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class CodexCategoryAuditTests
{
    internal static int Run()
    {
        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        var audit = typeof(ContractAudit).GetMethod("EvaluateCodexCategories", System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing optional Codex category audit.");
        int checks = 0;
        void Check(bool expected, string name, bool expectItems = true)
        {
            object[] arguments = { module, Array.Empty<string>() };
            var result = (CapabilityResult)audit.Invoke(null, arguments)!;
            if (result.Name != "codexCategories" || result.Available != expected)
                throw new InvalidOperationException(name + ": " + result.Reason);
            if (expected && arguments[1] is string[] menus && menus.Contains("TABS_BLOCKS") != expectItems)
                throw new InvalidOperationException(name + " has incorrect Items menu discovery.");
            if (!expected && !ContractAudit.EvaluateWeaponAttachments(module).Available)
                throw new InvalidOperationException(name + " unexpectedly disabled attachment placement.");
            checks++;
        }

        Check(true, "Native Weapons Codex");
        module.Mvid = Guid.NewGuid();
        Check(true, "Unrelated assembly identity");

        var awake = module.GetType("UI.UI_Codex").Methods.Single(method => method.Name == "Awake");
        var menu = awake.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldsfld
            && instruction.Operand is FieldReference field && field.Name == "TABS_BLOCKS");
        var originalMenu = menu.Operand;
        menu.Operand = module.GetType("PaintSelection").Fields.Single(field => field.Name == "TABS_MATERIALS");
        try { Check(true, "An unrelated menu remains available after the Items path changes", expectItems: false); }
        finally { menu.Operand = originalMenu; }

        var tab = module.GetType("VoxelEngine.Brick").Methods.Single(method => method.Name == "Tab");
        var categoryList = tab.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldfld
            && instruction.Operand is FieldReference field && field.Name == "items");
        var originalList = categoryList.Operand;
        categoryList.Operand = module.GetType("Item").Fields.Single(field => field.Name == "CodexItems");
        try { Check(false, "Native registration loses the category list"); }
        finally { categoryList.Operand = originalList; }

        return checks;
    }
}
