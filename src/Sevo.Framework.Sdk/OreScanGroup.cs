using System.Numerics;

namespace Sevo.Framework.Sdk
{
    /// <summary>A copied scanner widget with positions in the game's current floating-origin frame.</summary>
    public readonly struct OreScanGroup
    {
        internal OreScanGroup(Vector3 center, Vector3 minimum, Vector3 maximum, uint packedColor, int symbolId, int count)
        {
            Center = center;
            Minimum = minimum;
            Maximum = maximum;
            PackedColor = packedColor;
            SymbolId = symbolId;
            Count = count;
        }

        public Vector3 Center { get; }
        public Vector3 Minimum { get; }
        public Vector3 Maximum { get; }
        public uint PackedColor { get; }
        public int SymbolId { get; }
        public int Count { get; }
    }
}
