using Sevo.Example.TerrainEditProbe;

void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

const string current = "f0d32877e8ef44b09336be58a68b4cd01";
const string request = "61f78ea2cf9246a9a7917786203d7e05";
Require(TerrainEvidence.TryParseRequest($"{current} {request} cut", current, out var id, out var operation)
    && id.ToString("N") == request && operation == ProbeOperation.Cut, "Cut request and stable request ID");
Require(!TerrainEvidence.TryParseRequest($"old {request} cut", current, out _, out _), "Old session requests are rejected");
Require(!TerrainEvidence.TryParseRequest($"{current} {request} unknown", current, out _, out _), "Unsupported requests are rejected");
Require(!TerrainEvidence.TryParseRequest($"{current} {request} cut extra", current, out _, out _), "Ambiguous request is rejected");
Require(!TerrainEvidence.TryParseRequest($"{current} {new string('x', 250)} cut", current, out _, out _), "Oversize request is rejected");

var cut = TerrainEvidence.Compare(new short[] { -10, -20, 4 }, new short[] { 1, -2, 4 });
Require(cut.Changed == 2 && cut.RemovedSolid == 1 && cut.AddedSolid == 0 && cut.IsAccepted(ProbeOperation.Cut),
    "Cut evidence requires a solid-to-air transition and monotonic density");
var fill = TerrainEvidence.Compare(new short[] { 1, -2, 4 }, new short[] { -10, -20, 4 });
Require(fill.AddedSolid == 1 && fill.IsAccepted(ProbeOperation.Fill), "Fill evidence requires an air-to-solid transition");
Require(!TerrainEvidence.Compare(new short[] { -10, 1 }, new short[] { 1, 0 }).IsAccepted(ProbeOperation.Cut),
    "A direction error without a sign change still rejects the cut");
Require(!TerrainEvidence.Compare(new short[] { -10 }, new short[] { -5 }).IsAccepted(ProbeOperation.Cut),
    "A density adjustment alone does not prove removal");
Console.WriteLine("Terrain edit probe request and native density evidence checks passed.");
