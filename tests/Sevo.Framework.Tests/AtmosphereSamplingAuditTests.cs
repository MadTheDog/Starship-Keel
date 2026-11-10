using Mono.Cecil;
using Sevo.Framework.Runtime;

internal static class AtmosphereSamplingAuditTests
{
    internal static int Run()
    {
        using var native = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        int checks = 0;

        void Assert(bool expected, string scenario)
        {
            var result = ContractAudit.EvaluateAtmosphereSampling(native);
            if (result.Available != expected)
                throw new InvalidOperationException(scenario + ": " + result.Reason);
            checks++;
        }

        Assert(true, "Stock atmosphere sampling dependencies");
        native.Mvid = Guid.NewGuid();
        Assert(true, "Unrelated assembly identity");
        var row = native.GetType("World.Terrain.Planet.PlanetGravityData");
        var density = row.Fields.Single(field => field.Name == "atmosphereDensity");
        density.FieldType = native.TypeSystem.Int32;
        Assert(false, "Native density field type changed");
        density.FieldType = native.TypeSystem.Single;
        var body = native.GetType("World.SolarBody");
        var gravityId = body.Fields.Single(field => field.Name == "gravityId");
        gravityId.IsPublic = false;
        Assert(false, "Body gravity row is inaccessible");
        gravityId.IsPublic = true;
        var loop = native.GetType("Manager.GameloopManager");
        var access = loop.Methods.Single(method => method.Name == "get_IsNativeContainerAccessUnsafe");
        access.IsPublic = false;
        Assert(false, "Native safe-phase guard is inaccessible");
        access.IsPublic = true;
        var falloff = native.GetType("World.Terrain.Planet.PlanetCommon").Methods
            .Single(method => method.Name == "GetAtmosphereFalloffFactor");
        falloff.Parameters[1].ParameterType = native.TypeSystem.Double;
        Assert(false, "Native falloff signature changed");
        falloff.Parameters[1].ParameterType = native.TypeSystem.Single;
        Assert(true, "Native contracts restored");
        return checks;
    }
}
