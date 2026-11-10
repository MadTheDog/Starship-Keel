using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sevo.Framework.Runtime
{
    /// <summary>A fixed slider-local path in metres, starting at the stock home position.</summary>
    public sealed class MechanismPath
    {
        internal const float PositionTolerance = 0.00001f;
        private readonly Vector3[] vertices;
        private readonly float[] distances;

        public float Length => distances[distances.Length - 1];
        internal bool IsConnectedRail { get; private set; }

        /// <summary>Builds a fixed-facing rail from ordered slider-local junctions with straight or right-angle joins.</summary>
        /// <remarks>The caller resolves the stock pieces and supplies their centerline junctions in travel order.</remarks>
        public static MechanismPath FromConnectedRail(IEnumerable<Vector3> junctions)
        {
            var path = new MechanismPath(junctions);
            var points = path.vertices;
            for (int index = 2; index < points.Length; index++)
            {
                var previous = Vector3.Normalize(points[index - 1] - points[index - 2]);
                var next = Vector3.Normalize(points[index] - points[index - 1]);
                float alignment = Vector3.Dot(previous, next);
                const float tolerance = 0.0001f;
                if (Math.Abs(alignment) > tolerance && alignment < 1f - tolerance)
                    throw new ArgumentException("Rail joins must continue straight or turn 90 degrees.", nameof(junctions));
            }
            path.IsConnectedRail = true;
            return path;
        }

        public MechanismPath(IEnumerable<Vector3> offsets)
        {
            if (offsets == null) throw new ArgumentNullException(nameof(offsets));
            vertices = offsets.Take(66).ToArray();
            if (vertices.Length < 2 || vertices.Length > 65 || vertices[0] != Vector3.Zero)
                throw new ArgumentException("Supply 2 to 65 vertices starting at zero.", nameof(offsets));

            distances = new float[vertices.Length];
            for (int index = 1; index < vertices.Length; index++)
            {
                float segment = Vector3.Distance(vertices[index - 1], vertices[index]);
                float distance = distances[index - 1] + segment;
                if (!float.IsFinite(distance) || segment < 0.001f || distance <= distances[index - 1])
                    throw new ArgumentException("Path segments must have finite, distinct endpoints.", nameof(offsets));
                distances[index] = distance;
            }
        }

        public bool TryGetOffset(float distance, out Vector3 offset)
        {
            offset = default;
            if (!float.IsFinite(distance) || distance < 0f || distance > Length) return false;

            int vertex = Array.BinarySearch(distances, distance);
            if (vertex >= 0)
                offset = vertices[vertex];
            else
            {
                int next = ~vertex;
                float fraction = (distance - distances[next - 1]) / (distances[next] - distances[next - 1]);
                offset = Vector3.Lerp(vertices[next - 1], vertices[next], fraction);
            }
            return true;
        }

        internal bool TryGetRailDisplacement(float nativeDistance, float negativeExtension, out Vector3 displacement)
        {
            displacement = default;
            if (!TryGetOffset(negativeExtension, out var home)
                || !TryGetOffset(nativeDistance + negativeExtension, out var point))
                return false;
            displacement = point - home;
            return true;
        }

        internal float LimitStep(float distance, float target, float maximumStep)
        {
            if (target > distance)
            {
                for (int vertex = 1; vertex < distances.Length; vertex++)
                    if (distances[vertex] > distance + PositionTolerance)
                        return Math.Min(maximumStep, distances[vertex] - distance);
            }
            else if (target < distance)
            {
                for (int vertex = distances.Length - 2; vertex >= 0; vertex--)
                    if (distances[vertex] < distance - PositionTolerance)
                        return Math.Min(maximumStep, distance - distances[vertex]);
            }
            return maximumStep;
        }
    }
}
