using Sevo.Framework.Sdk;
using Sevo.Framework.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class CelestialLayoutTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }

        void Reject(Action action, string reason)
        {
            checks++;
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(reason);
        }

        var radius = CelestialLayoutRule.OverrideBody(21, 31, radiusMeters: 50000);
        var orbit = CelestialLayoutRule.OverrideBody(21, 31, orbitRadiusMeters: 1000000);
        var moon = CelestialLayoutRule.AddRockyMoon(21, 31, 32, 20000, 400000);
        Check(radius.SystemSeed == 21 && radius.BodySeed == 31 && radius.RadiusMeters == 50000
            && radius.OrbitRadiusMeters == null && !radius.IsAddition, "Body override must preserve exact identifiers and optional fields.");
        Check(moon.IsAddition && moon.ParentSeed == 31 && moon.BodySeed == 32, "Moon declaration must carry its own seed and parent.");
        Reject(() => CelestialLayoutRule.OverrideBody(0, 31, 50000), "A layout rule must target a valid system.");
        Reject(() => CelestialLayoutRule.OverrideBody(21, 0, 50000), "A layout rule must target a valid body.");
        Reject(() => CelestialLayoutRule.OverrideBody(21, 31), "An empty override must reject.");
        Reject(() => CelestialLayoutRule.OverrideBody(21, 31, radiusMeters: float.NaN), "A non-finite radius must reject.");
        Reject(() => CelestialLayoutRule.OverrideBody(21, 31, orbitRadiusMeters: 0), "A zero orbit must reject.");
        Reject(() => CelestialLayoutRule.AddRockyMoon(21, 31, 31, 20000, 400000), "An appended moon must have a distinct seed.");

        var declarations = new Dictionary<string, CelestialLayoutRule>
        {
            ["example.beta:orbit"] = orbit,
            ["example.alpha:radius"] = radius,
            ["example.alpha:moon"] = moon
        };
        var plan = CelestialLayoutPlan.Resolve(declarations);
        Check(plan.GetRules(21).Select(pair => pair.Key).SequenceEqual(new[]
            { "example.alpha:moon", "example.alpha:radius", "example.beta:orbit" }),
            "Layout edits must be stable across plugin registration order.");
        Check(plan.GetRules(19).Count == 0, "Layout rules cannot leak into other systems.");
        Check(!CelestialLayoutPlan.HasConflict(plan.GetRules(21)), "Distinct fields and moon additions must compose.");
        declarations.Add("example.gamma:radius", CelestialLayoutRule.OverrideBody(21, 31, 55000));
        Check(CelestialLayoutPlan.HasConflict(CelestialLayoutPlan.Resolve(declarations).GetRules(21)),
            "Competing field writes must reject as a unit.");
        declarations.Remove("example.gamma:radius");
        declarations.Add("example.gamma:moon", CelestialLayoutRule.AddRockyMoon(21, 31, 32, 22000, 600000));
        Check(CelestialLayoutPlan.HasConflict(CelestialLayoutPlan.Resolve(declarations).GetRules(21)),
            "Two definitions cannot claim the same new body identity.");
        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var baseline = ContractAudit.EvaluateCelestialLayout(game.MainModule);
        Check(baseline.Available, "Stock layout seam must validate: " + baseline.Reason);
        game.MainModule.Mvid = Guid.NewGuid();
        Check(ContractAudit.EvaluateCelestialLayout(game.MainModule).Available, "Assembly identity cannot gate the layout hook.");
        var source = game.MainModule.GetType("World.SolarBodyGenerator").Methods.Single(method => method.Name == "GetStarSystemBodiesData");
        var stockGeneration = source.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference call && call.Name == "GetCelestialBodyData");
        stockGeneration.OpCode = OpCodes.Nop;
        stockGeneration.Operand = null;
        Check(!ContractAudit.EvaluateCelestialLayout(game.MainModule).Available, "Losing the shared stock generator must fail closed.");
        Check(ContractAudit.EvaluateOreGeneration(game.MainModule).Available, "A layout seam failure must leave ore tuning available.");
        return checks;
    }
}
