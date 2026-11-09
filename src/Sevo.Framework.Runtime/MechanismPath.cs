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
