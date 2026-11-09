using BepInEx;
using Sevo.Framework.Runtime;

namespace Sevo.Example.GridPhysicsProbe
{
    [BepInPlugin("example.keel.gridphysics", "Keel Grid Physics Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private bool observed;

        private void Awake() => GridPhysicsStep.Register("example.keel.gridphysics", "phase", OnStep);

        private void OnStep(GridPhysicsContext context)
        {
            if (observed) return;
            observed = true;
            Logger.LogInfo($"KEEL_GRID_PHYSICS_PROBE dt={context.DeltaTime:F6}");
        }
    }
}
