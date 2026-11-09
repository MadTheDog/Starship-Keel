using Sevo.Example.WreckClaimProbe;

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
