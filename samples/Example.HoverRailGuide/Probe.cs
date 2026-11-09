using System.Collections.Generic;
using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Example.HoverRailGuide
{
    [BepInPlugin(Id, "Keel Hover Rail Guidance Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.hoverrail";
        private readonly List<RailMarker> rails = new List<RailMarker>();
        private readonly List<GuidePod> pods = new List<GuidePod>();

        private void Awake()
        {
            RegisterMarker("straight", "Probe Rail Straight", 0);
            RegisterMarker("right", "Probe Rail Right Turn", 1);
            RegisterMarker("left", "Probe Rail Left Turn", -1);
            ContentRegistration.RegisterFunctionalBlock(Id, "pod", 1, "Probe Guide Pod", () => TabHull.HULL_A,
                context => new GuidePod(this, context));
            GridPhysicsStep.Register(Id, "guidance", Guide);
            PrepareFixture();
        }

        private void RegisterMarker(string name, string label, int turn)
            => ContentRegistration.RegisterFunctionalBlock(Id, name, 1, label, () => TabHull.HULL_A,
                context => new RailMarker(rails, context, turn));

        private void Guide(GridPhysicsContext step)
        {
            foreach (var pod in pods) pod.Guide(step);
        }

        private void Observe(string message)
        {
            Logger.LogInfo("KEEL_HOVER_RAIL " + message);
            RecordFixtureObservation(message);
        }

        private static bool TryPointVelocity(EntityRigidbody body, Vector3 worldPoint, out Vector3 velocity)
        {
            velocity = default;
            var handle = body.SkyRigidbody;
            if (!handle.IsValid || !SkyRigidbody.datas.IsCreated || handle.id >= SkyRigidbody.datas.Length
                || !float.IsFinite(worldPoint.sqrMagnitude)) return false;
            // The native data function expects a floating-origin world point in long coordinates.
            velocity = handle.Data.VelocityAtPoint(new Vector3l(worldPoint));
            return float.IsFinite(velocity.sqrMagnitude);
        }

        private sealed class RailMarker : FunctionalBlockBehavior
        {
            private readonly List<RailMarker> registry;
            private readonly FunctionalBlockContext context;
            private readonly int turn;

            internal RailMarker(List<RailMarker> registry, FunctionalBlockContext context, int turn)
            {
                this.registry = registry;
                this.context = context;
                this.turn = turn;
            }

            public override void OnSpawn() => registry.Add(this);
            public override void OnDespawn() => registry.Remove(this);

            internal bool TryRead(out RailFrame rail)
            {
                rail = default;
                if (!context.TryGetPlacement(out var brick) || brick.Parent == null || brick.Parent.Rigidbody == null) return false;
                float unit = brick.brickData.GridSize * brick.TransformData.scale;
                if (!float.IsFinite(unit) || unit <= 0f) return false;
                rail = new RailFrame(brick.Parent, brick.WorldPosition, brick.WorldForward, brick.WorldUp,
                    brick.WorldRight * (turn < 0 ? -1 : 1), unit, turn != 0);
                return true;
            }
        }

        private readonly struct RailFrame
        {
            internal ParentEntity Grid { get; }
            internal float Unit { get; }
            private readonly Vector3 origin;
            private readonly Vector3 forward;
            private readonly Vector3 up;
            private readonly Vector3 inward;
            private readonly bool curve;

            internal RailFrame(ParentEntity grid, Vector3 origin, Vector3 forward, Vector3 up,
                Vector3 inward, float unit, bool curve)
            {
                Grid = grid;
                this.origin = origin;
                this.forward = forward;
                this.up = up;
                this.inward = inward;
                Unit = unit;
                this.curve = curve;
            }

            internal (Vector3 Point, Vector3 Tangent) Project(Vector3 position)
            {
                Vector3 offset = position - origin;
                var sample = RailGeometry.Closest(curve, 4f * Unit, Vector3.Dot(offset, forward), Vector3.Dot(offset, inward));
                return (origin + forward * sample.Along + inward * sample.Cross + up * (1.5f * Unit),
                    forward * sample.AlongTangent + inward * sample.CrossTangent);
            }

            private (Vector3 Point, Vector3 Outward) Endpoint(bool end)
            {
                Vector3 ride = origin + up * (1.5f * Unit);
                if (!end) return (ride, -forward);
                return curve ? (ride + (forward + inward) * (4f * Unit), inward)
                    : (ride + forward * (4f * Unit), forward);
            }

            internal bool ConnectsNear(RailFrame next, Vector3 currentPoint, Vector3 nextPoint)
            {
                if (!ReferenceEquals(Grid, next.Grid) || Mathf.Abs(Unit - next.Unit) > 0.001f) return false;
                for (int i = 0; i < 2; i++)
                {
                    var from = Endpoint(i != 0);
                    if ((from.Point - currentPoint).sqrMagnitude > Unit * Unit) continue;
                    for (int j = 0; j < 2; j++)
                    {
                        var to = next.Endpoint(j != 0);
                        if ((from.Point - to.Point).sqrMagnitude <= 0.0025f * Unit * Unit
                            && (to.Point - nextPoint).sqrMagnitude <= Unit * Unit
                            && Vector3.Dot(from.Outward, to.Outward) < -0.98f)
                            return true;
                    }
                }
                return false;
            }
        }

        private sealed class GuidePod : FunctionalBlockBehavior
        {
            private readonly Probe probe;
            private readonly FunctionalBlockContext context;
            private RailMarker? captured;
            private bool armed;

            internal GuidePod(Probe probe, FunctionalBlockContext context)
            {
                this.probe = probe;
                this.context = context;
            }

            public override void OnSpawn() => probe.pods.Add(this);
            public override void OnDespawn()
            {
                Release("removed");
                probe.pods.Remove(this);
            }

            public override void OnSustainedInput(bool active)
            {
                if (active) armed = true;
                else Release("switch");
            }

            internal void Guide(GridPhysicsContext step)
            {
                if (!armed) return;
                if (!WorldSurfaceQuery.IsAvailable || !context.TryGetPlacement(out var brick)
                    || brick.Parent is not { } grid || grid == null || grid.Rigidbody is not { } body || body.isParented)
                {
                    Release("grid or query unavailable");
                    return;
                }

                Vector3 podPoint = brick.WorldPosition;
                var previous = captured;
                var best = previous;
                RailFrame selected = default;
                (Vector3 Point, Vector3 Tangent) guide = default;
                float distance = float.PositiveInfinity;

                if (previous != null)
                {
                    if (!previous.TryRead(out selected))
                    {
                        Release("marker removed");
                        return;
                    }
                    guide = selected.Project(podPoint);
                    distance = (guide.Point - podPoint).sqrMagnitude;
                }

                var currentRail = selected;
                var currentGuide = guide;
                float handoffMargin = previous == null ? 0f : 0.0025f * currentRail.Unit * currentRail.Unit;
                foreach (var marker in probe.rails)
                {
                    if (ReferenceEquals(marker, previous) || !marker.TryRead(out var rail)
                        || ReferenceEquals(rail.Grid, grid)) continue;
                    var candidate = rail.Project(podPoint);
                    float candidateDistance = (candidate.Point - podPoint).sqrMagnitude;
                    if (candidateDistance + handoffMargin >= distance || (previous != null
                        && !currentRail.ConnectsNear(rail, currentGuide.Point, candidate.Point))) continue;
                    best = marker;
                    selected = rail;
                    guide = candidate;
                    distance = candidateDistance;
                }

                if (best == null) return;
                float limit = (previous == null ? 1.2f : 2.25f) * selected.Unit;
                if (distance > limit * limit)
                {
                    if (previous != null) Release("out of range");
                    return;
                }

                Vector3 error = guide.Point - podPoint;
                if (distance > 0.0025f)
                {
                    var sight = WorldSurfaceQuery.Raycast(podPoint, error, Mathf.Sqrt(distance), false, out _, grid);
                    if (sight != WorldSurfaceQueryResult.Miss)
                    {
                        if (previous != null) Release("guide obstructed");
                        return;
                    }
                }

                if (!TryPointVelocity(body, podPoint, out var podVelocity)
                    || !TryPointVelocity(selected.Grid.Rigidbody, guide.Point, out var trackVelocity))
                {
                    if (previous != null) Release("velocity unavailable");
                    return;
                }
                Vector3 relativeVelocity = podVelocity - trackVelocity;
                Vector3 acceleration = 18f * Vector3.ProjectOnPlane(error, guide.Tangent)
                    - 7f * Vector3.ProjectOnPlane(relativeVelocity, guide.Tangent);
                acceleration = Vector3.ClampMagnitude(acceleration, 30f);
                if (acceleration.sqrMagnitude > 0.000001f && !GridForceService.TryApplyAtGridPoint(step, grid,
                    grid.TransformData.WorldToLocal(podPoint), body.Mass * acceleration))
                {
                    if (previous != null) Release("force rejected");
                    return;
                }

                captured = best;
                if (previous == null) probe.Observe("captured");
                else if (!ReferenceEquals(previous, best)) probe.Observe("continuous segment handoff");
            }

            private void Release(string reason)
            {
                if (captured != null) probe.Observe("released: " + reason);
                captured = null;
                armed = false;
            }
        }
    }
}
