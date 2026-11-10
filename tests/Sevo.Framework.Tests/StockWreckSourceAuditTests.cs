using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class StockWreckSourceAuditTests
{
    internal static int Run()
    {
        using var game = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        Check(true);
        game.Mvid = Guid.NewGuid();
        Check(true);
        var sources = game.GetType("World.WorldEntities");
        foreach (string name in new[] { "OnNodeLoad", "SpawnWreckOnNewTerrainNode" })
        {
            var call = sources.Methods.Single(method => method.Name == name).Body.Instructions
                .Single(instruction => instruction.Operand is MethodReference method && method.Name == "GetWreckSGUID");
            var original = call.OpCode;
            call.OpCode = OpCodes.Nop;
            Check(false);
            if (!ContractAudit.EvaluateWreckClaim(game).Available)
                throw new InvalidOperationException("Stock source drift disabled transient wreck claims.");
            call.OpCode = original;
        }
        var identity = sources.Methods.Single(method => method.Name == "GetWreckSGUID").Body.Instructions[0];
        var originalFormat = identity.Operand;
        identity.Operand = "{1}-{0}-w";
        Check(false);
        identity.Operand = originalFormat;

        var load = sources.Methods.Single(method => method.Name == "OnNodeLoad");
        var branch = load.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.Name == "LoadEntityFromPlayerInstanceAtPath").Next;
        var originalBranch = branch.Operand;
        branch.Operand = branch.Next;
        Check(false);
        branch.Operand = originalBranch;
        Check(true);
        return 9 + StockWreckClaimJournalTests.Run();

        void Check(bool expected)
        {
            var result = ContractAudit.EvaluateStockWreckSources(game);
            if (result.Available != expected) throw new InvalidOperationException(result.Reason);
        }
    }
}
