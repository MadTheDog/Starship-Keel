using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using Game;
using Game.Player;
using Manager;
using Manager.Save;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.WreckClaimProbe;

[BepInPlugin("example.keel.wreck-claim", "Keel Wreck Claim Probe", "0.1.0")]
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
public sealed class Probe : BaseUnityPlugin
{
    private const string Prefix = "KEEL_WRECK_CLAIM_PROBE";
    private const string Env = "SEVO_KEEL_WRECK_CLAIM_PROBE";
    private const string BlueprintName = "V27 Hotwing";
    private const string WreckName = "Keel Wreck Claim Fixture";
    private enum Step { Off, CreateSource, DefeatSource, Stage, AwaitRetirement, AwaitReplacement, Reload }
    private readonly Stopwatch deadline = new Stopwatch();
    private Step step;
    private ProfileSession profile = null!;
    private string session = "";
    private string sourceSession = "";
    private string character = "";
    private string stagedPath = "";
    private string receiptPath = "";
    private string identity = "";
    private string readiness = "";
    private StarData star;
    private WorldTransform pose;
    private ParentEntity? source;
    private ParentEntity? replacement;
    private FileStream? fixtureLock;

    private void Awake()
    {
        var mode = Read(Env);
        if (mode.Length == 0) return;
        try
        {
            Require(mode is "stage" or "reload", "Unknown wreck probe mode.");
            session = Read("SEVO_PLATFORM_SESSION");
            sourceSession = mode == "stage" ? session : Read(Env + "_SOURCE_SESSION");
            character = Read(Env + "_CHARACTER");
            var game = Read(Env + "_GAME");
            var company = Read(Env + "_COMPANY");
            var product = Read(Env + "_PRODUCT");
            Require(Guid.TryParseExact(session, "N", out _) && Guid.TryParseExact(sourceSession, "N", out _)
                && (mode == "stage" || session != sourceSession)
                && game.Length != 0 && company.Length != 0 && product.Length != 0
                && company != "Moonfire Entertainment" && product != "Starship EVO"
                && Application.companyName == company && Application.productName == product
                && string.Equals(Path.GetFullPath(game), Path.GetFullPath(Paths.GameRootPath), StringComparison.OrdinalIgnoreCase)
                && Read("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1",
                "Use a distinct isolated lab identity and its managed profile.");
            if (character.Length == 0) character = "WreckProbe-" + sourceSession.Substring(0, 8);
            Require(character == "WreckProbe-" + sourceSession.Substring(0, 8),
                "Use the exact disposable wreck probe character.");
            profile = ProfileProtocol.Open(Read("SEVO_PLATFORM_STATE_DIR"), Paths.GameRootPath, session);
            var root = Path.Combine(profile.DirectoryPath, "wreck-probe-" + sourceSession);
            RejectReparse(root);
            if (mode == "stage")
            {
                Require(!Directory.Exists(root), "The probe stage directory already exists.");
                Directory.CreateDirectory(root);
            }
            Require(Directory.Exists(root), "The original probe stage directory is missing.");
            var lockPath = Path.Combine(root, "lock");
            RejectReparse(lockPath);
            fixtureLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stagedPath = Path.Combine(root, "fixture.sevo");
            receiptPath = Path.Combine(root, "saved.txt");
            if (mode == "reload") ReadReceipt();
            Next(mode == "stage" ? Step.CreateSource : Step.Reload);
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnDestroy() => fixtureLock?.Dispose();

    private void Update()
    {
        if (step == Step.Off) return;
        try
        {
            if (deadline.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("Wreck probe phase timed out. " + readiness);
            if (!WorldReady()) return;
            Advance();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Advance()
    {
        switch (step)
        {
            case Step.CreateSource:
                SpawnTransientNpcFixture();
                break;
            case Step.DefeatSource:
                if (source == null || !source.IsInitialized || source.BrickCount.totalBricks == 0) return;
                Require(SourceIsEligible(), "The probe source is not the exact ordinary transient NPC fixture.");
                Require(!source.isDestroyed, "The source was defeated before stock destruction.");
                Dependency.PlayerManager.NetworkSetup.ServerDestroyEntity(source);
                Next(Step.Stage);
                break;
            case Step.Stage:
                if (source == null || !source.isDestroyed) return;
                Require(SourceIsEligible(), "The probe source changed before staging.");
                Require(!File.Exists(stagedPath) && !File.Exists(ParentEntity.GetBlueprintHeaderFullPath(
                    Path.GetDirectoryName(stagedPath), Path.GetFileNameWithoutExtension(stagedPath))), "Staging paths already exist.");
                source.SaveToDiskAtPath(stagedPath, isBlueprintExport: false);
                VerifyNativeBody(stagedPath);
                pose = new WorldTransform(source.TransformData);
                BlockEntityManager.instance.RemoveAndDestroy(source, removeFromLocationMapping: true);
                Next(Step.AwaitRetirement);
                break;
            case Step.AwaitRetirement:
                if (source != null || CountLive() != 0 || NPCManager.NpcEntities.Any(entry => ReferenceEquals(entry, source))) return;
                VerifyNativeBody(stagedPath);
                replacement = Dependency.PlayerManager.NetworkSetup.ServerLoadBlueprint(new EntityInstanceData
                {
                    entityName = WreckName,
                    blueprintFullPath = stagedPath,
                    npcBehavior = NpcSpaceshipCommon.Behavior.playerControllable,
                    ownerPlayerNetID = Dependency.PlayerNetwork.netId,
                    isNewInstance = false,
                    saveInWorld = true,
                    buildPermission = BuildPermission.canBeBuilt
                }, pose, star);
                Require(replacement != null, "Stock replacement spawn returned no entity.");
                Next(Step.AwaitReplacement);
                break;
            case Step.AwaitReplacement:
                if (replacement == null || !replacement.IsInitialized || !replacement.isDestroyed) return;
                Require(ReplacementMatches(replacement), "Replacement ownership or persistent identity differs.");
                VerifyNativeCharacterDirectory();
                replacement.SetDirty();
                SaveManager.SaveOnSceneQuit();
                VerifyNativeIndex();
                WriteReceipt();
                Report("STAGED_PASS source retired, same SGUID and destroyed state saved with one stock index entry");
                step = Step.Off;
                break;
            case Step.Reload:
                if (PlanetarySystem.CurrentStarData.seed != star.seed) throw new InvalidOperationException("Reload is in another system.");
                var target = BlockEntityManager.Entities.FirstOrDefault(entity => entity != null && entity.SGUID == identity);
                if (target == null || !target.IsInitialized || !target.isDestroyed) return;
                Require(ReplacementMatches(target), "Reloaded native ownership or persistent identity differs.");
                VerifyNativeIndex();
                Report("RELOAD_PASS same SGUID and destroyed state, local build permission, exactly one native live grid and saved entry");
                step = Step.Off;
                break;
        }
    }

    private void SpawnTransientNpcFixture()
    {
        var player = Dependency.PlayerManager;
        if (player.Health == null || player.Health.IsRespawning || GameIntroductionResources.instance?.Medbay?.inAnimation != false)
        {
            readiness = "Waiting for stock medbay and player respawn.";
            return;
        }
        star = PlanetarySystem.CurrentStarData;
        if (star.seed == 0 || player.SkyTransform.CurrentSystem != star || Dependency.PlayerNetwork.netId == 0)
        {
            readiness = "Waiting for the same initialized system and local loader authority.";
            return;
        }
        if (!TryChooseClearPose(player))
        {
            readiness = "The stock clearance test rejected the fixture positions.";
            return;
        }
        Report("spawn-clearance PASS system=" + star.seed + " position=" + pose.systemPosition);
        var template = Path.Combine(Application.streamingAssetsPath, "Default Blueprints", BlueprintName, BlueprintName + ".sevo");
        Require(File.Exists(template), "The fixture stock blueprint is unavailable.");
        var meta = ParentEntity.GetMetaData(template);
        Require(meta.name == BlueprintName && meta.type == EntityMetaType.spaceship
            && !meta.isWorldWreck && !meta.includesDockedEntities && meta.dockedEntityCount == 0,
            "The fixture blueprint no longer matches a single ordinary spaceship.");
        source = BlockEntityManager.instance.CreateEntity(pose, star);
        Require(source != null, "Native fixture shell creation failed.");
        source!.Sync.SetInstanceData(new EntityInstanceData
        {
            entityName = WreckName,
            blueprintFullPath = template,
            npcBehavior = NpcSpaceshipCommon.Behavior.civilian,
            ownerPlayerNetID = Dependency.PlayerNetwork.netId,
            entityType = EntityType.spaceship,
            isNewInstance = true,
            saveInWorld = false
        });
        Next(Step.DefeatSource);
    }

    private bool TryChooseClearPose(PlayerCommandManager player)
    {
        var origin = player.TransformData.systemPosition;
        if (origin.Equals(Vector3l.zero) || PlanetarySystem.SolarBodies.Count == 0) return false;
        var axes = new[] { Vector3.up, Vector3.right, Vector3.forward, Vector3.down, Vector3.left, Vector3.back };
        foreach (float distance in new[] { 250f, 400f, 650f })
        foreach (var axis in axes)
        {
            var candidate = origin + new Vector3l(axis * distance);
            if (PlanetarySystem.SolarBodies.Any(body => body.IsShipTooClose(candidate))
                || SkyRigidbody.Entities.Any(other => other != null && !other.WillBeUnspawned && other.SkyTransform.CurrentSystem == star
                    && (other.TransformData.systemPosition - candidate).Magnitude < 120f + other.ParentRadius))
                continue;
            pose = new WorldTransform(candidate, Quaternion.identity);
            return true;
        }
        return false;
    }

    private bool SourceIsEligible()
    {
        if (source == null || source.SkyTransform.CurrentSystem != star || source.attachedNodeId != 0
            || source.saveInWorld || !source.Identity.IsNPC || !NPCManager.NpcEntities.Contains(source)
            || source.Identity.IsPlayerOwned || source.WillBeUnspawned || source.EntityMetaData.isWorldWreck
            || source.Type != EntityType.spaceship || source.EntityName != WreckName || !IsFixtureIdentity(source.SGUID)
            || source.IsLocalPlayerParent || source.Rigidbody.isParented || source.directDockedChildrens.Count != 0
            || source.EntityCrew.crews.Count != 0 || source.EntityCrew.npcCharactersIds.Count != 0
            || source.controlMode != BaseVehicle.VehicleControlMode.notControlled)
            return false;
        if (identity.Length == 0) identity = source.SGUID;
        return source.SGUID == identity && CountLive() == 1;
    }

    private bool ReplacementMatches(ParentEntity target)
        => target.SGUID == identity && target.SkyTransform.CurrentSystem.seed == star.seed && CountLive() == 1
            && target.Type == EntityType.spaceship && target.attachedNodeId == 0 && target.isDestroyed
            && target.Identity.IsPlayerOwned && !target.Identity.IsNPC && target.saveInWorld
            && target.Identity.NpcBehavior == NpcSpaceshipCommon.Behavior.playerControllable
            && target.Sync.instanceData.ownerPlayerNetID == Dependency.PlayerNetwork.netId
            && target.Sync.instanceData.buildPermission == BuildPermission.canBeBuilt && target.BrickCount.totalBricks > 0
            && !NPCManager.NpcEntities.Any(entry => entry != null && entry.SGUID == identity);

    private int CountLive() => BlockEntityManager.Entities.Count(entity => entity != null && entity.SGUID == identity);

    private void VerifyNativeBody(string path, string? trustedRoot = null)
    {
        RejectReparse(path, trustedRoot);
        RejectReparse(Path.ChangeExtension(path, ".xml"), trustedRoot);
        var meta = ParentEntity.GetMetaData(path);
        Require(meta.name == WreckName && meta.type == EntityMetaType.spaceship && !meta.isWorldWreck,
            "Stock fixture metadata differs or is incomplete.");
        using var file = File.OpenRead(path);
        Require(file.Length > 0 && file.Length < 16 * 1024 * 1024, "Stock fixture payload size is invalid.");
        var body = BrickEntity_mp.GetSaveFromFile(file, isNewInstance: false);
        Require(body != null && body.SGUID == identity && body.isDestroyed && body.assembly == null,
            "The stock fixture payload did not round-trip its identity and defeat state.");
    }

    private void VerifyNativeIndex()
    {
        var saveRoot = VerifyNativeCharacterDirectory();
        var index = EntityInstanceSystem_mp.LoadFromDisk(PlanetarySystem.CurrentStarData, character, useInstallFolder: false);
        Require(index != null && index.starData.seed == star.seed
            && index.instances.Count(row => row.fileName == identity) == 1, "The stock index lacks one exact fixture entry.");
        VerifyNativeBody(EntityInstanceSystem_mp.GetPlayerInstanceFullPath(character, identity), saveRoot);
    }

    private void WriteReceipt()
    {
        var temporary = receiptPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.WriteLine(profile.ProfileId.ToString("D"));
            writer.WriteLine(character);
            writer.WriteLine(star.seed);
            writer.WriteLine(identity);
            writer.Flush();
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, receiptPath);
    }

    private void ReadReceipt()
    {
        RejectReparse(receiptPath);
        Require(new FileInfo(receiptPath).Length < 1024, "The stock fixture receipt is oversized.");
        var data = File.ReadAllLines(receiptPath);
        Require(data.Length == 4 && Guid.TryParse(data[0], out var owner) && owner == profile.ProfileId
            && data[1] == character && ulong.TryParse(data[2], out _) && IsFixtureIdentity(data[3]), "The stock fixture receipt is invalid.");
        star.seed = ulong.Parse(data[2]);
        Require(star.seed != 0, "The stock fixture system is invalid.");
        identity = data[3];
    }

    private bool WorldReady()
        => GameloopManager.IsMainThread && !GameloopManager.IsNativeContainerAccessUnsafe && !GameloopManager.jobStarted
            && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
            && PlayerState.sessionMode == PlayerState.SessionMode.solo && PlayerState.gameMode == PlayerState.GameMode.creative
            && NetworkServer.active && NetworkServer.dontListen && Dependency.PlayerManager != null
            && Dependency.PlayerManager.IsLocal && Dependency.PlayerNetwork != null
            && SaveManager.CharacterName == character && BlockEntityManager.instance != null;

    private string VerifyNativeCharacterDirectory()
    {
        // The launcher verifies the disposable LocalLow junction before this process starts.
        var persistent = Path.GetFullPath(Application.persistentDataPath);
        var expected = Path.GetFullPath(Path.Combine(persistent, "Save_Data", "Characters", character));
        var actual = Path.GetFullPath(Path.Combine(Common.SkyApplication.Paths.SAVE_CHARACTER, character));
        Require(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) && Directory.Exists(actual),
            "The native save path differs from the expected isolated character.");
        RejectReparse(actual, persistent);
        return persistent;
    }

    private static bool IsFixtureIdentity(string? value)
        => value is { Length: > 0 and < 100 } && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static void RejectReparse(string path, string? trustedRoot = null)
    {
        if (trustedRoot != null)
            Require(Path.GetFullPath(path).StartsWith(trustedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "The native file is outside the isolated save root.");
        for (string? current = path; current != null && !string.Equals(current, trustedRoot, StringComparison.OrdinalIgnoreCase);
            current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing a redirected probe path.");
    }

    private static string Read(string name) => Environment.GetEnvironmentVariable(name) ?? "";
    private void Next(Step next) { step = next; deadline.Restart(); Report("stage=" + next); }
    private void Report(string text) => Logger.LogInfo($"{Prefix} session={session} sguid={identity} {text}");
    private void Fail(Exception error) { Logger.LogError($"{Prefix} FAIL session={session} stage={step} {error}"); step = Step.Off; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
