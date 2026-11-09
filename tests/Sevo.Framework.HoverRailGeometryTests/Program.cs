using Sevo.Example.HoverRailGuide;

void Near(float expected, float actual)
{
    if (!float.IsFinite(actual) || MathF.Abs(expected - actual) > 0.00001f)
        throw new Exception($"Expected {expected}, got {actual}.");
}

var straight = RailGeometry.Closest(false, 4f, 8f, 1f);
Near(4f, straight.Along);
Near(0f, straight.Cross);
Near(1f, straight.AlongTangent);
Near(0f, straight.CrossTangent);

var entry = RailGeometry.Closest(true, 4f, 0f, 0f);
Near(0f, entry.Along);
Near(0f, entry.Cross);
Near(straight.AlongTangent, entry.AlongTangent);
Near(straight.CrossTangent, entry.CrossTangent);

var turn = RailGeometry.Closest(true, 4f, 4f / MathF.Sqrt(2f), 4f - 4f / MathF.Sqrt(2f));
Near(1f, turn.AlongTangent * turn.AlongTangent + turn.CrossTangent * turn.CrossTangent);
Near(4f / MathF.Sqrt(2f), turn.Along);
Near(4f - 4f / MathF.Sqrt(2f), turn.Cross);

var exit = RailGeometry.Closest(true, 4f, 3.5f, 5f);
Near(4f, exit.Along);
Near(4f, exit.Cross);
Near(0f, exit.AlongTangent);
Near(1f, exit.CrossTangent);

var outside = RailGeometry.Closest(true, 4f, -1f, 8f);
Near(4f, outside.Cross);
Near(4f, outside.Along);
Console.WriteLine("Hover rail straight, quarter-turn, and join geometry passed.");
