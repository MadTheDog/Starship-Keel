using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.LogicOutputProbe
{
    [BepInPlugin(Id, "Keel Logic Output Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.logicoutput";

        private void Awake()
        {
            ContentRegistration.RegisterFunctionalBlock(Id, "surface-sensor", 1, "Probe Surface Sensor",
                () => TabHull.HULL_A, context => new Sensor(context));
            LogicOutputProvider.Declare(Id, "surface-sensor");
        }

        private sealed class Sensor : FunctionalBlockBehavior
        {
            private readonly FunctionalBlockContext context;
            private float nextSample;
            private bool detected;

            internal Sensor(FunctionalBlockContext context) => this.context = context;
            public override void OnSpawn() => GameloopManager.OnTransformJobFinished += ReadSurface;
            public override void OnDespawn() => GameloopManager.OnTransformJobFinished -= ReadSurface;

            private void ReadSurface()
            {
                if (Time.unscaledTime < nextSample || !WorldSurfaceQuery.IsAvailable || !LogicOutputProvider.IsAvailable
                    || !context.TryGetPlacement(out var placement))
                    return;
                nextSample = Time.unscaledTime + 0.2f;
                Vector3 down = placement.TransformData.rotation * -placement.LocalUp;
                var result = WorldSurfaceQuery.Raycast(placement.WorldPosition, down, 40f, true, out var hit, placement.Parent);
                if (result is not (WorldSurfaceQueryResult.Hit or WorldSurfaceQueryResult.Miss)) return;
                bool found = result == WorldSurfaceQueryResult.Hit && hit.Kind is WorldSurfaceKind.Terrain or WorldSurfaceKind.Water;
                LogicOutputProvider.TrySetSignal(context, found);
                LogicOutputProvider.TrySetValue(context, found ? hit.Distance : 0f);
                if (found && !detected) LogicOutputProvider.TryEmitPulse(context);
                detected = found;
            }
        }
    }
}
