using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UI;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public readonly struct NavigationMarkerSnapshot
    {
        internal NavigationMarkerSnapshot(string key, string name, VoxelEntityID anchorEntityId, Vector3 localPosition,
            Color32 color, bool showDistance)
        {
            Key = key;
            Name = name;
            AnchorEntityId = anchorEntityId;
            LocalPosition = localPosition;
            Color = color;
            ShowDistance = showDistance;
        }

        public string Key { get; }
        public string Name { get; }
        public VoxelEntityID AnchorEntityId { get; }
        public Vector3 LocalPosition { get; }
        public Color32 Color { get; }
        public bool ShowDistance { get; }
    }

    /// <summary>Displays owned HUD markers attached to live grid transforms.</summary>
    public static class NavigationMarkerProvider
    {
        private const int MaxMarkers = 64;
        private static readonly SortedDictionary<string, Marker> Markers = new SortedDictionary<string, Marker>(StringComparer.Ordinal);
        private static readonly List<string> Expired = new List<string>();
        private static Action<string>? warn;
        private static UI_MainWaypoints? widgetOwner;

        /// <summary>Creates an entity-relative HUD marker and returns its disposable lifetime handle.</summary>
        public static bool TryAdd(string ownerId, string localName, string name, VoxelEntity anchor, Vector3 localPosition,
            Color32 color, bool showDistance, out IDisposable? handle)
        {
            handle = null;
            var key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A marker name is required.", nameof(name));
            if (!ValidCoordinate(localPosition.x) || !ValidCoordinate(localPosition.y) || !ValidCoordinate(localPosition.z))
                throw new ArgumentOutOfRangeException(nameof(localPosition));
            if (warn == null || !NavigationWorld.CanRead || !NavigationWorld.TryReadTransform(anchor, out var transform)
                || Markers.Count >= MaxMarkers || Markers.ContainsKey(key)) return false;

            var marker = new Marker(new NavigationMarkerSnapshot(key, name, anchor.ID, localPosition, color, showDistance),
                anchor, transform);
            Markers.Add(key, marker);
            handle = marker;
            return true;
        }

        /// <summary>Returns current owned marker metadata in key order. Call from the game thread.</summary>
        public static IReadOnlyList<NavigationMarkerSnapshot> ReadMarkers()
        {
            if (warn == null || !NavigationWorld.CanRead) return Array.Empty<NavigationMarkerSnapshot>();
            var snapshots = new List<NavigationMarkerSnapshot>(Markers.Count);
            foreach (var marker in Markers.Values)
                if (!marker.IsDisposed && marker.HasLiveAnchor()) snapshots.Add(marker.Snapshot);
            return snapshots.AsReadOnly();
        }

        internal static void Install(Harmony harmony, Action<string> warning)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_MainWaypoints), nameof(UI_MainWaypoints.LateUpdate)),
                postfix: new HarmonyMethod(typeof(NavigationMarkerProvider), nameof(DrawMarkers)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(UI_MainWaypoints), nameof(UI_MainWaypoints.OnDestroy)),
                prefix: new HarmonyMethod(typeof(NavigationMarkerProvider), nameof(BeforeHudDestroy)));
            warn = warning;
        }

        internal static void Disable()
        {
            warn = null;
            ReleaseWidgets();
            Markers.Clear();
        }

        private static bool ValidCoordinate(float coordinate)
            => float.IsFinite(coordinate) && Math.Abs(coordinate) <= 10_000_000f;

        private static void DrawMarkers(UI_MainWaypoints __instance, GameObject ___waypointTemplate)
        {
            if (warn == null || Markers.Count == 0) return;
            if (!ReferenceEquals(widgetOwner, __instance))
            {
                ReleaseWidgets();
                widgetOwner = __instance;
            }

            var canRead = NavigationWorld.CanRead;
            var player = canRead ? Dependency.PlayerTransform : null;
            var camera = player == null ? null : Dependency.RenderPipeline?.playerCamera;
            Expired.Clear();
            foreach (var marker in Markers.Values)
            {
                try
                {
                    if (marker.IsDisposed || canRead && !marker.HasLiveAnchor())
                    {
                        marker.ReleaseWidget();
                        Expired.Add(marker.Snapshot.Key);
                    }
                    else if (camera == null || player == null || ___waypointTemplate == null)
                        marker.Hide();
                    else
                        marker.Draw(__instance, ___waypointTemplate, camera, player.position);
                }
                catch (Exception exception)
                {
                    marker.ReleaseWidget();
                    Expired.Add(marker.Snapshot.Key);
                    warn("HUD marker '" + marker.Snapshot.Key + "' disabled: " + exception.Message);
                }
            }
            foreach (var key in Expired) Markers.Remove(key);
            Expired.Clear();
        }

        private static void BeforeHudDestroy(UI_MainWaypoints __instance)
        {
            if (ReferenceEquals(widgetOwner, __instance)) ReleaseWidgets();
        }

        private static void ReleaseWidgets()
        {
            foreach (var marker in Markers.Values) marker.ReleaseWidget();
            widgetOwner = null;
        }

        private sealed class Marker : IDisposable
        {
            private readonly VoxelEntity anchor;
            private readonly int transform;
            private UI_WaypointWidget? widget;
            private int disposed;

            internal Marker(NavigationMarkerSnapshot snapshot, VoxelEntity anchor, int transform)
            {
                Snapshot = snapshot;
                this.anchor = anchor;
                this.transform = transform;
            }

            internal NavigationMarkerSnapshot Snapshot { get; }
            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
            internal bool HasLiveAnchor()
                => NavigationWorld.TryReadTransform(anchor, out var current) && current == transform;

            internal void Draw(UI_MainWaypoints owner, GameObject template, Camera camera, Vector3 playerPosition)
            {
                if (widget == null)
                {
                    var clone = UnityEngine.Object.Instantiate(template, owner.transform, false);
                    widget = clone.GetComponent<UI_WaypointWidget>();
                    if (widget == null)
                    {
                        UnityEngine.Object.Destroy(clone);
                        throw new InvalidOperationException("Stock HUD template has no waypoint widget.");
                    }
                }
                widget.OnUpdate(new UI_MainWaypoints.WaypointData
                {
                    transformId = transform,
                    position = Snapshot.LocalPosition,
                    color = Snapshot.Color,
                    showDistance = Snapshot.ShowDistance
                }, camera, playerPosition);
            }

            internal void Hide()
            {
                if (widget != null) widget.SetActive(false);
            }

            internal void ReleaseWidget()
            {
                if (widget != null)
                {
                    widget.SetActive(false);
                    UnityEngine.Object.Destroy(widget.gameObject);
                }
                widget = null;
            }

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        }
    }

    internal static class NavigationWorld
    {
        internal static bool CanRead => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe
            && FunctionalCommandState.IsSolo && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
            && !ReferenceEquals(BlockEntityManager.instance, null);

        internal static bool TryReadTransform(VoxelEntity anchor, out int transform)
        {
            transform = 0;
            if (anchor == null || !ReferenceEquals(anchor.ID.Entity, anchor) || !SkyTransformData.datas.IsCreated)
                return false;
            int id = anchor.transformId;
            if (id <= 0 || id >= SkyTransformData.datas.Length) return false;
            transform = id;
            return true;
        }
    }
}
