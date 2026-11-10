using System;
using System.Numerics;

namespace Sevo.Framework.Runtime
{
    /// <summary>Calculates force opposite the velocity of an exposed point relative to the air.</summary>
    public static class AtmosphericDrag
    {
        /// <remarks>Both velocities must use the same frame. The mod supplies the coefficient, including area
        /// and conversion from stock relative density to its chosen force scale.</remarks>
        public static bool TryCalculate(float density, Vector3 pointVelocity, Vector3 airVelocity,
            float coefficient, out Vector3 force)
        {
            force = default;
            if (!float.IsFinite(density) || density < 0f || !float.IsFinite(coefficient) || coefficient < 0f
                || !AtmosphereProviders.IsFinite(pointVelocity) || !AtmosphereProviders.IsFinite(airVelocity))
                return false;
            if (density == 0f || coefficient == 0f) return true;
            var relative = pointVelocity - airVelocity;
            var calculated = relative * (-0.5f * density * coefficient * relative.Length());
            if (!AtmosphereProviders.IsFinite(calculated)) return false;
            force = calculated;
            return true;
        }
    }
}
