using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class AttachmentDefaultPaintAuditTests
{
    internal static int Run()
    {
        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        int checks = 0;

        void Check(bool expected, string scenario)
        {
            var result = ContractAudit.EvaluateAttachmentDefaultPaint(module);
            if (result.Name != "attachmentDefaultPaint" || result.Available != expected)
                throw new InvalidOperationException(scenario + " failed: " + result.Reason);
            if (!expected && !ContractAudit.EvaluateAttachmentVisuals(module).Available)
                throw new InvalidOperationException(scenario + " disabled the attachment mesh contract.");
            checks++;
        }

        Check(true, "Target native default and preview paint");
        module.Mvid = Guid.NewGuid();
        Check(true, "Unrelated native assembly identity");

        var brick = module.GetType("VoxelEngine.Brick");
        var defaultPaint = brick.Fields.Single(field => field.Name == "defaultPaint");
        var previewPaint = brick.Fields.Single(field => field.Name == "PreviewPaint");
        previewPaint.IsInitOnly = true;
        try { Check(false, "Native preview paint becomes read-only"); }
        finally { previewPaint.IsInitOnly = false; }

        void ReplaceRead(string owner, string name, FieldDefinition source, FieldDefinition replacement, string scenario)
        {
            var read = module.GetType(owner).Methods.Single(method => method.Name == name).Body.Instructions
                .First(instruction => instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field && field.Name == source.Name);
            var operand = read.Operand;
            read.Operand = replacement;
            try { Check(false, scenario); }
            finally { read.Operand = operand; }
        }

        ReplaceRead("VoxelEngine.ActionBuild", "OnIsSelected", defaultPaint, previewPaint, "Fresh placement stops reading default paint");
        ReplaceRead("VoxelEngine.ActionPaint", "ColorBuildItem", defaultPaint, previewPaint, "Erase stops reading default paint");
        ReplaceRead("VoxelEngine.ItemPreview", "ComputeGeoItem", previewPaint, defaultPaint, "Item preview stops reading preview paint");
        return checks;
    }
}
