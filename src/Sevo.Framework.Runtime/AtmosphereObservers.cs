using System;
using System.Collections.Generic;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct AtmosphereObservationPoint
    {
        public AtmosphereObservationPoint(Vector3l position, double simulationSeconds)
            => (Position, SimulationSeconds) = (position, simulationSeconds);
        public Vector3l Position { get; }
        public double SimulationSeconds { get; }
    }

    /// <summary>Feeds mod-owned local effects or instruments from the scoped atmosphere sampler.</summary>
    public static class AtmosphereObservers
    {
        private const int MaximumObservers = 32;
        private static readonly SortedDictionary<string, Observer> Observers = new(StringComparer.Ordinal);
        private static Observer[]? plan;
        private static Action<string>? warn;
        private static float nextTick;

        public static bool IsAvailable => warn != null && AtmosphereSampling.IsSystemPositionSamplingAvailable;

        /// <summary>Registers during plugin startup. The receiver gets null once when its active sample is lost.</summary>
        /// <remarks>Return null from locate while the mod's camera or instrument is inactive. Callbacks run on the game thread.</remarks>
        public static void Register(string ownerId, string localName, BodyEnvironmentTarget body,
            Func<AtmosphereObservationPoint?> locate, Action<AtmosphereSample?> receive)
        {
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (body.StarSeed == 0) throw new ArgumentOutOfRangeException(nameof(body));
            if (locate == null) throw new ArgumentNullException(nameof(locate));
            if (receive == null) throw new ArgumentNullException(nameof(receive));
            if (plan != null || Observers.Count >= MaximumObservers)
                throw new InvalidOperationException("Atmosphere observation registration is closed or at capacity.");
            Observers.Add(key, new Observer(key, body, locate, receive));
        }

        internal static void Enable(Action<string> warning)
        {
            warn = warning;
            nextTick = 0f;
        }

        internal static void Disable()
        {
            if (plan != null)
                foreach (var observer in plan) UpdateObserver(observer, false);
            warn = null;
            plan = null;
        }

        internal static void Tick()
        {
            if (!IsAvailable || !GameloopManager.IsMainThread || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.1f;
            bool canRead = WorldSurfaceQuery.IsSafePhase;
            if (plan == null)
            {
                if (!canRead) return;
                plan = new Observer[Observers.Count];
                Observers.Values.CopyTo(plan, 0);
            }
            foreach (var observer in plan) UpdateObserver(observer, canRead);
        }

        private static void UpdateObserver(Observer observer, bool canRead)
        {
            if (observer.Disabled) return;
            try
            {
                if (canRead && observer.Locate() is { } point
                    && AtmosphereSampling.SampleSystemPosition(observer.Body, point.Position, point.SimulationSeconds,
                        out var sample) == AtmosphereSampleResult.Sampled)
                {
                    observer.Active = true;
                    observer.Receive(sample);
                }
                else if (observer.Active)
                {
                    observer.Active = false;
                    observer.Receive(null);
                }
            }
            catch (Exception exception)
            {
                observer.Disabled = true;
                warn?.Invoke("Stopped atmosphere observer " + observer.Key + ": " + exception.Message);
                if (observer.Active)
                {
                    observer.Active = false;
                    try { observer.Receive(null); }
                    catch (Exception cleanup) { warn?.Invoke("Could not clear atmosphere observer " + observer.Key + ": " + cleanup.Message); }
                }
            }
        }

        private sealed class Observer
        {
            internal Observer(string key, BodyEnvironmentTarget body, Func<AtmosphereObservationPoint?> locate,
                Action<AtmosphereSample?> receive)
                => (Key, Body, Locate, Receive) = (key, body, locate, receive);
            internal string Key { get; }
            internal BodyEnvironmentTarget Body { get; }
            internal Func<AtmosphereObservationPoint?> Locate { get; }
            internal Action<AtmosphereSample?> Receive { get; }
            internal bool Active { get; set; }
            internal bool Disabled { get; set; }
        }
    }
}
