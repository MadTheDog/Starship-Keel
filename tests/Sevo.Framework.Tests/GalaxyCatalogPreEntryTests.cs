using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Runtime;

internal static class GalaxyCatalogPreEntryTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { checks++; return; }
            throw new InvalidOperationException(message);
        }

        using var game = AssemblyDefinition.ReadAssembly(".refs/game/Assembly-CSharp.dll");
        var module = game.MainModule;
        var audit = ContractAudit.EvaluateGalaxyCatalogPreEntry(module);
        Check(audit.Available, audit.Reason);
        module.Mvid = Guid.NewGuid();
        Check(ContractAudit.EvaluateGalaxyCatalogPreEntry(module).Available, "Unrelated assembly identity is not an admission gate.");
        var creator = module.GetType("UI.UI_CharacterCreation");
        var play = creator.Methods.Single(method => method.Name == "PlayWithNewCharacter");
        var host = play.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method && method.Name == "StartHost");
        var original = host.OpCode;
        host.OpCode = OpCodes.Nop;
        Check(!ContractAudit.EvaluateGalaxyCatalogPreEntry(module).Available, "A changed native entry path must reject.");
        host.OpCode = original;
        var map = module.GetType("World.Galaxy.GalaxyMap");
        var field = map.Fields.Single(value => value.Name == "starDistribution");
        var type = field.FieldType;
        field.FieldType = module.TypeSystem.Int32;
        Check(!ContractAudit.EvaluateGalaxyCatalogPreEntry(module).Available, "A changed serialized field type must reject.");
        field.FieldType = type;
        var publication = ContractAudit.EvaluateGalaxyCatalogPublication(module);
        Check(publication.Available, publication.Reason);
        var exceptionRegion = new ExceptionHandler(ExceptionHandlerType.Finally);
        play.Body.ExceptionHandlers.Add(exceptionRegion);
        Check(!ContractAudit.EvaluateGalaxyCatalogPublication(module).Available,
            "An exception region at character entry must prevent injection of the publication gate.");
        play.Body.ExceptionHandlers.Remove(exceptionRegion);
        var mapData = module.GetType("World.GalaxyWarpGateRoutes");
        var reverse = mapData.Fields.Single(value => value.Name == "EndToStart");
        var reverseType = reverse.FieldType;
        reverse.FieldType = module.TypeSystem.Int32;
        Check(!ContractAudit.EvaluateGalaxyCatalogPublication(module).Available,
            "A changed reverse route binding must reject publication.");
        reverse.FieldType = reverseType;
        var faction = module.GetType("Game.FactionTerritoryMap");
        var spawn = faction.Methods.Single(method => method.Name == "SpawnFortress");
        var populate = spawn.Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference method && method.Name == "PopulateFortressToSpawn");
        var spawnCall = populate.OpCode;
        populate.OpCode = OpCodes.Nop;
        Check(!ContractAudit.EvaluateGalaxyCatalogPublication(module).Available,
            "A changed fortress allocation boundary must reject sparse catalog publication.");
        populate.OpCode = spawnCall;

        string? installedAssets = Environment.GetEnvironmentVariable("SEVO_KEEL_GALAXY_CONTRACT_TEST_ASSETS");
        if (!string.IsNullOrEmpty(installedAssets))
        {
            var bindings = GalaxyCatalogAssetContract.ReadInstalled(module, installedAssets);
            GalaxyCatalogAssetContract.VerifyBindings(bindings);
            Check(bindings.Count(binding => binding.TypeName == "World.Galaxy.GalaxyMap") > 0,
                "No stock serialized GalaxyMap instance was verified.");
            var firstMap = bindings.FindIndex(binding => binding.TypeName == "World.Galaxy.GalaxyMap");
            var mapBinding = bindings[firstMap];
            bindings[firstMap] = new GalaxyCatalogAssetBinding(mapBinding.TypeName, mapBinding.Object, new Dictionary<string, GalaxyCatalogAssetReference>
            {
                ["starDistribution"] = new GalaxyCatalogAssetReference("unresolved.assets", 999)
            });
            Reject(() => GalaxyCatalogAssetContract.VerifyBindings(bindings), "A mismatched serialized map source must reject.");
        }

        string state = Path.GetFullPath(Path.Combine(".cache", "galaxy-pre-entry-test-" + Guid.NewGuid().ToString("N")));
        string gamePath = Path.Combine(state, "game");
        string saves = Path.Combine(state, "saves");
        string world = Path.Combine(saves, "Existing");
        string profileFile = Path.Combine(state, "sevo-runtime-profile.json");
        string bindingFile = Path.Combine(saves, GalaxyCatalogNativeSaveBinding.FileName);
        string pinFile = Path.Combine(state, "galaxy-catalog-pin.json");
        string lockFile = Path.Combine(state, "galaxy-catalog-session.lock");
        string worldFile = Path.Combine(world, "Existing.mpc");
        var profileId = Guid.NewGuid();
        const string provider = "example.catalog:layout";
        string digest = new string('a', 64);
        Directory.CreateDirectory(gamePath);
        Directory.CreateDirectory(saves);
        try
        {
            File.WriteAllText(profileFile, JsonSerializer.Serialize(new { schemaVersion = 1, profileId, gameDirectory = gamePath }));
            File.WriteAllText(bindingFile, JsonSerializer.Serialize(new
                { schemaVersion = 1, profileId, stateDirectory = state, nativeSaveRoot = saves }));
            var profile = ProfileProtocol.Open(state, gamePath, "galaxy-pre-entry-test");
            Check(profile.ReadGalaxyCatalogPin() == null, "An ordinary profile has no custom catalog pin.");
            using (profile.AcquireGalaxyCatalogSession())
            {
                var other = ProfileProtocol.Open(state, gamePath, "another-galaxy-session");
                Reject(() => other.AcquireGalaxyCatalogSession().Dispose(),
                    "Two processes must not admit the same custom catalog root concurrently.");
            }
            using var released = profile.AcquireGalaxyCatalogSession();
            var prepared = GalaxyCatalogPreEntry.VerifySelection(profile, saves, null, provider, 1, digest, "Existing", 10, creating: true);
            Check(prepared.ContainsWorld("Existing", 10) && !Directory.Exists(world), "New-world preflight must not create a native save.");
            profile.WriteGalaxyCatalogPin(prepared);
            var relaunched = ProfileProtocol.Open(state, gamePath, "galaxy-next-process");
            var persisted = relaunched.ReadGalaxyCatalogPin();
            Check(persisted != null && persisted.Matches(saves, provider, 1, digest) && persisted.ContainsWorld("Existing", 10),
                "A fresh profile session must read the exact catalog and world identity.");
            Reject(() => relaunched.WriteGalaxyCatalogPin(new GalaxyCatalogProfilePin(Guid.NewGuid(), saves, provider, 1, digest)),
                "Writing a foreign galaxy pin must reject.");
            Directory.CreateDirectory(world);
            File.WriteAllBytes(worldFile, new byte[] { 9, 1, 2, 3 });
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, null, provider, 1, digest, "Existing", 10, false, worldFile),
                "A pre-existing unpinned world must reject.");
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, prepared, provider, 1, digest, "Existing", 11, false, worldFile),
                "A pinned character with a mismatched seed must reject.");
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, prepared, provider, 1, digest, "Existing", 10, true),
                "An existing native directory must not be repinned by the new-character path.");
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, prepared, provider, 1, new string('b', 64), "Existing", 10, false, worldFile),
                "A different catalog fingerprint must reject.");
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, prepared, provider, 1, digest, "Existing", 10, false, profileFile),
                "A native UI selection outside the exact character path must reject.");
            var foreign = new GalaxyCatalogProfilePin(Guid.NewGuid(), saves, provider, 1, digest).WithWorld("Existing", 10);
            Reject(() => GalaxyCatalogPreEntry.VerifySelection(profile, saves, foreign, provider, 1, digest, "Existing", 10, false, worldFile),
                "The native UI selection cannot use a pin owned by another managed profile.");
            Check(ReferenceEquals(prepared, GalaxyCatalogPreEntry.VerifySelection(profile, saves, prepared, provider, 1, digest, "Existing", 10, false, worldFile)),
                "Exact existing pin and world identity should pass preflight.");
            Check(File.ReadAllBytes(worldFile).SequenceEqual(new byte[] { 9, 1, 2, 3 }) && Directory.EnumerateFiles(world).Count() == 1,
                "All preflight paths must leave the native save untouched.");
        }
        finally
        {
            File.Delete(worldFile);
            if (Directory.Exists(world)) Directory.Delete(world);
            File.Delete(bindingFile);
            File.Delete(pinFile);
            File.Delete(lockFile);
            File.Delete(profileFile);
            Directory.Delete(gamePath);
            Directory.Delete(saves);
            Directory.Delete(state);
        }
        return checks;
    }
}
