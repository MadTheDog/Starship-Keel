using Sevo.Framework.Sdk;

internal static class OreDepositTests
{
    internal static int Run()
    {
        var first = new OreDepositField("fixture.alpha:ore", 11, 23, 32, 8, 1);
        var replay = new OreDepositField("fixture.alpha:ore", 11, 23, 32, 8, 1);
        var other = new OreDepositField("fixture.beta:ore", 11, 23, 32, 8, 1);
        bool differs = false;
        int covered = 0;
        int samples = 0;
        for (int x = -64; x <= 64; x += 2)
        for (int y = -64; y <= 64; y += 2)
        for (int z = -64; z <= 64; z += 2)
        {
            samples++;
            bool present = first.Contains(x, y, z);
            if (present != replay.Contains(x, y, z)) throw new Exception("Deposit replay differs.");
            differs |= present != other.Contains(x, y, z);
            if (present) covered++;
        }
        if (!differs || covered == 0 || covered == samples) throw new Exception("Deposit field is degenerate.");
        var reference = new StockOreDepositReference("Iron", 64, 500000, 256);
        var (spacing, radius, chance) = reference.ToFieldParameters();
        double density = chance / (spacing * spacing * spacing);
        double volume = 4 * Math.PI * radius * radius * radius / (3 * Math.Sqrt(2));
        if (Math.Abs(density - 64.0 / 500000) > 1e-12 || Math.Abs(volume - 256) > 1e-9)
            throw new Exception("Stock reference lost its measured density or median volume.");
        using var runtime = Mono.Cecil.ModuleDefinition.ReadModule(typeof(Sevo.Framework.Runtime.ContractAudit).Assembly.Location);
        var readback = runtime.GetType("Sevo.Framework.Runtime.TerrainPlacement").Methods
            .Single(method => method.Name == "RecordSuccessfulReadback");
        if (!readback.Body.Instructions.Any(instruction => instruction.Operand is Mono.Cecil.MethodReference method
            && method.DeclaringType.FullName == "Sevo.Framework.Runtime.OreDepositRegistration" && method.Name == "Seal"))
            throw new Exception("Deposit-only declarations must be checked at the async readback gate.");
        return 5;
    }
}
