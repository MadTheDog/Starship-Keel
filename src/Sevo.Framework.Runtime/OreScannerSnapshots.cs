using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sevo.Framework.Sdk;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Observes the native ore scanner's completed widget groups.</summary>
    public static class OreScannerSnapshots
    {
        private static readonly object Gate = new object();
        private static readonly SortedDictionary<string, Action<IReadOnlyList<OreScanGroup>>> Observers =
            new SortedDictionary<string, Action<IReadOnlyList<OreScanGroup>>>(StringComparer.Ordinal);
        private static Action<string>? warning;
        private static bool notifying;
        private static int gameThreadId;

        public static bool IsAvailable { get; private set; }
        internal static void Disable() => IsAvailable = false;

        /// <summary>Registers a game-thread observer for copied groups during active scans.</summary>
        public static void Register(string ownerId, string localName, Action<IReadOnlyList<OreScanGroup>> observer)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            lock (Gate)
            {
                if (Observers.ContainsKey(key)) throw new InvalidOperationException("Duplicate ore scanner observer: " + key);
                Observers.Add(key, observer);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            var target = typeof(OreScannerResources).GetMethod(nameof(OreScannerResources.DisposeWidgetJob), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(OreScannerResources.DisposeWidgetJob));
            var postfix = typeof(OreScannerSnapshots).GetMethod(nameof(AfterDisposeWidgetJob), BindingFlags.NonPublic | BindingFlags.Static)!;
            warning = reportWarning;
            gameThreadId = Environment.CurrentManagedThreadId;
            harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            IsAvailable = true;
        }

        private static void AfterDisposeWidgetJob()
        {
            if (!IsAvailable || notifying) return;
            if (Environment.CurrentManagedThreadId != gameThreadId)
            {
                Disable();
                warning!("Ore scanner snapshots disabled: native scanner publication moved off the game thread.");
                return;
            }
            KeyValuePair<string, Action<IReadOnlyList<OreScanGroup>>>[] observers;
            lock (Gate)
            {
                if (Observers.Count == 0) return;
                observers = Observers.ToArray();
            }
            notifying = true;
            try
            {
                if (!OreScannerResources.IsScannerRunning) return;
                var widgets = OreScannerResources.WidgetResults;
                if (!widgets.IsCreated) return;
                var groups = new OreScanGroup[widgets.Length];
                for (int index = 0; index < groups.Length; index++)
                {
                    var widget = widgets[index];
                    groups[index] = new OreScanGroup(
                        new System.Numerics.Vector3(widget.worldPos.x, widget.worldPos.y, widget.worldPos.z),
                        new System.Numerics.Vector3(widget.worldPosMin.x, widget.worldPosMin.y, widget.worldPosMin.z),
                        new System.Numerics.Vector3(widget.worldPosMax.x, widget.worldPosMax.y, widget.worldPosMax.z),
                        widget.color, widget.texId, widget.count);
                }
                var snapshot = Array.AsReadOnly(groups);
                foreach (var observer in observers)
                {
                    try { observer.Value(snapshot); }
                    catch (Exception exception)
                    {
                        lock (Gate) Observers.Remove(observer.Key);
                        warning!("Ore scanner observer disabled for this process: " + observer.Key + ". " + exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                IsAvailable = false;
                warning!("Ore scanner snapshots disabled: " + exception.Message);
            }
            finally { notifying = false; }
        }
    }
}
