using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Terrain.Planet;

namespace Sevo.Framework.Runtime
{
    public enum AtmosphereSampleResult { Unavailable, InvalidInput, BodyUnavailable, Sampled, ProviderRejected }

    /// <summary>Read-only atmospheric density in native relative units for one loaded rocky body.</summary>
    public static class AtmosphereSampling
    {
        private static Func<float, float, float>? falloff;

        public static bool IsAvailable => falloff != null;
        public static bool IsSystemPositionSamplingAvailable { get; internal set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Enable(Action<string> warn)
        {
            var method = typeof(PlanetCommon).GetMethod("GetAtmosphereFalloffFactor",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(float), typeof(float) }, null)
                ?? throw new MissingMethodException("PlanetCommon.GetAtmosphereFalloffFactor");
            falloff = (Func<float, float, float>)method.CreateDelegate(typeof(Func<float, float, float>));
            AtmosphereProviders.Warn = warn;
        }

        internal static void Disable()
        {
            falloff = null;
            IsSystemPositionSamplingAvailable = false;
            AtmosphereProviders.Warn = null;
        }

        /// <summary>Samples stock density and an optional body-scoped provider at the caller's simulation time.</summary>
        public static AtmosphereSampleResult Sample(BodyEnvironmentTarget target, Vector3 bodyLocalPosition,
            double simulationSeconds, out AtmosphereSample sample)
        {
            sample = default;
            if (!double.IsFinite(simulationSeconds) || simulationSeconds < 0d) return AtmosphereSampleResult.InvalidInput;
            var result = SampleDensity(target, bodyLocalPosition, out float density);
            if (result != AtmosphereSampleResult.Sampled) return result;
            return AtmosphereProviders.Resolve(target,
                new System.Numerics.Vector3(bodyLocalPosition.x, bodyLocalPosition.y, bodyLocalPosition.z),
                simulationSeconds, density, out sample) ? result : AtmosphereSampleResult.ProviderRejected;
        }

        /// <summary>Samples at a native system coordinate while keeping wind expressed relative to the selected body.</summary>
        public static AtmosphereSampleResult SampleSystemPosition(BodyEnvironmentTarget target, Vector3l position,
            double simulationSeconds, out AtmosphereSample sample)
        {
            sample = default;
            if (!IsAvailable || !IsSystemPositionSamplingAvailable) return AtmosphereSampleResult.Unavailable;
            if (target.StarSeed == 0 || !double.IsFinite(simulationSeconds) || simulationSeconds < 0d)
                return AtmosphereSampleResult.InvalidInput;
            return SampleSystemPositionNative(target, position, simulationSeconds, out sample);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static AtmosphereSampleResult SampleSystemPositionNative(BodyEnvironmentTarget target, Vector3l position,
            double simulationSeconds, out AtmosphereSample sample)
        {
            sample = default;
            if (!WorldSurfaceQuery.IsSafePhase) return AtmosphereSampleResult.Unavailable;
            var body = LoadedRockyBody.Find(target.StarSeed, target.BodySeed);
            if (body == null || body.planetGenerationId == 0 || !SkyTransformData.datas.IsCreated
                || body.transformId <= 0 || body.transformId >= SkyTransformData.datas.Length)
                return AtmosphereSampleResult.BodyUnavailable;
            return Sample(target, SkyTransformData.datas[body.transformId].SystemToLocal(position), simulationSeconds, out sample);
        }

        /// <summary>Samples native relative density, including zero in vacuum or at or below the ocean surface.</summary>
        /// <remarks>Position is in metres from the body's center in its local frame. Call on the game thread during a safe phase of a loaded solo session.</remarks>
        public static AtmosphereSampleResult SampleDensity(BodyEnvironmentTarget target, Vector3 bodyLocalPosition, out float density)
        {
            density = 0f;
            var evaluateFalloff = falloff;
            if (evaluateFalloff == null) return AtmosphereSampleResult.Unavailable;
            if (target.StarSeed == 0) return AtmosphereSampleResult.InvalidInput;
            float distance = bodyLocalPosition.magnitude;
            if (!float.IsFinite(distance)) return AtmosphereSampleResult.InvalidInput;
            return SampleLoadedBody(target, distance, evaluateFalloff, out density);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static AtmosphereSampleResult SampleLoadedBody(BodyEnvironmentTarget target, float distance,
            Func<float, float, float> evaluateFalloff, out float density)
        {
            density = 0f;
            if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle)
                return AtmosphereSampleResult.Unavailable;

            var selected = LoadedRockyBody.Find(target.StarSeed, target.BodySeed);
            var rows = PlanetResources.PlanetGravityDatas;
            if (selected == null || selected.planetGenerationId == 0 || selected.transformId <= 0
                || !rows.IsCreated || selected.gravityId < 0 || selected.gravityId >= rows.Length)
                return AtmosphereSampleResult.BodyUnavailable;

            var row = rows[selected.gravityId];
            if (row.transformId != selected.transformId || row.planetGenerationId != selected.planetGenerationId
                || !float.IsFinite(row.radius) || row.radius <= 0f
                || !float.IsFinite(row.oceanRadius) || !float.IsFinite(row.atmosphereDensity) || row.atmosphereDensity < 0f)
                return AtmosphereSampleResult.BodyUnavailable;

            if (distance <= row.oceanRadius || row.atmosphereDensity == 0f)
                return AtmosphereSampleResult.Sampled;

            float value = row.atmosphereDensity * evaluateFalloff(distance, row.radius);
            if (!float.IsFinite(value) || value < 0f) return AtmosphereSampleResult.Unavailable;
            density = value;
            return AtmosphereSampleResult.Sampled;
        }
    }
}
