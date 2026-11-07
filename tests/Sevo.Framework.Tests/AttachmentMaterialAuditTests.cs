using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class AttachmentMaterialAuditTests
{
    internal static int Run()
    {
        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        int checks = 0;

        void Check(bool expected, string scenario, bool requireStockVisuals = true)
        {
            var result = ContractAudit.EvaluateAttachmentMaterials(module);
            if (result.Name != "attachmentMaterials" || result.Available != expected)
                throw new InvalidOperationException(scenario + " failed: " + result.Reason);
            if (!expected && requireStockVisuals && !ContractAudit.EvaluateAttachmentVisuals(module).Available)
                throw new InvalidOperationException(scenario + " disabled stock-backed custom visuals.");
            checks++;
        }

        MethodDefinition FindMethod(string type, string name) =>
            module.GetType(type).Methods.Single(method => method.Name == name);

        void ChangeLiteral(MethodDefinition method, string literal)
        {
            var instruction = method.Body.Instructions.Single(value => value.OpCode == OpCodes.Ldstr && Equals(value.Operand, literal));
            instruction.Operand = "Changed_" + literal;
            try { Check(false, "Changed " + literal + " binding"); }
            finally { instruction.Operand = literal; }
        }

        Check(true, "Target native material contracts");
        module.Mvid = Guid.NewGuid();
        Check(true, "Unrelated native material assembly identity");
        checks += CheckAttachmentPaint(module);

        var awake = FindMethod("ResourcesLoader", "Awake");
        ChangeLiteral(awake, "MAIN_TEX");
        ChangeLiteral(awake, "SPECULAR_TEX");
        var upload = FindMethod("VoxelEngine.VoxelMaterialResources", "LoadBuffers");
        ChangeLiteral(upload, "MaterialParams");
        ChangeLiteral(upload, "TEXTURE_RES");

        var stockLoad = awake.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "LoadAllResourcesRuntime");
        var nativeAllocate = awake.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "Manager.GameResourcesManager" && method.Name == "AllocateResources");
        var processor = awake.Body.GetILProcessor();
        var skipAllocation = processor.Create(OpCodes.Br, nativeAllocate.Next);
        processor.InsertAfter(stockLoad, skipAllocation);
        try { Check(false, "Initial stock load bypasses native allocation", requireStockVisuals: false); }
        finally { processor.Remove(skipAllocation); }

        var material = module.GetType("VoxelEngine.VoxelMaterial");
        var field = material.Fields.Single(value => value.Name == "specularID");
        field.Name = "ChangedSpecular";
        try { Check(false, "Changed native material row layout"); }
        finally { field.Name = "specularID"; }

        var noPaint = module.GetType("VoxelEngine.PaintMode").Fields.Single(value => value.Name == "noPaint");
        var savedPaint = noPaint.Constant;
        noPaint.Constant = 0;
        try { Check(false, "Changed custom palette paint value"); }
        finally { noPaint.Constant = savedPaint; }

        var seed = FindMethod("VoxelEngine.VoxelMaterialResources", "SetMaterialParamDatas");
        var reset = seed.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_0);
        reset.OpCode = OpCodes.Ldc_I4_1;
        try { Check(false, "Changed material cursor initialization"); }
        finally { reset.OpCode = OpCodes.Ldc_I4_0; }

        var reload = FindMethod("VoxelEngine.VoxelMaterialResources", "OnGameReload");
        var call = reload.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Call);
        var savedCall = call.Operand;
        call.Operand = upload;
        try { Check(false, "Reload replaces material buffer contents"); }
        finally { call.Operand = savedCall; }

        return checks;
    }

    private static int CheckAttachmentPaint(ModuleDefinition module)
    {
        var audit = typeof(ContractAudit).GetMethod("EvaluateAttachmentPaint")
            ?? throw new InvalidOperationException("Missing optional attachment paint audit.");
        void Check(bool expected, string scenario)
        {
            var result = (CapabilityResult)audit.Invoke(null, new object[] { module })!;
            if (result.Name != "attachmentPaint" || result.Available != expected)
                throw new InvalidOperationException(scenario + " failed: " + result.Reason);
            if (!ContractAudit.EvaluateAttachmentMaterials(module).Available)
                throw new InvalidOperationException(scenario + " disabled the fixed material palette path.");
        }

        Check(true, "Native paint contract after an unrelated assembly identity change");
        var paintMode = module.GetType("VoxelEngine.PaintMode").Fields.Single(field => field.Name == "on");
        var value = paintMode.Constant;
        paintMode.Constant = 5;
        try { Check(false, "Native paint mode change"); }
        finally { paintMode.Constant = value; }

        var tool = module.GetType("VoxelEngine.ActionPaint").Methods.Single(method => method.Name == "ColorBuildItem");
        var readGate = tool.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldfld
            && instruction.Operand is FieldReference field && field.Name == "canPaint");
        var gate = readGate.Operand;
        readGate.Operand = module.GetType("VoxelEngine.Brick").Fields.Single(field => field.Name == "hasGlowPaint");
        try { Check(false, "Native paint tool gate change"); }
        finally { readGate.Operand = gate; }

        var upload = module.GetType("VoxelEngine.VoxelMaterialResources").Methods.Single(method => method.Name == "LoadBuffers");
        var binding = upload.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, "PaintData"));
        binding.Operand = "ChangedPaintData";
        try { Check(false, "Native paint palette shader binding change"); }
        finally { binding.Operand = "PaintData"; }
        return 4;
    }
}
