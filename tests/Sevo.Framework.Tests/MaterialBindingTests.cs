using Sevo.Framework.Runtime;

internal static class MaterialBindingTests
{
    internal static int Run()
    {
        var binding = new MaterialBindingSnapshot<int>(
            new Dictionary<int, int> { [1] = 42 },
            new Dictionary<int, byte[][]> { [0] = new[] { new byte[] { 1, 2 }, new byte[] { 3 } } },
            new Dictionary<int, byte[][]> { [1] = new[] { new byte[] { 4 }, new byte[] { 5 } } });
        var diffuse = new[] { new[] { new byte[] { 1, 2 }, new byte[] { 3 } } };
        var specular = new[] { Array.Empty<byte[]>(), new[] { new byte[] { 4 }, new byte[] { 5 } } };
        var rows = new[] { 19, 42, 91 };

        bool Matches() => binding.Mismatch(rows, (slice, mip) => diffuse[slice][mip],
            (slice, mip) => specular[slice][mip]) == null;
        void Check(bool expected, string scenario)
        {
            if (Matches() != expected) throw new InvalidOperationException(scenario);
        }

        Check(true, "Copied arrays and appended rows preserve owned bindings.");
        rows[0] = 33;
        Check(true, "Unrelated rows do not invalidate a palette.");
        rows[1] = 43;
        Check(false, "A changed owned row is rejected.");
        rows[1] = 42;
        diffuse[0][1][0] = 7;
        Check(false, "An in-place edit to a lower mip is rejected.");
        diffuse[0][1][0] = 3;
        specular[1][0] = Array.Empty<byte>();
        Check(false, "Missing specular bytes are rejected.");
        specular[1][0] = new byte[] { 4 };
        rows = new[] { 19 };
        Check(false, "A missing owned row is rejected.");
        return 6;
    }
}
