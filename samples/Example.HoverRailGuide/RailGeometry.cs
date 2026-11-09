using System;

namespace Sevo.Example.HoverRailGuide
{
    internal static class RailGeometry
    {
        private const float QuarterTurn = MathF.PI * 0.5f;

        // Along and Cross use the segment's oriented local frame. On a curve Cross points into the bend.
        internal static (float Along, float Cross, float AlongTangent, float CrossTangent) Closest(
            bool curve, float size, float along, float cross)
        {
            if (!curve) return (Math.Min(size, Math.Max(0f, along)), 0f, 1f, 0f);

            float radialCross = size - cross;
            float angle = MathF.Atan2(along, radialCross);
            if (angle < 0f || angle > QuarterTurn)
                angle = along > radialCross ? QuarterTurn : 0f;

            float sin = MathF.Sin(angle);
            float cos = MathF.Cos(angle);
            return (size * sin, size * (1f - cos), cos, sin);
        }
    }
}
