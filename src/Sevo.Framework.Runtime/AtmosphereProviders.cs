using System;
using System.Collections.Generic;
using System.Numerics;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public readonly struct AtmosphereQuery
    {
        internal AtmosphereQuery(BodyEnvironmentTarget body, Vector3 position, double seconds, float stockDensity)
            => (Body, BodyLocalPosition, SimulationSeconds, StockDensity) = (body, position, seconds, stockDensity);

        public BodyEnvironmentTarget Body { get; }
        public Vector3 BodyLocalPosition { get; }
        public double SimulationSeconds { get; }
        public float StockDensity { get; }
    }

    public readonly struct AtmosphereSample
    {
        public AtmosphereSample(float relativeDensity, Vector3? bodyLocalAirVelocity)
            => (RelativeDensity, BodyLocalAirVelocity) = (relativeDensity, bodyLocalAirVelocity);

        public float RelativeDensity { get; }
        /// <summary>Velocity relative to the body, in metres per second. Null means unknown.</summary>
        public Vector3? BodyLocalAirVelocity { get; }
    }

    /// <summary>Registers optional atmospheric fields for explicitly seeded bodies.</summary>
    public static class AtmosphereProviders
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<BodyEnvironmentTarget, (string Key, Func<AtmosphereQuery, AtmosphereSample?> Sample)> Providers
            = new Dictionary<BodyEnvironmentTarget, (string, Func<AtmosphereQuery, AtmosphereSample?>)>();
        [ThreadStatic] private static bool sampling;

        internal static Action<string>? Warn { get; set; }

        /// <summary>Registers a read-only callback during mod startup. Return null to use stock density.</summary>
        public static void Register(string ownerId, string localName, BodyEnvironmentTarget body,
            Func<AtmosphereQuery, AtmosphereSample?> sample)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (body.StarSeed == 0) throw new ArgumentOutOfRangeException(nameof(body));
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            lock (Gate)
            {
                if (Keys.Contains(key) || Providers.ContainsKey(body))
                    throw new InvalidOperationException("Atmosphere provider key or body is already claimed: " + key);
                Providers.Add(body, (key, sample));
                Keys.Add(key);
            }
        }

        internal static bool Resolve(BodyEnvironmentTarget body, Vector3 position, double seconds, float stockDensity,
            out AtmosphereSample sample)
        {
            sample = new AtmosphereSample(stockDensity, null);
            if (sampling) return false;
            (string Key, Func<AtmosphereQuery, AtmosphereSample?> Sample) provider;
            lock (Gate)
                if (!Providers.TryGetValue(body, out provider)) return true;

            sampling = true;
            try
            {
                var replacement = provider.Sample(new AtmosphereQuery(body, position, seconds, stockDensity));
                if (!replacement.HasValue) return true;
                var value = replacement.Value;
                if (!float.IsFinite(value.RelativeDensity) || value.RelativeDensity < 0f
                    || (value.BodyLocalAirVelocity is Vector3 velocity && !IsFinite(velocity)))
                    throw new InvalidOperationException("Returned invalid density or air velocity.");
                sample = value;
                return true;
            }
            catch (Exception exception)
            {
                Warn?.Invoke("Atmosphere provider " + provider.Key + " rejected: " + exception.Message);
                return false;
            }
            finally { sampling = false; }
        }

        internal static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
