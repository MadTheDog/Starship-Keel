using Sevo.Example.WreckClaimProbe;
using Sevo.Framework.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

var root = Path.Combine(AppContext.BaseDirectory, "wreck-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var profile = Guid.NewGuid();
var session = Guid.NewGuid();
var character = "WreckProbe-" + session.ToString("N").Substring(0, 8);
var body = Path.Combine(root, "fixture.sevo");
var metadata = Path.Combine(root, "fixture.xml");
var manifest = Path.Combine(root, "snapshot.json");
File.WriteAllText(body, "native wreck body");
File.WriteAllText(metadata, "native wreck metadata");
var expected = new WreckRecoverySnapshot(profile, session, character, "survival", 123UL, "NPC-21",
    (20L, -30L, 40L), (0f, 0f, 0f, 1f));

// A leftover unpublished temp file cannot stand in for a snapshot.
File.WriteAllText(manifest + ".tmp", "{");
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata));
expected.Publish(manifest, body, metadata);
var first = WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata);
var replay = WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata);
Require(first.Identity == expected.Identity && first.Star == 123UL
    && first.Position == expected.Position && first.Rotation == expected.Rotation
    && replay.Identity == first.Identity, "Exact restart must recover the same grid and native pose.");
Reject(() => expected.Publish(manifest, body, metadata));
Reject(() => WreckRecoverySnapshot.Open(manifest, Guid.NewGuid(), session, character, "survival", body, metadata));
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, Guid.NewGuid(), character, "survival", body, metadata));
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character + "-wrong", "survival", body, metadata));
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "creative", body, metadata));
File.AppendAllText(body, " modified");
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata));
File.WriteAllText(body, "native wreck body");
File.AppendAllText(metadata, " modified");
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata));
File.WriteAllText(metadata, "native wreck metadata");
File.WriteAllText(manifest, "{");
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata));
File.WriteAllText(manifest, new string('x', 5000));
Reject(() => WreckRecoverySnapshot.Open(manifest, profile, session, character, "survival", body, metadata));
Console.WriteLine("Wreck snapshot publication, replay, profile scope and integrity checks passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidOperationException or IOException or System.Runtime.Serialization.SerializationException) { return; }
    throw new Exception("Invalid wreck snapshot was accepted.");
}
// The journal can resume a partial native publication only when the existing bytes match.
var publication = Path.Combine(AppContext.BaseDirectory, "KeelWreckClaim-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(publication);
try
{
    var nativeBody = Path.Combine(publication, "wreck.sevo");
    var nativeMetadata = Path.Combine(publication, "wreck.xml");
    byte[] stagedBody = { 1, 2, 3 };
    byte[] stagedMetadata = { 4, 5, 6 };
    var path = Path.Combine(publication, "claim.bin");
    var claim = new WreckClaimJournal(profile, "Player", 123, "system", "NPC-21", (20L, -30L, 40L), (0f, 0f, 0f, 1f),
        WreckClaimFiles.Digest(stagedBody), WreckClaimFiles.Digest(stagedMetadata));
    claim.Publish(path);
    var reopened = WreckClaimJournal.Open(path, profile, "Player", 123);
    Require(reopened.Identity == "NPC-21" && reopened.System == "system" && reopened.Position == claim.Position,
        "Recovery lost the accepted target or transform.");
    Reject(() => WreckClaimJournal.Open(path, Guid.NewGuid(), "Player", 123));
    Reject(() => WreckClaimJournal.Open(path, profile, "Other player", 123));
    Reject(() => WreckClaimJournal.Open(path, profile, "Player", 124));
    Require(!WreckClaimJournal.IsIdentity("../other") && !WreckClaimJournal.IsIdentity("Com2"),
        "Unsafe stock save names must be rejected before retirement.");
    File.WriteAllBytes(nativeBody, stagedBody);
    WreckClaimFiles.Publish(nativeBody, stagedBody);
    WreckClaimFiles.Publish(nativeMetadata, stagedMetadata);
    if (!File.ReadAllBytes(nativeBody).SequenceEqual(stagedBody) || !File.ReadAllBytes(nativeMetadata).SequenceEqual(stagedMetadata))
        throw new Exception("Matching orphan publication did not resume.");
    File.WriteAllBytes(nativeBody, new byte[] { 7 });
    try
    {
        WreckClaimFiles.Publish(nativeBody, stagedBody);
        throw new Exception("An unrelated native body was overwritten.");
    }
    catch (IOException)
    {
        if (!File.ReadAllBytes(nativeBody).SequenceEqual(new byte[] { 7 }))
            throw new Exception("Conflicting native file changed.");
    }
    Reject(() => reopened.ReadPayload(nativeBody, nativeMetadata));
}
finally
{
    foreach (var file in Directory.EnumerateFiles(publication)) File.Delete(file);
    Directory.Delete(publication);
}
Console.WriteLine("Claim journal scope, partial native publication and conflict handling passed.");

using var native = ModuleDefinition.ReadModule(args.Single());
var available = ContractAudit.EvaluateWreckClaim(native);
Require(available.Available, available.Reason);
native.Mvid = Guid.NewGuid();
Require(ContractAudit.EvaluateWreckClaim(native).Available, "Assembly identity unexpectedly disabled wreck claiming.");
foreach (var dependency in new[]
{
    (Type: "VoxelEngine.ParentEntity", Method: "SaveToDiskAtPath", Call: "GetBlueprintHeaderFullPath"),
    (Type: "BlockEntitySync", Method: "OnDestroy", Call: "UnregisterNPC"),
    (Type: "Player_NetworkSetup", Method: "ServerLoadBlueprint", Call: "SetInstanceData"),
    (Type: "Manager.Save.EntityInstanceSystem_mp", Method: "SaveSystemDataToDisk", Call: "WriteAtomically")
})
{
    var instruction = native.GetType(dependency.Type).Methods.Single(method => method.Name == dependency.Method)
        .Body.Instructions.Single(instruction => instruction.Operand is MethodReference target && target.Name == dependency.Call);
    var previous = instruction.OpCode;
    instruction.OpCode = OpCodes.Nop;
    Require(!ContractAudit.EvaluateWreckClaim(native).Available, "Changed native dependency was accepted: " + dependency.Call);
    instruction.OpCode = previous;
}
Console.WriteLine("Wreck claim native contract checks passed.");
