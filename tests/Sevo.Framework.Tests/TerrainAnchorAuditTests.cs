using Mono.Cecil;
using Sevo.Framework.Runtime;

internal static class TerrainAnchorAuditTests
{
    internal static int Run()
    {
        using var native = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        var anchor = ContractAudit.EvaluateTerrainAnchors(native);
        if (!anchor.Available) throw new InvalidOperationException(anchor.Reason);
        native.Mvid = Guid.NewGuid();
        if (!ContractAudit.EvaluateTerrainAnchors(native).Available)
            throw new InvalidOperationException("Assembly identity gated anchors.");
        var method = native.GetType("Manager.SkyTransformData").Methods.Single(m => m.Name == "SystemToLocal"
            && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Vector3l");
        method.IsPublic = false;
        if (ContractAudit.EvaluateTerrainAnchors(native).Available || !ContractAudit.EvaluateWorldSurfaceQuery(native).Available)
            throw new InvalidOperationException("Body conversion drift was not isolated to the dependent contact path.");
        method.IsPublic = true;
        var pending = native.GetType("World.Terrain.OctreeNodeJobs").Fields.Single(field => field.Name == "toGeoCompute");
        pending.IsPublic = false;
        if (ContractAudit.EvaluateTerrainAnchors(native).Available || !ContractAudit.EvaluateWorldSurfaceQuery(native).Available)
            throw new InvalidOperationException("Missing pending geometry state did not disable anchor revalidation.");
        return 4;
    }
}
