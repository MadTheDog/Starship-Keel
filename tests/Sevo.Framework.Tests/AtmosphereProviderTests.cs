using System.Numerics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;

internal static class AtmosphereProviderTests
{
    internal static int Run()
    {
        var first = new BodyEnvironmentTarget(1, 2);
        var second = new BodyEnvironmentTarget(1, 3);
        var position = new Vector3(1, 2, 3);
        AtmosphereProviders.Register("test.weather", "wind", first, query =>
            query.SimulationSeconds == 7 && query.BodyLocalPosition == position
                ? new AtmosphereSample(query.StockDensity / 2, Vector3.UnitX) : null);
        var sampled = AtmosphereProviders.Resolve(first, position, 7, 0.5f, out var sample);
        if (!sampled || sample.RelativeDensity != 0.25f || sample.BodyLocalAirVelocity != Vector3.UnitX)
            throw new InvalidOperationException("The weather provider did not receive the scoped sample.");
        if (!AtmosphereProviders.Resolve(second, position, 7, 0.5f, out var stock)
            || stock.RelativeDensity != 0.5f || stock.BodyLocalAirVelocity.HasValue)
            throw new InvalidOperationException("Wind crossed body scope or reported unmeasured stock velocity.");
        if (!AtmosphereProviders.Resolve(first, position, 8, 0.5f, out stock) || stock.BodyLocalAirVelocity.HasValue)
            throw new InvalidOperationException("A provider decline did not preserve stock.");
        AtmosphereProviders.Register("test.weather", "broken", second, _ =>
            new AtmosphereSample(float.NaN, Vector3.UnitX));
        if (AtmosphereProviders.Resolve(second, position, 7, 0.5f, out stock) || stock.RelativeDensity != 0.5f)
            throw new InvalidOperationException("An invalid provider response contaminated stock.");
        try
        {
            AtmosphereProviders.Register("test.other", "wind", first, _ => null);
            throw new Exception("An occupied body accepted a second provider.");
        }
        catch (InvalidOperationException) { }
        if (!AtmosphericDrag.TryCalculate(0.5f, Vector3.UnitX * 4, Vector3.UnitX * 2, 3, out var force)
            || force != -Vector3.UnitX * 3
            || !AtmosphericDrag.TryCalculate(1, Vector3.One, Vector3.One, 1, out force) || force != Vector3.Zero
            || AtmosphericDrag.TryCalculate(1, new Vector3(float.NaN), Vector3.Zero, 1, out _))
            throw new InvalidOperationException("Drag did not oppose relative motion or reject invalid input.");
        if (!AtmosphericHeating.TryCalculateEnergy(0.5f, Vector3.UnitX * 4, Vector3.UnitX * 2,
                2f, 3f, 0.5f, 100f, out var energy) || energy != 6f
            || !AtmosphericHeating.TryCalculateEnergy(1f, new Vector3(float.MaxValue), Vector3.Zero,
                1f, 1f, 1f, 100f, out energy) || energy != 100f
            || AtmosphericHeating.TryCalculateEnergy(1f, Vector3.Zero, Vector3.Zero, -1f, 1f, 1f, 100f, out _))
            throw new InvalidOperationException("Heating must use relative speed, honor the cap, and reject invalid coefficients.");
        using var game = ModuleDefinition.ReadModule(".refs/game/Assembly-CSharp.dll");
        if (!ContractAudit.EvaluateGridPointMotion(game).Available)
            throw new InvalidOperationException("Native point motion contract was not recognized.");
        var velocity = game.GetType("Manager.SkyRigidbody/Data").Methods.Single(method => method.Name == "VelocityAtSystemPoint");
        var cross = velocity.Body.Instructions.Single(i => i.Operand is MethodReference method && method.Name == "cross");
        cross.OpCode = OpCodes.Nop;
        if (ContractAudit.EvaluateGridPointMotion(game).Available || !ContractAudit.EvaluateGridPhysics(game).Available)
            throw new InvalidOperationException("Point motion drift affected unrelated force contracts.");
        return 11;
    }
}
