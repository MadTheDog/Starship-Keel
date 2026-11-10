using Mono.Cecil;
using Sevo.Framework.Runtime;

internal static class AsteroidTerrainEditAuditTests
{
    internal static int Run()
    {
        using var game = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        Check(true);
        game.Mvid = Guid.NewGuid();
        Check(true);
        var asteroid = game.GetType("World.Terrain.AsteroidData");
        var flag = asteroid.Fields.Single(field => field.Name == "hasSaveData");
        flag.Name = "changed";
        Check(false);
        if (!ContractAudit.EvaluateTerrainEdits(game).Available)
            throw new InvalidOperationException("Asteroid persistence drift disabled planet editing.");
        return 4;

        void Check(bool expected)
        {
            var result = ContractAudit.EvaluateAsteroidTerrainEdits(game);
            if (result.Available != expected) throw new InvalidOperationException(result.Reason);
        }
    }
}
