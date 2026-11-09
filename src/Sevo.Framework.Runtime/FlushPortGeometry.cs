using System;
using Unity.Mathematics;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    internal static class FlushPortGeometry
    {
        internal static bool IsAligned(Vector3 point, Vector3 normal, float3 minimum, float3 maximum)
        {
            const float faceTolerance = 0.005f;
            const float normalTolerance = 0.0001f;
            const float nativeInset = 0.01f;
            const float nativeProbe = 0.02f;
            if (!Finite(point) || !Finite(normal) || !math.all(math.isfinite(minimum)) || !math.all(math.isfinite(maximum)))
                return false;
            var n = new float3(normal.x, normal.y, normal.z);
            var p = new float3(point.x, point.y, point.z);
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(Math.Abs(n[axis]) - 1f) > normalTolerance
                    || Math.Abs(n[(axis + 1) % 3]) > normalTolerance || Math.Abs(n[(axis + 2) % 3]) > normalTolerance)
                    continue;
                float face = n[axis] > 0f ? minimum[axis] : maximum[axis];
                if (Math.Abs(p[axis] - face) > faceTolerance) return false;
                var inside = p + nativeProbe * n;
                return math.all(inside > minimum + nativeInset) && math.all(inside < maximum - nativeInset);
            }
            return false;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
