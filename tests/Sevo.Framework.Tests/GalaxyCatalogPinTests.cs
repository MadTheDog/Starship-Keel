using System.Text.Json;
using Sevo.Framework.Runtime;

internal static class GalaxyCatalogPinTests
{
    internal static int Run()
    {
        var profile = Guid.NewGuid();
        string root = Path.GetFullPath("native-profile");
        string digest = new string('a', 64);
        var initial = new GalaxyCatalogProfilePin(profile, root, "example.catalog:layout", 2, digest);
        var pin = initial.WithWorld("World A", 9123);
        int checks = 0;
        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        }

        Check(!initial.ContainsWorld("World A", 9123) && pin.ContainsWorld("world a", 9123),
            "Recording a native world must leave the original pin immutable and use case-insensitive native identity.");
        Check(!pin.ContainsWorld("World A", 9124), "An imported world with the same name and another seed must not be admitted.");
        using var serialized = new MemoryStream(pin.Serialize(profile));
        var restored = GalaxyCatalogProfilePin.Read(serialized, profile);
        Check(restored.Matches(root, "example.catalog:layout", 2, digest) && restored.ContainsWorld("World A", 9123),
            "The native save root, catalog identity and world identity must round trip.");
        Check(!restored.Matches(root, "example.catalog:layout", 3, digest)
            && !restored.Matches(root, "example.catalog:layout", 2, new string('b', 64)),
            "A changed revision or fingerprint must not admit the pinned world.");
        Check(!restored.Matches(Path.GetFullPath("other-native-profile"), "example.catalog:layout", 2, digest),
            "A distinct native save root must not reuse the pin.");
        Reject(() => restored.Serialize(Guid.NewGuid()), "A different managed profile must not write the pin.");
        Reject(() => restored.WithWorld("world a", 9319), "An admitted world's name must not be rebound to a different seed.");
        Reject(() => restored.WithWorld("../World", 123), "A traversal string is not a native world name.");
        Reject(() => restored.WithWorld("World A.", 123), "A trailing-dot alias is not a distinct native world name.");
        string state = Path.GetFullPath(Path.Combine(".cache", "galaxy-pin-test-" + Guid.NewGuid().ToString("N")));
        string saves = Path.Combine(state, "saves");
        string existingWorld = Path.Combine(saves, "ExistingWorld");
        string game = Path.Combine(state, "game");
        string profileFile = Path.Combine(state, "sevo-runtime-profile.json");
        string bindingFile = Path.Combine(saves, GalaxyCatalogNativeSaveBinding.FileName);
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(saves);
        try
        {
            File.WriteAllText(profileFile, JsonSerializer.Serialize(new { schemaVersion = 1, profileId = profile, gameDirectory = game }));
            var session = ProfileProtocol.Open(state, game, "galaxy-pin-test");
            Reject(() => GalaxyCatalogNativeSaveBinding.Verify(session, saves, true), "An unmarked native save root must reject.");
            WriteBinding(Guid.NewGuid());
            Reject(() => GalaxyCatalogNativeSaveBinding.Verify(session, saves, true), "A native save marker bound to another profile must reject.");
            WriteBinding(profile);
            Check(GalaxyCatalogNativeSaveBinding.Verify(session, saves, true) == saves,
                "A verified empty native save root must be admitted.");
            Directory.CreateDirectory(existingWorld);
            Reject(() => GalaxyCatalogNativeSaveBinding.Verify(session, saves, true), "First activation must reject an existing world.");
            Check(GalaxyCatalogNativeSaveBinding.Verify(session, saves, false) == saves,
                "An already pinned native save root may retain its worlds.");
        }
        finally
        {
            if (Directory.Exists(existingWorld)) Directory.Delete(existingWorld);
            File.Delete(bindingFile);
            File.Delete(profileFile);
            Directory.Delete(game);
            Directory.Delete(saves);
            Directory.Delete(state);
        }
        return checks;

        void WriteBinding(Guid owner)
            => File.WriteAllText(bindingFile, JsonSerializer.Serialize(new
                { schemaVersion = 1, profileId = owner, stateDirectory = state, nativeSaveRoot = saves }));

        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            { checks++; return; }
            throw new InvalidOperationException(message);
        }
    }
}
