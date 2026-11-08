using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class OreGenerationTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks++;
        }

        var alpha = new OreGenerationRule("example.alpha:veins", OreGenerationScope.Planets, 0, 12f, 0.98f);
        var beta = new OreGenerationRule("example.beta:veins", OreGenerationScope.Asteroids, 0, 15f, null);
        var gamma = new OreGenerationRule("example.gamma:veins", OreGenerationScope.All, 1, null, 1f);
        var rules = new[] { gamma, beta, alpha };
        var sizes = new[] { 8f, 9f };
        var thresholds = new[] { 0.99f, 0.97f };
        var planets = OreGenerationPlan.Resolve(rules, OreGenerationScope.Planets, 2, _ => throw new Exception("Unexpected conflict."));
        Check(planets.TryApply(sizes, thresholds, out var weight), "Valid planet rules rejected.");
        Check(sizes.SequenceEqual(new[] { 12f, 9f }) && thresholds.SequenceEqual(new[] { 0.98f, 1f })
            && weight == 1f - thresholds[0], "Planet rule resolution or native weight sum is incorrect.");

        sizes = new[] { 8f, 9f };
        thresholds = new[] { 0.99f, 1.001f };
        var asteroids = OreGenerationPlan.Resolve(rules, OreGenerationScope.Asteroids, 2, _ => throw new Exception("Unexpected conflict."));
        Check(asteroids.TryApply(sizes, thresholds, out _) && sizes.SequenceEqual(new[] { 15f, 9f })
            && thresholds.SequenceEqual(new[] { 0.99f, 1f }), "Asteroid scope must apply only matching rules.");

        var competing = new OreGenerationRule("example.delta:veins", OreGenerationScope.All, 0, 90f, null);
        foreach (var order in new[] { new[] { competing, alpha, gamma }, new[] { gamma, alpha, competing } })
        {
            sizes = new[] { 8f, 9f };
            thresholds = new[] { 0.99f, 0.97f };
            var conflicts = new List<string>();
            var resolved = OreGenerationPlan.Resolve(order, OreGenerationScope.Planets, 2, conflicts.Add);
            Check(resolved.TryApply(sizes, thresholds, out _) && sizes[0] == 8f && thresholds[0] == 0.99f
                && thresholds[1] == 1f && conflicts.Single().Contains("example.alpha:veins, example.delta:veins"),
                "A contested slot must retain stock values regardless of load order.");
        }

        sizes = new[] { 8f, float.NaN };
        thresholds = new[] { 0.99f, 0.97f };
        Check(!planets.TryApply(sizes, thresholds, out _) && sizes[0] == 8f && thresholds[0] == 0.99f,
            "An invalid stock payload must fail without partial mutation.");
        sizes = new[] { 8f, 9f };
        thresholds = new[] { -1f, 0.97f };
        Check(!planets.TryApply(sizes, thresholds, out _) && sizes[0] == 8f && thresholds[0] == -1f,
            "An out-of-domain native threshold must fail without mutation.");
        foreach (var invalid in new[] { -1f, float.NaN, float.PositiveInfinity })
        {
            try { _ = new OreGenerationRule("example.alpha:bad", OreGenerationScope.All, 0, invalid, null); }
            catch (ArgumentOutOfRangeException) { checks++; continue; }
            throw new InvalidOperationException("Invalid size was accepted.");
        }

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool generation, bool scanner)
        {
            var ore = ContractAudit.EvaluateOreGeneration(module);
            var scan = ContractAudit.EvaluateOreScanner(module);
            Check(ore.Available == generation, "Ore generation contract: " + ore.Reason);
            Check(scan.Available == scanner, "Ore scanner contract: " + scan.Reason);
        }
        Audit(true, true);
        module.Mvid = Guid.NewGuid();
        Audit(true, true);
        var data = module.GetType("World.Terrain.LandformCommon/Lf_OreData");
        var threshold = data.Fields.Single(field => field.Name == "oreThreshold");
        var attribute = threshold.CustomAttributes.Single(value => value.AttributeType.Name == "FixedBufferAttribute");
        var priorLength = attribute.ConstructorArguments[1];
        attribute.ConstructorArguments[1] = new CustomAttributeArgument(priorLength.Type, 16);
        Audit(false, true);
        attribute.ConstructorArguments[1] = priorLength;
        var widget = module.GetType("World.Terrain.OreWidgetData").Fields.Single(field => field.Name == "texId");
        widget.FieldType = module.TypeSystem.Int16;
        Audit(true, false);

        var solo = module.GetType("PlayerState/SessionMode").Fields.Single(field => field.Name == "solo");
        var oldSolo = solo.Constant;
        solo.Constant = 100;
        Audit(false, false);
        solo.Constant = oldSolo;

        var terrain = ContractAudit.EvaluateTerrainMaterialVisuals(module);
        Check(terrain.Available, "Terrain material publication contract: " + terrain.Reason);
        var publish = module.GetType("World.WorldTerrainResources").Methods.Single(method => method.Name == "PushMaterialDatas");
        var setter = publish.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Call
            && instruction.Operand is MethodReference method && method.Name == "set_Item");
        var originalSetter = setter.Operand;
        setter.Operand = new MethodReference("ChangedSetter", module.TypeSystem.Void);
        Check(!ContractAudit.EvaluateTerrainMaterialVisuals(module).Available
            && ContractAudit.EvaluateOreGeneration(module).Available, "Terrain setter drift must disable only that capability.");
        setter.Operand = originalSetter;
        return checks;
    }
}
