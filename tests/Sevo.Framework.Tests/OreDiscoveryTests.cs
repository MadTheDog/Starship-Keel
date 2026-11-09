using System.Numerics;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class OreDiscoveryTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            if (!ok) throw new InvalidOperationException(message);
        }

        var near = new OreDiscoveryResult("example.scan:iron", new Vector3(0, 0, 300), 2);
        var far = new OreDiscoveryResult("example.scan:far", new Vector3(301, 0, 0), 2);
        var invalid = new OreDiscoveryResult("example.scan:invalid", new Vector3(float.NaN, 0, 0), 2);
        var empty = new OreDiscoveryResult("example.scan:empty", Vector3.Zero, 0);
        var unkeyed = new OreDiscoveryResult("unkeyed", Vector3.Zero, 2);
        var candidates = new[] { far, invalid, empty, unkeyed, near };
        var result = new List<OreDiscoveryResult>();
        OreDiscoveryPlan.Append(result, candidates, 300);
        Check(result.SequenceEqual(new[] { near }), "Only in-range finite, keyed discoveries with positive sample counts should publish.");
        result.Clear();
        OreDiscoveryPlan.Append(result, Enumerable.Repeat(near, 200).ToArray(), 300);
        Check(result.Count == OreDiscoveryPlan.PerProviderLimit, "A single provider cannot exceed its quota.");
        for (int i = 0; i < 10; i++) OreDiscoveryPlan.Append(result, Enumerable.Repeat(near, 200).ToArray(), 300);
        Check(result.Count == OreDiscoveryPlan.TotalLimit, "The complete pulse cannot exceed the global quota.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var stock = ContractAudit.EvaluateOreDiscovery(game.MainModule);
        Check(stock.Available, "Stock discovery seam must validate: " + stock.Reason);
        game.MainModule.Mvid = Guid.NewGuid();
        Check(ContractAudit.EvaluateOreDiscovery(game.MainModule).Available, "MVID drift cannot disable discovery.");
        var onNewSystem = game.MainModule.GetType("World.PlanetarySystem").Fields.Single(field => field.Name == "OnNewSystem");
        onNewSystem.Name = "RenamedWorldTransitionCallback";
        Check(!ContractAudit.EvaluateOreDiscovery(game.MainModule).Available, "A missing system transition must reject stale-result risk.");
        onNewSystem.Name = "OnNewSystem";
        var scanner = game.MainModule.GetType("World.Terrain.OreScannerResources").Methods.Single(method => method.Name == "OnScannerKeyPressed");
        var publish = scanner.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stsfld
            && instruction.Operand is FieldReference field && field.Name == "lastActivateTime");
        publish.OpCode = OpCodes.Nop;
        publish.Operand = null;
        Check(!ContractAudit.EvaluateOreDiscovery(game.MainModule).Available, "A missing accepted-pulse publication must reject.");
        Check(ContractAudit.EvaluateOreScanner(game.MainModule).Available, "Stock scanner snapshots have an independent contract.");
        return checks;
    }
}
