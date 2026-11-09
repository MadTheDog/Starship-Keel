using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class TerrainPlacementTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
            checks++;
        }

        var iron = TerrainMaterialReference.Stock("Iron");
        var stone = TerrainMaterialReference.Stock("Stone");
        var other = TerrainMaterialReference.Stock("Other");
        var rules = new[]
        {
            new TerrainPlacementRule("alpha.mod:stone", TerrainPlacementScope.All, stone, iron, null),
            new TerrainPlacementRule("beta.mod:iron", TerrainPlacementScope.Planets, iron, other, null),
            new TerrainPlacementRule("gamma.mod:stone", TerrainPlacementScope.Planets, stone, other, 45)
        };
        byte? Resolve(TerrainMaterialReference reference) => reference.Value switch
        {
            "Stone" => 2,
            "Iron" => 7,
            "Other" => 8,
            _ => null
        };
        var conflicts = new List<string>();
        var asteroid = TerrainPlacementPlan.Build(rules, TerrainPlacementScope.Asteroids, 45, Resolve, conflicts.Add);
        var planet = TerrainPlacementPlan.Build(rules, TerrainPlacementScope.Planets, 46, Resolve, conflicts.Add);
        Check(asteroid.Map(2) == 7 && asteroid.Map(7) == 7, "Asteroid scope leaked planet rules.");
        Check(planet.Map(2) == 7 && planet.Map(7) == 8 && planet.Map(8) == 8, "The single-stage map was chained or lost a rule.");
        var contested = TerrainPlacementPlan.Build(rules.Reverse().ToArray(), TerrainPlacementScope.Planets, 45, Resolve, conflicts.Add);
        Check(contested.Map(2) == 2 && contested.Map(7) == 8
            && conflicts.Single().Contains("alpha.mod:stone, gamma.mod:stone"), "Conflicting destination replaced a stock row.");
        var absent = TerrainPlacementPlan.Build(rules, TerrainPlacementScope.Asteroids, null, _ => null, _ => {});
        Check(!absent.HasChanges && absent.Map(2) == 2, "An unresolved asset produced a mapping.");
        var registered = TerrainMaterialReference.Registered("delta.mod:ore");
        var mixed = new[]
        {
            new TerrainPlacementRule("delta.mod:custom", TerrainPlacementScope.Planets, stone, registered, null),
            rules[1]
        };
        var noRegistry = TerrainPlacementPlan.Build(mixed, TerrainPlacementScope.Planets, null, Resolve, _ => {});
        Check(noRegistry.Map(2) == 2 && noRegistry.Map(7) == 8, "An unavailable registered destination disabled a stock rule.");
        var activeRegistry = TerrainPlacementPlan.Build(mixed, TerrainPlacementScope.Planets, null,
            reference => reference.Kind == TerrainMaterialReferenceKind.Registered ? (byte)55 : Resolve(reference), _ => {});
        Check(activeRegistry.Map(2) == 55 && activeRegistry.Map(7) == 8, "An admitted registered destination did not resolve.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool expected)
        {
            var result = ContractAudit.EvaluateTerrainPlacement(module);
            Check(result.Available == expected, result.Reason);
        }
        Audit(true);
        module.Mvid = Guid.NewGuid();
        Audit(true);
        var texId = module.GetType("World.Terrain.VoxelNodeData").Fields.Single(field => field.Name == "texId1");
        texId.FieldType = module.TypeSystem.UInt16;
        Audit(false);
        texId.FieldType = module.TypeSystem.Byte;
        var immediate = module.GetType("World.Terrain.DensityComputeJob").Methods.Single(method => method.Name == "DispatchImmediate");
        var getData = immediate.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Callvirt
            && instruction.Operand is MethodReference method && method.Name == "GetData");
        getData.OpCode = OpCodes.Nop;
        Audit(false);
        return checks;
    }
}
