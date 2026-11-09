using System;

namespace Sevo.Example.TerrainEditProbe
{
    internal enum ProbeOperation { Snapshot, Cut, Fill }

    internal static class TerrainEvidence
    {
        internal const int MaximumRequestLength = 256;

        internal static bool TryParseRequest(string text, string session, out Guid id, out ProbeOperation operation)
        {
            id = default;
            operation = default;
            if (text.Length > MaximumRequestLength) return false;
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !string.Equals(parts[0], session, StringComparison.Ordinal)
                || !Guid.TryParseExact(parts[1], "N", out id) || id == Guid.Empty)
                return false;

            switch (parts[2])
            {
                case "snapshot": operation = ProbeOperation.Snapshot; return true;
                case "cut": operation = ProbeOperation.Cut; return true;
                case "fill": operation = ProbeOperation.Fill; return true;
                default: return false;
            }
        }

        internal static DensityChange Compare(ReadOnlySpan<short> before, ReadOnlySpan<short> after)
        {
            if (before.Length != after.Length) throw new ArgumentException("Density snapshots must have the same lattice size.");
            int changed = 0, removedSolid = 0, addedSolid = 0, increased = 0, decreased = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                changed++;
                if (before[i] < after[i]) increased++;
                else decreased++;
                if (before[i] < 0 && after[i] >= 0) removedSolid++;
                if (before[i] >= 0 && after[i] < 0) addedSolid++;
            }
            return new DensityChange(changed, removedSolid, addedSolid, increased, decreased);
        }
    }

    internal readonly struct DensityChange
    {
        internal readonly int Changed;
        internal readonly int RemovedSolid;
        internal readonly int AddedSolid;
        internal readonly int Increased;
        internal readonly int Decreased;

        internal DensityChange(int changed, int removedSolid, int addedSolid, int increased, int decreased)
        {
            Changed = changed;
            RemovedSolid = removedSolid;
            AddedSolid = addedSolid;
            Increased = increased;
            Decreased = decreased;
        }

        internal bool IsAccepted(ProbeOperation operation) => operation switch
        {
            ProbeOperation.Cut => RemovedSolid > 0 && AddedSolid == 0 && Decreased == 0,
            ProbeOperation.Fill => AddedSolid > 0 && RemovedSolid == 0 && Increased == 0,
            _ => false
        };
    }
}
