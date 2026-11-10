using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>One invocation immediately before the native rigidbody simulation is scheduled.</summary>
    public readonly struct GridPhysicsContext
    {
        internal long Token { get; }
        public float DeltaTime { get; }

        internal GridPhysicsContext(long token, float deltaTime)
        {
            Token = token;
            DeltaTime = deltaTime;
        }
    }

    /// <summary>Registers callbacks on the managed pre-simulation phase of an audited solo session.</summary>
    public static class GridPhysicsStep
    {
        private const int ForceBudgetPerCallback = 64;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Subscription> Registrations = new Dictionary<string, Subscription>(StringComparer.Ordinal);
        private static Subscription[]? plan;
        private static Action<string>? warn;
        private static bool enabled;
        private static int lastFrame = -1;
        private static long nextToken;
        private static long activeToken;
        private static int remainingForces;

        public static bool IsAvailable => enabled;

        public static void Register(string ownerId, string localName, Action<GridPhysicsContext> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            lock (Gate)
            {
                if (plan != null || Registrations.ContainsKey(key))
                    throw new InvalidOperationException("Grid physics callback registration is closed or already registered: " + key);
                Registrations.Add(key, new Subscription(key, callback));
            }
        }

        internal static void Install(Harmony harmony, Action<string> logWarning)
        {
            var schedule = AccessTools.Method(typeof(SkyRigidbody), nameof(SkyRigidbody.ScheduleJob), Type.EmptyTypes);
            if (schedule == null || !schedule.IsStatic || schedule.ReturnType != typeof(void))
                throw new InvalidOperationException("Live rigidbody schedule method is unavailable.");
            GridForceService.Bind();
            harmony.Patch(schedule, prefix: new HarmonyMethod(typeof(GridPhysicsStep), nameof(BeforeSchedule)));
            warn = logWarning;
            enabled = true;
            lastFrame = -1;
        }

        internal static void Disable()
        {
            enabled = false;
            activeToken = 0;
            warn = null;
        }

        private static bool IsSafePhase => enabled && GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle && !PlayerState.InMenu;

        internal static bool CanApply(in GridPhysicsContext context)
            => remainingForces > 0 && CanRead(context);

        internal static bool CanRead(in GridPhysicsContext context)
            => context.Token != 0 && context.Token == activeToken && IsSafePhase;

        internal static void ConsumeForce() => remainingForces--;

        private static void BeforeSchedule()
        {
            if (!IsSafePhase || Time.frameCount == lastFrame) return;
            float deltaTime = Time.deltaTime;
            if (!float.IsFinite(deltaTime) || deltaTime <= 0.000001f) return;
            lastFrame = Time.frameCount;
            if (plan == null)
            {
                lock (Gate) plan ??= Registrations.Values.OrderBy(subscription => subscription.Key, StringComparer.Ordinal).ToArray();
            }

            foreach (var subscription in plan)
            {
                if (subscription.Disabled) continue;
                activeToken = ++nextToken;
                remainingForces = ForceBudgetPerCallback;
                try { subscription.Callback(new GridPhysicsContext(activeToken, deltaTime)); }
                catch (Exception exception)
                {
                    subscription.Disabled = true;
                    warn?.Invoke("Stopped grid physics callback " + subscription.Key + ": " + exception.Message);
                }
                finally { activeToken = 0; }
            }
        }

        private sealed class Subscription
        {
            internal string Key { get; }
            internal Action<GridPhysicsContext> Callback { get; }
            internal bool Disabled { get; set; }

            internal Subscription(string key, Action<GridPhysicsContext> callback)
            {
                Key = key;
                Callback = callback;
            }
        }
    }
}
