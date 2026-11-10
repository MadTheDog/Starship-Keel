using System;
using System.Collections.Generic;
using HarmonyLib;
using UI;
using UnityEngine;
using UnityEngine.UI;
using World;
using Object = UnityEngine.Object;

namespace Sevo.Framework.Runtime
{
    /// <summary>Displays and picks persistent waypoints in the stock system map's Orrery view.</summary>
    public static class PersistentWaypointMap
    {
        internal const float OrreryScale = 4e-7f;
        private static readonly Dictionary<string, Pin> Pins = new Dictionary<string, Pin>(StringComparer.Ordinal);
        private static SystemMap? owner;
        private static RectTransform? parent;
        private static Font? font;
        private static Action<string>? warn;
        private static (string Character, ulong Seed, ulong Star, long Revision) scope;
        private static Pin? selected;

        public static bool IsAvailable => warn != null && PersistentWaypoints.IsAvailable;

        /// <summary>Reads the current map pick on the game thread while the Orrery and the selected anchor are available.</summary>
        public static bool TryGetSelected(out PersistentWaypoint? waypoint)
        {
            waypoint = null;
            if (selected is not { } pin || !CanPick(pin)) return false;
            waypoint = pin.Waypoint;
            return true;
        }

        internal static void Install(Harmony harmony, Action<string> warning)
        {
            Patch(nameof(SystemMap.OnUpdate), nameof(Update));
            Patch("SetActive", nameof(OnMapStateChanged));
            Patch("OnDestroy", nameof(OnMapDestroyed));
            warn = warning;

            void Patch(string target, string hook)
                => harmony.Patch(AccessTools.DeclaredMethod(typeof(SystemMap), target, Type.EmptyTypes),
                    postfix: new HarmonyMethod(typeof(PersistentWaypointMap), hook));
        }

        internal static void Disable()
        {
            warn = null;
            Clear();
            font = null;
        }

        private static void OnMapStateChanged(SystemMap __instance)
        {
            if (!SystemMap.IsActive.Value) OnMapDestroyed(__instance);
        }

        private static void OnMapDestroyed(SystemMap __instance)
        {
            if (ReferenceEquals(owner, __instance)) Clear();
        }

        private static bool TryReadScope(out (string Character, ulong Seed, ulong Star, long Revision) current)
        {
            current = default;
            if (!IsAvailable || !PersistentWaypoints.TryScope(out var character, out var seed, out var star)
                || !SystemMap.IsActive.Value || !SystemMap.IsOrreryModeActive.Value
                || SystemMap.currentSystem.Value?.parentStar.seed != star)
                return false;
            current = (character, seed, star, PersistentWaypoints.Revision);
            return true;
        }

        private static bool CanPick(Pin pin)
            => TryReadScope(out var current) && scope == current && pin.Label != null
                && pin.Label.isActiveAndEnabled && Pins.TryGetValue(pin.Waypoint.Key, out var live)
                && ReferenceEquals(live, pin) && PersistentWaypoints.TryResolve(pin.Waypoint, out _);

        private static void Update(SystemMap __instance, Camera ___holomapCamera, RectTransform ___selectedPlanetWidget)
        {
            try
            {
                if (!TryReadScope(out var current) || ___holomapCamera == null || ___selectedPlanetWidget == null
                    || ___selectedPlanetWidget.parent is not RectTransform container || !container.gameObject.activeInHierarchy)
                {
                    Clear();
                    return;
                }
                if (!ReferenceEquals(owner, __instance) || parent != container || scope != current)
                {
                    Clear();
                    owner = __instance;
                    parent = container;
                    scope = current;
                    foreach (var waypoint in PersistentWaypoints.ReadCurrentSystem())
                        Pins.Add(waypoint.Key, new Pin(waypoint));
                }

                foreach (var pin in Pins.Values)
                {
                    if (PersistentWaypoints.TryResolve(pin.Waypoint, out var systemPosition))
                    {
                        var position = OrreryScale * systemPosition.ToVector3();
                        var viewport = ___holomapCamera.WorldToViewportPoint(position);
                        if (viewport.z > 0 && viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1)
                        {
                            var widget = UI_Common.GetWidgetPosition(___holomapCamera, position, Vector3.zero);
                            if (float.IsFinite(widget.x) && float.IsFinite(widget.y))
                            {
                                Draw(pin, ___selectedPlanetWidget, container, widget);
                                continue;
                            }
                        }
                    }
                    if (ReferenceEquals(selected, pin)) Select(null);
                    if (pin.Label != null) pin.Label.gameObject.SetActive(false);
                }
            }
            catch (Exception exception)
            {
                var warning = warn;
                Disable();
                warning?.Invoke("Persistent waypoint map disabled: " + exception.Message);
            }
        }

        private static void Draw(Pin pin, RectTransform template, RectTransform container, Vector2 position)
        {
            if (pin.Label == null)
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font == null) throw new InvalidOperationException("No built-in UI font is available.");
                var widget = new GameObject("Keel Waypoint", typeof(RectTransform), typeof(Text), typeof(Button));
                var rect = (RectTransform)widget.transform;
                rect.SetParent(container, false);
                // Native targets above the new pins keep their normal raycast priority.
                rect.SetAsFirstSibling();
                rect.anchorMin = template.anchorMin;
                rect.anchorMax = template.anchorMax;
                rect.sizeDelta = new Vector2(170, 22);
                pin.Label = widget.GetComponent<Text>();
                pin.Label.font = font;
                pin.Label.fontSize = 15;
                pin.Label.alignment = TextAnchor.MiddleCenter;
                pin.Label.supportRichText = false;
                pin.Label.text = "◆ " + pin.Waypoint.Name;
                pin.Label.color = new Color(0.5f, 0.9f, 1f);
                var button = widget.GetComponent<Button>();
                button.targetGraphic = pin.Label;
                button.transition = Selectable.Transition.None;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                button.onClick.AddListener(() =>
                {
                    if (SystemMap.planetHover.Value == null && CanPick(pin)) Select(pin);
                });
            }
            pin.Label.raycastTarget = SystemMap.planetHover.Value == null;
            pin.Label.rectTransform.anchoredPosition = position;
            pin.Label.gameObject.SetActive(true);
        }

        private static void Select(Pin? pin)
        {
            if (selected?.Label != null) selected.Label.color = new Color(0.5f, 0.9f, 1f);
            selected = pin;
            if (selected?.Label != null) selected.Label.color = new Color(1f, 0.85f, 0.35f);
        }

        private static void Clear()
        {
            foreach (var pin in Pins.Values)
            {
                if (pin.Label == null) continue;
                pin.Label.gameObject.SetActive(false);
                Object.Destroy(pin.Label.gameObject);
            }
            Pins.Clear();
            selected = null;
            owner = null;
            parent = null;
            scope = default;
        }

        private sealed class Pin
        {
            internal Pin(PersistentWaypoint waypoint) => Waypoint = waypoint;
            internal PersistentWaypoint Waypoint { get; }
            internal Text? Label { get; set; }
        }
    }
}
