using Sevo.Framework.Runtime;

internal static class TerrainSuspensionTests
{
    internal static int Run()
    {
        if (!TerrainSuspension.TryCalculate(0.5f, -2f, 1f, 100f, 5f, out var force) || force != 60f
            || !TerrainSuspension.TryCalculate(1.2f, -20f, 1f, 100f, 5f, out force) || force != 0f
            || !TerrainSuspension.TryCalculate(0.5f, 20f, 1f, 100f, 5f, out force) || force != 0f
            || TerrainSuspension.TryCalculate(float.NaN, 0, 1, 100, 5, out _))
            throw new InvalidOperationException("Suspension must oppose compression and never pull or act outside reach.");
        return 4;
    }
}
