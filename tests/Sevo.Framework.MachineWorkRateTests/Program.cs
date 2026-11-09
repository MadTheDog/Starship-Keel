using Sevo.Framework.Runtime;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, 0f, -1f, 0.1f, 5f })
    Check(!MachineWorkProgress.IsValidRate(invalid), "Invalid multiplier admitted: " + invalid);
Check(MachineWorkProgress.IsValidRate(0.25f) && MachineWorkProgress.IsValidRate(4f), "Rate limits rejected");

var work = new MachineWorkProgress();
Check(work.Advance(false, 1f, 2f, 2f) == MachineWorkStep.None && work.Value == 0f,
    "An unpowered tick changed progress");
Check(work.Advance(true, float.NaN, 2f, 2f) == MachineWorkStep.None && work.Value == 0f,
    "An invalid completed tick changed progress");
Check(work.Advance(true, 0.5f, 2f, 2f) == MachineWorkStep.Progressed && work.Value == 0.5f,
    "A doubled powered tick advanced the wrong amount");
Check(work.Advance(true, 0.5f, 2f, 1f) == MachineWorkStep.Progressed && work.Value == 0.75f,
    "Removing a module changed already-earned progress");
Check(work.Advance(true, float.MaxValue, 2f, 4f) == MachineWorkStep.Completed && work.Value == 1f,
    "An overshoot did not clamp to one completed batch");
Check(work.Advance(true, 1f, 2f, 4f) == MachineWorkStep.None && work.Value == 1f,
    "One work item signaled completion twice");

Console.WriteLine("Powered machine work rate and completion checks passed.");
