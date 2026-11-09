using BepInEx;
using Sevo.Framework.Runtime;
using UnityEngine;

namespace Sevo.Example.SeatInteractionProbe
{
    [BepInPlugin(Id, "Keel Seat Interaction Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.seatinteraction";

        private void Awake()
        {
            var seatLabel = Config.Bind("Probe", "SeatLabel", "keel-seat-probe", "Only this tagged seat uses the probe.");
            var left = Config.Bind("Probe", "LeftFoot", new Vector3(0.48f, 0.12f, 0.18f), "Seat-local left foot position.");
            var right = Config.Bind("Probe", "RightFoot", new Vector3(0.48f, 0.12f, -0.18f), "Seat-local right foot position.");
            var exit = Config.Bind("Probe", "Exit", new Vector3(1.3f, 0.8f, 0.4f), "Seat-local exit capsule center.");
            SeatedPoseTargetProvider.Register(Id, "foot-pose", context =>
                context.Seat.GetTagLabel("") == seatLabel.Value ? new SeatedFootTargets(left.Value, right.Value) : null);
            DismountPoseProvider.Register(Id, "exit", context =>
                TrySelectFixtureExit(context, out var candidate) ? candidate
                    : context.Seat.GetTagLabel("") == seatLabel.Value ? exit.Value : (Vector3?)null);
            PrepareDismountFixture();
        }
    }
}
