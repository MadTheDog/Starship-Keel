using System;

namespace Sevo.Framework.Runtime
{
    internal static class TerrainEditMath
    {
        // Negative density denotes solid terrain. Intersect the half-space with a bounded sphere.
        internal static float Combine(float existing, float boundary, float plane, bool fill)
        {
            if (boundary >= 0f) return existing;
            return fill
                ? Math.Min(existing, Math.Max(boundary, plane))
                : Math.Max(existing, -Math.Max(boundary, -plane));
        }
    }
}
