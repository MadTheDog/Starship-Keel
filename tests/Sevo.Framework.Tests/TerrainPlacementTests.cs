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

        // The two directions have different ground radii even though the samples share a nominal elevation.
        double? Surface(TerrainAuthoredSurfaceSample sample)
            => sample.DirectionX > 0 ? sample.NominalRadius + 10 : sample.NominalRadius;
        var depthRule = new TerrainPlacementRule("alpha.mod:surface", TerrainPlacementScope.Planets, stone, iron, 45,
            depth: new TerrainAuthoredSurfaceDepthBand(10, 20), surfaceRadius: Surface);
        var depth = TerrainPlacementPlan.Build(new[] { depthRule, rules[1] }, TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(depth.HasAuthoredSurfaceBands && depth.Map(2, 0) == 2,
            "A surface band was evaluated using nominal elevation without a local surface.");
        Check(TerrainAuthoredSurfaceSample.TryCreate(45, 100, 100, 0, 0, out var uphill)
            && TerrainAuthoredSurfaceSample.TryCreate(45, 100, -100, 0, 0, out var downhill)
            && depth.MapAuthoredSurface(2, uphill, true) == 7 && depth.MapAuthoredSurface(2, downhill, true) == 2,
            "Equal nominal elevations must classify using the distinct local surface radii.");
        Check(TerrainAuthoredSurfaceSample.TryCreate(45, 100, 90, 0, 0, out var belowBand)
            && depth.MapAuthoredSurface(2, belowBand, true) == 2,
            "The deep edge of an authored surface band must be exclusive.");
        Check(TerrainAuthoredSurfaceSample.TryCreate(45, 100, 100, 0, 0, out var point)
            && depth.MapAuthoredSurface(2, point, false) == 2 && depth.MapAuthoredSurface(7, point, true) == 8
            && !TerrainAuthoredSurfaceSample.TryCreate(45, 100, 0, 0, 0, out _),
            "Air, invalid directions and unrelated material rules must preserve their established behavior.");
        double Coordinate(double nodeCentre, int index, double spacing) => nodeCentre + (index - 9.5) * spacing;
        Check(TerrainAuthoredSurfaceSample.TryCreate(45, 100, Coordinate(88, 17, 1), 0.5, 0.5, out var left)
            && TerrainAuthoredSurfaceSample.TryCreate(45, 100, Coordinate(104, 1, 1), 0.5, 0.5, out var right)
            && TerrainAuthoredSurfaceSample.TryCreate(45, 100, Coordinate(96, 9, 2), 1, 1, out var coarse)
            && left.DirectionX == right.DirectionX && depth.MapAuthoredSurface(2, left, true) == 7
            && depth.MapAuthoredSurface(2, right, true) == 7 && depth.MapAuthoredSurface(2, coarse, true) == 7,
            "The shared edge and nearby coarse sample must classify from the same body-local surface field.");
        var incompatible = TerrainPlacementPlan.Build(new[] { depthRule, strata[0], rules[1] },
            TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(!incompatible.HasAuthoredSurfaceBands && !incompatible.HasRadialBands && incompatible.Map(7) == 8,
            "Incomparable reference frames must retain stock only for the contested material.");
        var upper = new TerrainPlacementRule("beta.mod:upper", TerrainPlacementScope.Planets, stone, other, 45,
            depth: new TerrainAuthoredSurfaceDepthBand(0, 10), surfaceRadius: Surface);
        var sameField = TerrainPlacementPlan.Build(new[] { upper, depthRule }, TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(sameField.HasAuthoredSurfaceBands && sameField.MapAuthoredSurface(2, point, true) == 7,
            "Disjoint bands must compose when they measure from one shared surface.");
        var competing = new TerrainPlacementRule("beta.mod:unrelated", TerrainPlacementScope.Planets, stone, other, 45,
            depth: new TerrainAuthoredSurfaceDepthBand(0, 10), surfaceRadius: sample => sample.NominalRadius + 30);
        var distinctFields = TerrainPlacementPlan.Build(new[] { competing, depthRule }, TerrainPlacementScope.Planets, 45,
            Resolve, _ => {});
        Check(!distinctFields.HasAuthoredSurfaceBands && distinctFields.MapAuthoredSurface(2, point, true) == 2,
            "Bands that use different ground surfaces cannot safely compose on the same material.");
        var declined = new TerrainPlacementRule("alpha.mod:declined", TerrainPlacementScope.Planets, stone, iron, 45,
            depth: new TerrainAuthoredSurfaceDepthBand(0, 20), surfaceRadius: _ => double.NaN);
        var invalidSurface = TerrainPlacementPlan.Build(new[] { declined, rules[1] }, TerrainPlacementScope.Planets, 45, Resolve, _ => {});
        Check(invalidSurface.MapAuthoredSurface(2, point, true) == 2 && invalidSurface.MapAuthoredSurface(7, point, true) == 8,
            "An invalid supplied surface must preserve the stock row and unrelated placement.");
        var errors = new List<string>();
        var throwing = new TerrainPlacementRule("alpha.mod:failed", TerrainPlacementScope.Planets, stone, iron, 45,
            depth: new TerrainAuthoredSurfaceDepthBand(0, 20), surfaceRadius: _ => throw new InvalidOperationException());
        var faulty = TerrainPlacementPlan.Build(new[] { throwing, rules[1] }, TerrainPlacementScope.Planets, 45, Resolve, errors.Add);
        Check(faulty.MapAuthoredSurface(2, point, false) == 2 && errors.Count == 0
            && faulty.MapAuthoredSurface(2, point, true) == 2 && errors.Single().Contains("alpha.mod:failed")
            && faulty.MapAuthoredSurface(7, point, true) == 8,
            "Authored fields must avoid air samples and isolate a callback failure to its affected material.");
        var noNativeDepth = TerrainPlacementPlan.Build(new[] { depthRule, rules[1] }, TerrainPlacementScope.Planets, 45,
            Resolve, _ => {}, authoredSurfaceBands: false);
        Check(!noNativeDepth.HasAuthoredSurfaceBands && noNativeDepth.Map(7) == 8,
            "An incompatible authored surface audit must leave ordinary placement usable.");

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
        var nativeDepth = ContractAudit.EvaluateTerrainAuthoredSurfacePlacement(module);
        Check(nativeDepth.Available, nativeDepth.Reason);
        module.Mvid = Guid.NewGuid();
        Audit(true);
        Check(ContractAudit.EvaluateTerrainRadialPlacement(module).Available, "A different MVID rejected native planet coordinates.");
        Check(ContractAudit.EvaluateTerrainAuthoredSurfacePlacement(module).Available,
            "A different MVID rejected the unchanged authored surface contract.");
        var density = module.GetType("World.Terrain.VoxelNodeData").Methods.Single(method => method.Name == "get_Density");
        var densityScale = density.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_R4);
        float originalScale = (float)densityScale.Operand;
        densityScale.Operand = -originalScale;
        Check(!ContractAudit.EvaluateTerrainAuthoredSurfacePlacement(module).Available
            && ContractAudit.EvaluateTerrainRadialPlacement(module).Available, "Changing the density sign must disable only authored surface depth.");
        densityScale.Operand = originalScale;
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
