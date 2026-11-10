using System;
using System.Numerics;

namespace Sevo.Framework.Runtime
{
    /// <summary>Computes a bounded aerodynamic energy input for a mod-owned thermal model.</summary>
    public static class AtmosphericHeating
    {
        /// <remarks>Velocities must share a frame. The mod supplies exposed area and a coefficient that
        /// converts stock relative density to its own thermal scale. This calculation does not apply damage.</remarks>
        public static bool TryCalculateEnergy(float relativeDensity, Vector3 pointVelocity, Vector3 airVelocity,
            float exposedArea, float coefficient, float seconds, float maximumEnergy, out float energy)
        {
            energy = 0f;
            if (!float.IsFinite(relativeDensity) || relativeDensity < 0f
                || !AtmosphereProviders.IsFinite(pointVelocity) || !AtmosphereProviders.IsFinite(airVelocity)
                || !float.IsFinite(exposedArea) || exposedArea < 0f || !float.IsFinite(coefficient) || coefficient < 0f
                || !float.IsFinite(seconds) || seconds < 0f || !float.IsFinite(maximumEnergy) || maximumEnergy < 0f)
                return false;

            double x = (double)pointVelocity.X - airVelocity.X;
            double y = (double)pointVelocity.Y - airVelocity.Y;
            double z = (double)pointVelocity.Z - airVelocity.Z;
            double squared = x * x + y * y + z * z;
            double input = 0.5d * relativeDensity * exposedArea * coefficient * squared * Math.Sqrt(squared) * seconds;
            energy = (float)Math.Min(input, maximumEnergy);
            return true;
        }
    }
}
