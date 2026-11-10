using System.Numerics;
using Mono.Cecil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class TerrainAnchorStateTests
{
    internal static int Run()
    {
        var profile = Guid.NewGuid();
        const string key = "test.terrain:landing";
        var pose = new TerrainAnchorRecord("GRID-1", new BodyEnvironmentTarget(3, 4), Vector3.One, Vector3.UnitY);
        var state = new TerrainAnchorState(profile);
        if (!state.TryAdd("Alpha", 2, key, pose) || state.TryAdd("Alpha", 2, key, pose)
            || !state.TryAdd("Beta", 2, key, pose))
            throw new InvalidOperationException("Terrain anchor keys did not respect save scope.");
        byte[] saved = state.Serialize();
        state = TerrainAnchorState.Read(saved, profile);
        if (!state.TryGet("Alpha", 2, key, out var restored) || restored.Grid != "GRID-1"
            || restored.Position != pose.Position || restored.Normal != pose.Normal || !restored.Body.Equals(pose.Body)
            || state.TryGet("Alpha", 3, key, out _))
            throw new InvalidOperationException("Terrain anchor did not restore exactly within its world.");
        if (state.Delete("Alpha", 2, key, "GRID-2") || !state.Delete("Alpha", 2, key, "grid-1"))
            throw new InvalidOperationException("Terrain anchor release failed its grid identity check.");
        state = TerrainAnchorState.Read(state.Serialize(), profile);
        if (state.TryGet("Alpha", 2, key, out _) || !state.TryGet("Beta", 2, key, out _))
            throw new InvalidOperationException("Terrain anchor release affected a different character or returned after reload.");
        try { TerrainAnchorState.Read(saved, Guid.NewGuid()); throw new Exception("An anchor crossed profile lineage."); }
        catch (IOException) { }
        using var game = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        var audit = ContractAudit.EvaluateTerrainAnchorAttachments(game);
        if (!audit.Available) throw new InvalidOperationException(audit.Reason);
        var field = game.GetType("VoxelEngine.ParentEntity").Fields.Single(value => value.Name == "saveInWorld");
        field.IsPublic = false;
        if (ContractAudit.EvaluateTerrainAnchorAttachments(game).Available || !ContractAudit.EvaluateTerrainAnchors(game).Available)
            throw new InvalidOperationException("Saved grid contract drift was not isolated to anchor persistence.");
        return 7;
    }
}
