using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class TerrainGenerationProviderTests
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
            catch (ArgumentOutOfRangeException) { checks++; return; }
            throw new InvalidOperationException(description);
        }

        Reject(() => _ = new PlanetTerrainOverrides(craterAmount: float.NaN), "NaN planet crater amount accepted.");
        Reject(() => _ = new PlanetTerrainOverrides(continentThreshold: -0.51f), "Unsafe continent threshold accepted.");
        Reject(() => _ = new PlanetTerrainOverrides(canyonStrength: 1.01f), "Unsafe canyon strength accepted.");
        Reject(() => _ = new AsteroidTerrainOverrides(craterAmount: 0.51f), "Unsafe asteroid crater amount accepted.");
        Reject(() => _ = new AsteroidTerrainOverrides(craterScaleFactor: 0f), "Zero crater scale accepted.");

        var stock = new PlanetTerrainContext(73, 4, false, 0.2f, 0.1f, 0.65f, true);
        var proposals = new TerrainGenerationCatalog<PlanetTerrainContext, PlanetTerrainOverrides>();
        var called = new List<string>();
        proposals.Add("zeta.mod", "terrain", context =>
        {
            called.Add("zeta");
            return new PlanetTerrainOverrides(craterAmount: 0.8f, canyonStrength: 0.1f);
        });
        proposals.Add("beta.mod", "terrain", context =>
        {
            called.Add("beta");
            throw new InvalidOperationException("Probe failure");
        });
        proposals.Add("alpha.mod", "terrain", context =>
        {
            called.Add("alpha");
            return context.Seed == 73 ? new PlanetTerrainOverrides(craterAmount: 0.7f, continentThreshold: -0.25f) : default;
        });

        var warnings = new List<string>();
        var values = proposals.Collect(stock, warnings.Add);
        var result = new PlanetTerrainOverrides(
            proposals.Resolve(values, value => value.CraterAmount, "craterAmount", warnings.Add),
            proposals.Resolve(values, value => value.ContinentThreshold, "continentThreshold", warnings.Add),
            proposals.Resolve(values, value => value.CanyonStrength, "canyonStrength", warnings.Add));
        Check(called.SequenceEqual(new[] { "alpha", "beta", "zeta" }), "Providers did not run in stable key order.");
        Check(result.CraterAmount == null && result.ContinentThreshold == -0.25f && result.CanyonStrength == 0.1f,
            "Contested or failed proposals interfered with disjoint fields.");
        Check(warnings.Any(message => message.Contains("alpha.mod:terrain, zeta.mod:terrain"))
            && warnings.Any(message => message.Contains("beta.mod:terrain")), "Provider diagnostics omitted an involved key.");
        bool closed = false;
        try { proposals.Add("late.mod", "terrain", _ => default); }
        catch (InvalidOperationException) { closed = true; }
        Check(closed, "A provider registered after first generation.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool planet, bool asteroid)
        {
            var planets = ContractAudit.EvaluatePlanetTerrainGeneration(module);
            var asteroids = ContractAudit.EvaluateAsteroidTerrainGeneration(module);
            Check(planets.Available == planet, "Planet terrain contract: " + planets.Reason);
            Check(asteroids.Available == asteroid, "Asteroid terrain contract: " + asteroids.Reason);
        }
        Audit(true, true);
        module.Mvid = Guid.NewGuid();
        Audit(true, true);

        var planetData = module.GetType("World.Terrain.PlanetDensityGenerationData");
        var canyonFrequency = planetData.Fields.Single(field => field.Name == "canyonFreq");
        canyonFrequency.FieldType = module.TypeSystem.Double;
        Audit(false, true);
        canyonFrequency.FieldType = module.TypeSystem.Single;

        var fieldTemplate = module.GetType("World.SolarBody").Methods.Single(method => method.Name == "SpawnRings");
        var templateCall = fieldTemplate.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Call
            && instruction.Operand is MethodReference method && method.Name == "GetAsteroidDataTemplate");
        templateCall.OpCode = OpCodes.Nop;
        Audit(true, false);
        return checks;
    }
}
