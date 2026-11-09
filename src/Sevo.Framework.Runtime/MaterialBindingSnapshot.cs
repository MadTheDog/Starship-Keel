using System;
using System.Collections.Generic;

namespace Sevo.Framework.Runtime
{
    internal sealed class MaterialBindingSnapshot<TRow>
    {
        private readonly IReadOnlyDictionary<int, TRow> rows;
        private readonly IReadOnlyDictionary<int, byte[][]> diffuse;
        private readonly IReadOnlyDictionary<int, byte[][]> specular;

        internal MaterialBindingSnapshot(IReadOnlyDictionary<int, TRow> rows,
            IReadOnlyDictionary<int, byte[][]> diffuse, IReadOnlyDictionary<int, byte[][]> specular)
        {
            this.rows = rows;
            this.diffuse = diffuse;
            this.specular = specular;
        }

        internal string? Mismatch(TRow[] liveRows, Func<int, int, byte[]> readDiffuse,
            Func<int, int, byte[]> readSpecular)
        {
            foreach (var row in rows)
                if ((uint)row.Key >= (uint)liveRows.Length ||
                    !EqualityComparer<TRow>.Default.Equals(row.Value, liveRows[row.Key]))
                    return $"material row {row.Key} changed or is missing";

            return CompareAtlas(diffuse, readDiffuse, "diffuse") ??
                CompareAtlas(specular, readSpecular, "specular");
        }

        private static string? CompareAtlas(IReadOnlyDictionary<int, byte[][]> slices,
            Func<int, int, byte[]> read, string name)
        {
            foreach (var slice in slices)
                for (int mip = 0; mip < slice.Value.Length; mip++)
                    if (!slice.Value[mip].AsSpan().SequenceEqual(read(slice.Key, mip)))
                        return $"{name} slice {slice.Key}, mip {mip} changed or is missing";
            return null;
        }
    }
}
