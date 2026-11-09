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

        var strata = new[]
        {
            new TerrainPlacementRule("alpha.mod:deep", TerrainPlacementScope.Planets, stone, iron, 45,
                new TerrainRadialBand(-300, -100)),
            new TerrainPlacementRule("beta.mod:shallow", TerrainPlacementScope.Planets, stone, other, 45,
                new TerrainRadialBand(-100, -25)),
            rules[1]
        };
        var layered = TerrainPlacementPlan.Build(strata, TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(layered.HasRadialBands && layered.Map(2, -300) == 7 && layered.Map(2, -101) == 7
            && layered.Map(2, -100) == 8 && layered.Map(2, -25) == 2,
            "Adjacent bands must select by half-open nominal elevation.");
        Check(layered.Map(2) == 2 && layered.Map(2, double.NaN) == 2 && layered.Map(7, -150) == 8,
            "Missing spatial information must leave bands untouched and preserve ordinary rules.");
        var unsupported = TerrainPlacementPlan.Build(strata, TerrainPlacementScope.Planets, 45, Resolve, _ => {}, radialBands: false);
        Check(!unsupported.HasRadialBands && unsupported.Map(2, -200) == 2 && unsupported.Map(7) == 8,
            "An unavailable spatial contract must leave ordinary rules usable.");
        var otherBody = TerrainPlacementPlan.Build(strata, TerrainPlacementScope.Planets, 46, Resolve, _ => {});
        Check(!otherBody.HasRadialBands, "A band leaked to a different body.");
        var overlapping = strata.Append(new TerrainPlacementRule("gamma.mod:overlap",
            TerrainPlacementScope.Planets, stone, other, 45, new TerrainRadialBand(-120, -95))).ToArray();
        var ambiguous = TerrainPlacementPlan.Build(overlapping, TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(!ambiguous.HasRadialBands && ambiguous.Map(2, -200) == 2 && ambiguous.Map(7) == 8,
            "Overlapping claims must keep stock for that source only.");
        var broad = TerrainPlacementPlan.Build(strata.Append(rules[0]).ToArray(), TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(!broad.HasRadialBands && broad.Map(2, -200) == 2, "An unconditional rule must conflict with a radial rule for the same source.");

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        void Audit(bool expected)
        {
            var result = ContractAudit.EvaluateTerrainPlacement(module);
            Check(result.Available == expected, result.Reason);
        }
        Audit(true);
        var nativeRadial = ContractAudit.EvaluateTerrainRadialPlacement(module);
        Check(nativeRadial.Available, nativeRadial.Reason);
        module.Mvid = Guid.NewGuid();
        Audit(true);
        Check(ContractAudit.EvaluateTerrainRadialPlacement(module).Available, "A different MVID rejected native planet coordinates.");
        var coordinates = module.GetType("World.Terrain.OctreeNodeData").Methods.Single(method => method.Name == "GetPosition");
        var centre = coordinates.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ldc_R4);
        centre.Operand = 0f;
        Check(!ContractAudit.EvaluateTerrainRadialPlacement(module).Available, "Changed native voxel centres admitted radial placement.");
        centre.Operand = 0.5f;
        var axis = coordinates.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldarg_2);
        axis.OpCode = OpCodes.Ldarg_3;
        Check(!ContractAudit.EvaluateTerrainRadialPlacement(module).Available, "Changed voxel coordinate ordering admitted radial placement.");
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
