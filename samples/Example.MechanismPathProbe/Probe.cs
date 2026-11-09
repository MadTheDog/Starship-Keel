using System.Numerics;
using BepInEx;
using Sevo.Framework.Runtime;

namespace Sevo.Example.MechanismPathProbe
{
    [BepInPlugin(Id, "Keel Mechanism Path Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.mechanismpath";

        private void Awake()
        {
            var label = Config.Bind("Probe", "SliderLabel", "keel-path-probe", "Only the tagged slider follows the test path.");
            PrepareAutomation();
            MechanismPathProvider.Register(Id, "corner", context =>
            {
                if (!Selected(context, label.Value) || context.TravelLength < 0.1f) return null;
                var corner = Vector3.UnitX * (context.TravelLength * 0.5f);
                var end = corner + Vector3.UnitZ * (context.TravelLength * 0.5f);
                return new MechanismPath(new[] { Vector3.Zero, corner, end });
            }, context => Selected(context, label.Value));
        }
    }
}
