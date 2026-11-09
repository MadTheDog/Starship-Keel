using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class GalaxyCatalogTests
{
    internal static int Run()
    {
        var start = new GalaxyStarSeed(42, 34, 2, 64);
        var second = new GalaxyStarSeed(43, 34, 0, 64);
        var third = new GalaxyStarSeed(44, 4, 1, 5);
        var route = new GalaxyGateRoute(42, 43);
        var forward = new GalaxyCatalogPlan(new[] { start, second, third }, new[] { route }, start);
        var reverse = new GalaxyCatalogPlan(new[] { third, second, start }, new[] { route }, start);
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException(message);
        }

        Check(forward.Fingerprint == reverse.Fingerprint && forward.Stars.SequenceEqual(reverse.Stars),
            "Catalog fingerprints and order must be independent of declaration ordering.");
        Check(forward.StarsInSector(34, 64).SequenceEqual(new[] { start, second })
            && forward.StarsInSector(4, 5).SequenceEqual(new[] { third })
            && !forward.StarsInSector(34, 63).Any(), "Sector ranges must select exact populated and empty cells.");
        Check(forward.Fingerprint != new GalaxyCatalogPlan(new[] { start, second, third }, Array.Empty<GalaxyGateRoute>(), start).Fingerprint,
            "Gate routes must contribute to catalog identity.");
        Reject(() => _ = new GalaxyCatalogPlan(new[] { second }, Array.Empty<GalaxyGateRoute>(), start),
            "Starting system must remain present.");
        Reject(() => _ = new GalaxyCatalogPlan(new[] { start, start }, Array.Empty<GalaxyGateRoute>(), start),
            "Duplicate native star seed accepted.");
        Reject(() => _ = new GalaxyCatalogPlan(new[] { start, new GalaxyStarSeed(42, 33, 2, 64) }, Array.Empty<GalaxyGateRoute>(), start),
            "A reused seed in a distinct sector must still reject.");
        Reject(() => _ = new GalaxyCatalogPlan(new[] { start, third }, new[] { route }, start), "Dangling route accepted.");
        Reject(() => _ = new GalaxyCatalogPlan(new[] { start, second, third }, new[] { route, new GalaxyGateRoute(43, 44) }, start),
            "A gate endpoint cannot appear in another route.");
        Reject(() => _ = new GalaxyStarSeed(1, 128, 0, 0), "Out-of-bounds sector accepted.");
        Reject(() => _ = new GalaxyStarSeed(0, 0, 0, 0), "Zero seed accepted.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        var audit = ContractAudit.EvaluateGalaxyCatalogPreview(module);
        Check(audit.Available, audit.Reason);
        module.Mvid = Guid.NewGuid();
        Check(ContractAudit.EvaluateGalaxyCatalogPreview(module).Available, "Unrelated identity drift must preserve the catalog preview.");
        var star = module.GetType("World.StarData");
        var shaderCoordinates = star.Methods.Single(method => method.Name == "get_HolomapPosition");
        var entry = shaderCoordinates.Body.Instructions.Last();
        entry.OpCode = OpCodes.Nop;
        Check(!ContractAudit.EvaluateGalaxyCatalogPreview(module).Available, "Broken native position result must reject the catalog preview.");
        return checks;
    }
}
