using System;
using System.IO;
using Sevo.Framework.Sdk;

namespace Sevo.Example.TerrainPlacement
{
    // A private acceptance fixture. It never reads the resident octree or changes the field while replaying.
    internal sealed class StockTerrainSurfaceSurvey
    {
        internal const int Width = 20;
        internal const int Count = Width * Width * Width;
        internal const string FileName = "terrain-stock-surface-v1.bin";
        private const int Magic = 0x3153544B;
        private const int FileLength = sizeof(int) * 2 + sizeof(float) * 5 + sizeof(ulong) * 2 + Count * sizeof(short);
        private const double GridHalf = 9.5;
        private const double BoundaryEpsilon = 0.000001;
        private readonly short[] density;

        internal StockTerrainSurfaceSurvey(int level, float spacing, ulong systemSeed, ulong bodySeed, float nominalRadius,
            float x, float y, float z, short[] density)
        {
            if (level < 0 || level > 30 || !float.IsFinite(spacing) || spacing <= 0 || systemSeed == 0 || bodySeed == 0
                || !float.IsFinite(nominalRadius) || nominalRadius < Width * spacing
                || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || density.Length != Count)
                throw new ArgumentException("Invalid canonical terrain capture.");
            Level = level;
            Spacing = spacing;
            SystemSeed = systemSeed;
            BodySeed = bodySeed;
            NominalRadius = nominalRadius;
            X = x;
            Y = y;
            Z = z;
            this.density = (short[])density.Clone();
        }

        internal int Level { get; }
        internal float Spacing { get; }
        internal ulong SystemSeed { get; }
        internal ulong BodySeed { get; }
        internal float NominalRadius { get; }
        internal float X { get; }
        internal float Y { get; }
        internal float Z { get; }
        internal short DensityAt(int index) => density[index];

        internal void WriteNew(string path)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(file);
            writer.Write(Magic);
            writer.Write(Level);
            writer.Write(Spacing);
            writer.Write(SystemSeed);
            writer.Write(BodySeed);
            writer.Write(NominalRadius);
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Z);
            foreach (short sample in density) writer.Write(sample);
        }

        internal static StockTerrainSurfaceSurvey Read(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A reparse path cannot be used as a canonical terrain capture.");
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (file.Length != FileLength || reader.ReadInt32() != Magic)
                throw new IOException("Invalid canonical terrain capture header, level or size.");
            int level = reader.ReadInt32();
            float spacing = reader.ReadSingle();
            ulong systemSeed = reader.ReadUInt64();
            ulong bodySeed = reader.ReadUInt64();
            float nominalRadius = reader.ReadSingle();
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
            var values = new short[Count];
            for (int i = 0; i < values.Length; i++) values[i] = reader.ReadInt16();
            return new StockTerrainSurfaceSurvey(level, spacing, systemSeed, bodySeed, nominalRadius, x, y, z, values);
        }

        internal double? Radius(TerrainAuthoredSurfaceSample sample)
            => sample.SystemSeed == SystemSeed && sample.BodySeed == BodySeed && sample.NominalRadius == NominalRadius
                ? Radius(sample.DirectionX, sample.DirectionY, sample.DirectionZ) : null;

        internal double? Radius(double x, double y, double z)
        {
            double inner = 0, outer = double.PositiveInfinity;
            if (!Clip(X, x, ref inner, ref outer) || !Clip(Y, y, ref inner, ref outer)
                || !Clip(Z, z, ref inner, ref outer))
                return null;

            Span<double> boundaries = stackalloc double[3 * (Width - 2) + 2];
            boundaries[0] = inner;
            boundaries[1] = outer;
            int count = 2;
            InsertBoundaries(X, x, inner, outer, boundaries, ref count);
            InsertBoundaries(Y, y, inner, outer, boundaries, ref count);
            InsertBoundaries(Z, z, inner, outer, boundaries, ref count);

            bool sawAirCell = false;
            double epsilon = BoundaryEpsilon * Spacing;
            for (int i = count - 1; i > 0; i--)
            {
                double current = boundaries[i], next = boundaries[i - 1];
                if (next == current) continue;
                if (current - next <= epsilon) return null;
                double inside = (current + next) * 0.5;
                int ix = CellIndex(inside, X, x), iy = CellIndex(inside, Y, y), iz = CellIndex(inside, Z, z);
                if (ix < 0 || ix >= Width - 1 || iy < 0 || iy >= Width - 1 || iz < 0 || iz >= Width - 1)
                    return null;

                var cell = new Cell(density, ix + iy * Width + iz * Width * Width);
                if (cell.Minimum > 0)
                {
                    sawAirCell = true;
                }
                else
                {
                    if (!sawAirCell || !cell.IncreasesOutward(x, y, z)) return null;
                    double DensityAt(double radius) => cell.Interpolate(
                        Local(radius, X, x) - ix, Local(radius, Y, y) - iy, Local(radius, Z, z) - iz);
                    if (DensityAt(current) <= 0) return null;
                    if (DensityAt(next) <= 0)
                    {
                        // Every earlier cell was proven air along the covered ray. The crossing cell's
                        // density is strictly increasing outward, giving exactly one bracketed zero.
                        for (int iteration = 0; iteration < 18; iteration++)
                        {
                            double middle = (current + next) * 0.5;
                            if (DensityAt(middle) > 0) current = middle;
                            else next = middle;
                        }
                        return (current + next) * 0.5;
                    }
                }
            }
            return null;
        }

        private void InsertBoundaries(double centre, double direction, double inner, double outer,
            Span<double> sorted, ref int count)
        {
            if (direction == 0) return;
            for (int gridLine = 1; gridLine < Width - 1; gridLine++)
            {
                double crossing = (centre + (gridLine - GridHalf) * Spacing) / direction;
                if (crossing <= inner || crossing >= outer) continue;
                int index = count++;
                while (sorted[index - 1] > crossing)
                {
                    sorted[index] = sorted[index - 1];
                    index--;
                }
                sorted[index] = crossing;
            }
        }

        private bool Clip(double centre, double direction, ref double inner, ref double outer)
        {
            double low = centre - GridHalf * Spacing, high = centre + GridHalf * Spacing;
            if (direction == 0) return low < 0 && high > 0;
            double a = low / direction, b = high / direction;
            inner = Math.Max(inner, Math.Min(a, b));
            outer = Math.Min(outer, Math.Max(a, b));
            return inner < outer;
        }

        private double Local(double radius, double centre, double direction)
            => (radius * direction - centre) / Spacing + GridHalf;

        private int CellIndex(double radius, double centre, double direction)
            => (int)Math.Floor(Local(radius, centre, direction));

        private readonly struct Cell
        {
            private readonly int a, b, c, d, e, f, g, h;

            internal Cell(short[] values, int index)
            {
                a = values[index];
                b = values[index + 1];
                c = values[index + Width];
                d = values[index + Width + 1];
                index += Width * Width;
                e = values[index];
                f = values[index + 1];
                g = values[index + Width];
                h = values[index + Width + 1];
            }

            internal int Minimum => Math.Min(Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Min(Math.Min(e, f), Math.Min(g, h)));

            internal bool IncreasesOutward(double x, double y, double z)
                => LowerSlope(x, b - a, d - c, f - e, h - g)
                    + LowerSlope(y, c - a, d - b, g - e, h - f)
                    + LowerSlope(z, e - a, f - b, g - c, h - d) > 0;

            // Each trilinear partial derivative is a convex combination of its four parallel edges.
            private static double LowerSlope(double direction, int a, int b, int c, int d)
                => Math.Min(Math.Min(direction * a, direction * b), Math.Min(direction * c, direction * d));

            internal double Interpolate(double x, double y, double z)
            {
                double bottom = Lerp(Lerp(a, b, x), Lerp(c, d, x), y);
                double top = Lerp(Lerp(e, f, x), Lerp(g, h, x), y);
                return Lerp(bottom, top, z);
            }

            private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Max(0, Math.Min(t, 1));
        }
    }
}
