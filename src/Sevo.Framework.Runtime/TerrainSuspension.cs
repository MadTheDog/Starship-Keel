using System;

namespace Sevo.Framework.Runtime
{
    public static class TerrainSuspension
    {
        /// <summary>Computes a compression-only spring force along a verified contact normal.</summary>
        public static bool TryCalculate(float separation, float relativeNormalSpeed, float restLength, float stiffness,
            float damping, out float force)
        {
            force = 0f;
            if (!float.IsFinite(separation) || !float.IsFinite(relativeNormalSpeed)
                || !float.IsFinite(restLength) || restLength <= 0f
                || !float.IsFinite(stiffness) || stiffness < 0f || !float.IsFinite(damping) || damping < 0f)
                return false;
            if (separation >= restLength || separation < 0f) return true;
            float value = stiffness * (restLength - separation) - damping * relativeNormalSpeed;
            if (!float.IsFinite(value)) return false;
            force = Math.Max(0f, value);
            return true;
        }
    }
}
