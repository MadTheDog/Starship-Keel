using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using UnityEngine;

namespace Sevo.Example.WorldSurfaceProbe
{
    [BepInPlugin("example.keel.worldsurface", "Keel World Surface Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private float nextProbeTime;
        private void Awake() => GameloopManager.OnTransformJobFinished += ProbeView;
        private void OnDestroy() => GameloopManager.OnTransformJobFinished -= ProbeView;

        private void ProbeView()
        {
            if (Time.unscaledTime < nextProbeTime || !WorldSurfaceQuery.IsAvailable) return;
            var camera = Camera.main;
            if (camera == null) return;
            nextProbeTime = Time.unscaledTime + 1f;

            var pose = camera.transform;
            var result = WorldSurfaceQuery.Raycast(pose.position, pose.forward, 128f, true, out var hit);
            if (result == WorldSurfaceQueryResult.Hit)
                Logger.LogInfo($"KEEL_SURFACE_PROBE {hit.Kind}, distance={hit.Distance:F2}, hasNormal={hit.HasNormal}, point={hit.Point}");
        }
    }
}
