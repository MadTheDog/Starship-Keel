using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class BodyEnvironmentTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks++;
        }

        var target = new BodyEnvironmentTarget(123, 456);
        var other = new BodyEnvironmentTarget(123, 567);
        var plan = BodyEnvironmentPlan.Resolve(new[]
        {
            new BodyEnvironmentRule("example.z:water", target, new BodyEnvironmentOverride(waterAmount: 0.6f)),
            new BodyEnvironmentRule("example.a:water", target, new BodyEnvironmentOverride(waterAmount: 0.5f)),
            new BodyEnvironmentRule("example.c:air", target, new BodyEnvironmentOverride(atmosphereDensity: 0.2f)),
            new BodyEnvironmentRule("example.b:other", other, new BodyEnvironmentOverride(gravityScale: 0f, atmosphereRgb: 0x112233)),
        }, _ => { });
        Check(plan[target].WaterAmount == null && plan[target].AtmosphereDensity == 0.2f,
            "Conflicting body fields retain stock without suppressing disjoint declarations.");
        Check(plan[other].GravityScale == 0f && plan[other].AtmosphereRgb == 0x112233 && plan[other].WaterAmount == null,
            "Resolved environments must apply only to the exact system/body key.");
        Check(plan[target].ResolveOxygen(0.3f, 0.2f) == 0.2f
            && new BodyEnvironmentOverride(gravityScale: 1f).ResolveOxygen(0.3f, 0.2f) == 0.3f,
            "Explicit atmosphere changes must constrain oxygen while independent changes preserve the stock value.");
        foreach (float water in new[] { -1f, 0.001f, 0.01f, 1.1f, float.NaN, float.PositiveInfinity })
        {
            try { _ = new BodyEnvironmentOverride(waterAmount: water); }
            catch (ArgumentOutOfRangeException) { checks++; continue; }
            throw new InvalidOperationException("Accepted unsafe stock water input " + water);
        }

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool expected)
        {
            var capability = ContractAudit.EvaluateBodyEnvironment(module);
            Check(capability.Available == expected, "Body environment contract: " + capability.Reason);
        }
        Audit(true);
        module.Mvid = Guid.NewGuid();
        Audit(true);
        var generator = module.GetType("World.Terrain.PlanetGenerationLogic").Methods.Single(method => method.Name == "GetPlanetData");
        var waterWrite = generator.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Stfld
            && instruction.Operand is FieldReference field && field.Name == "waterAmount");
        var original = waterWrite.OpCode;
        waterWrite.OpCode = OpCodes.Ldfld;
        Audit(false);
        waterWrite.OpCode = original;
        var solarBody = module.GetType("World.SolarBody");
        var gravity = solarBody.Methods.Single(method => method.Name == "SetPhysicsData");
        var nativeStore = gravity.Body.Instructions.Single(instruction => instruction.OpCode.Code == Code.Stfld
            && instruction.Operand is FieldReference field && field.Name == "g");
        original = nativeStore.OpCode;
        nativeStore.OpCode = OpCodes.Ldfld;
        Audit(false);
        nativeStore.OpCode = original;
        Audit(true);
        return checks;
    }
}
