using System;

namespace Sevo.Framework.Runtime
{
    internal enum MachineWorkStep { None, Progressed, Completed }

    // One pending batch can cross its completion boundary only once.
    internal struct MachineWorkProgress
    {
        internal float Value { get; private set; }

        internal static bool IsValidRate(float rate) => float.IsFinite(rate) && rate >= 0.25f && rate <= 4f;

        internal MachineWorkStep Advance(bool powered, float completedSeconds, float duration, float rate)
        {
            if (!powered || Value >= 1f || !float.IsFinite(completedSeconds) || completedSeconds <= 0f
                || !float.IsFinite(duration) || duration <= 0f || !IsValidRate(rate))
                return MachineWorkStep.None;

            Value = (float)Math.Min(1d, Value + completedSeconds * (double)rate / duration);
            return Value >= 1f ? MachineWorkStep.Completed : MachineWorkStep.Progressed;
        }
    }
}
