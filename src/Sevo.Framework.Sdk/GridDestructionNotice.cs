using System;
using System.Collections.Generic;
using System.Linq;

namespace Sevo.Framework.Sdk
{
    /// <summary>Copied grid identity and classification immediately before stock gameplay destruction.</summary>
    public readonly struct GridDestructionNotice
    {
        internal GridDestructionNotice(string? gridKey, string? gridName, ulong starSeed, bool wasNpc, bool wasGeneratedWreck)
        {
            GridKey = gridKey ?? string.Empty;
            GridName = gridName ?? string.Empty;
            StarSeed = starSeed;
            WasNpc = wasNpc;
            WasGeneratedWreck = wasGeneratedWreck;
        }

        public string GridKey { get; }
        public string GridName { get; }
        public ulong StarSeed { get; }
        public bool WasNpc { get; }
        public bool WasGeneratedWreck { get; }
    }

    internal sealed class GridDestructionObservers
    {
        private readonly object gate = new object();
        private readonly SortedDictionary<string, Action<GridDestructionNotice>> registrations =
            new SortedDictionary<string, Action<GridDestructionNotice>>(StringComparer.Ordinal);
        private volatile KeyValuePair<string, Action<GridDestructionNotice>>[] observers =
            Array.Empty<KeyValuePair<string, Action<GridDestructionNotice>>>();

        internal bool HasObservers => observers.Length != 0;

        internal void Add(string ownerId, string localName, Action<GridDestructionNotice> observer)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            lock (gate)
            {
                if (registrations.ContainsKey(key))
                    throw new InvalidOperationException("Duplicate grid destruction observer: " + key);
                registrations.Add(key, observer);
                observers = registrations.ToArray();
            }
        }

        internal void PublishTransition(GridDestructionNotice? before, bool isDestroyed, Action<string> report)
        {
            if (!before.HasValue || !isDestroyed) return;
            foreach (var observer in observers)
            {
                try { observer.Value(before.Value); }
                catch (Exception exception)
                {
                    ReportSafely(report, "Grid destruction observer failed: " + observer.Key + ". " + exception.Message);
                }
            }
        }

        internal static void ReportSafely(Action<string> report, string message)
        {
            try { report(message); }
            catch (Exception) { }
        }
    }
}
