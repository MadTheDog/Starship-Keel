using System;
using System.IO;
using System.Linq;
using Common.SkyApplication;
using Game;
using Manager;
using Manager.Save;
using Mirror;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    public enum WreckClaimState { Pending, Claimed, NeedsAttention }

    /// <summary>Progress of an accepted claim in the current game process.</summary>
    public sealed class WreckClaimOperation
    {
        internal WreckClaimOperation(string id) => PersistentId = id;
        public string PersistentId { get; }
        public WreckClaimState State { get; internal set; }
        public ParentEntity? ClaimedGrid { get; internal set; }
        public string? FailureReason { get; internal set; }
    }

    /// <summary>Transfers eligible, caller-provided transient NPC wrecks to the local solo player.</summary>
    public static class WreckClaimService
    {
        private const string PendingDirectory = "keel-wreck-claim.pending";
        private static ProfileSession? profile;
        private static FileStream? sessionLock;
        private static Action<string>? warning;
        private static Claim? active;
        private static string? blockedCharacter;
        private static double nextPoll;
        private static bool busy;

        public static bool IsAvailable => profile != null;

        /// <summary>Starts a claim on a defeated, pilotless transient NPC created by the caller with local loader authority.</summary>
        /// <remarks>The caller must own its spawn lifecycle and prevent independent respawn. A true result means Keel has durably accepted the claim.</remarks>
        public static bool TryClaimTransientWreck(ParentEntity wreck, out WreckClaimOperation? operation)
        {
            operation = null;
            if (busy || !TryGetWorld(out var world) || !Eligible(wreck, world)) return false;
            busy = true;
            string? stage = null;
            try
            {
                if (Directory.Exists(world.Pending) || File.Exists(world.Pending)) return false;
                string id = wreck.SGUID;
                var files = new NativeFiles(world, id);
                if (files.Count(ReadIndex(world)) != 0 || files.HasNativePayload
                    || EntitiesLocationMapping.Current.Mapping.TryGetValue(id, out var mapped) && !mapped.Equals(world.Star))
                    return false;
                var transform = new WorldTransform(wreck.TransformData);
                if (!ClearForLoad(transform.systemPosition, world.Star, wreck)) return false;

                stage = world.Child(".keel-wreck-stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                var body = Path.Combine(stage, WreckClaimJournal.Body);
                var metadata = Path.Combine(stage, WreckClaimJournal.Metadata);
                wreck.SaveToDiskAtPath(body, isBlueprintExport: false);
                ValidatePayload(body, id);
                var bodyBytes = FlushSnapshot(body, WreckClaimJournal.MaxBody);
                var metadataBytes = FlushSnapshot(metadata, WreckClaimJournal.MaxMetadata);
                var p = transform.systemPosition;
                var q = transform.rotation;
                var journal = new WreckClaimJournal(profile!.ProfileId, world.Character, world.WorldSeed, world.Star.SaveString(), id,
                    (p.x, p.y, p.z), (q.x, q.y, q.z, q.w), WreckClaimFiles.Digest(bodyBytes), WreckClaimFiles.Digest(metadataBytes));
                journal.Publish(Path.Combine(stage, WreckClaimJournal.Manifest));

                // Publishing the directory is the acceptance boundary. The stock source still exists until then.
                Directory.Move(stage, world.Pending);
                stage = null;
                var claim = new Claim(world, journal) { Source = wreck };
                active = claim;
                operation = claim.Operation;
                BlockEntityManager.instance.RemoveAndDestroy(wreck, removeFromLocationMapping: true);
                return true;
            }
            catch (Exception exception)
            {
                if (operation != null) Stop(world, exception);
                else warning?.Invoke("Wreck claim was rejected before source retirement: " + exception.GetBaseException().Message);
                return operation != null;
            }
            finally
            {
                try { if (stage != null) CleanOwnedDirectory(stage); }
                finally { busy = false; }
            }
        }

        internal static void Enable(ProfileSession session, Action<string> log)
        {
            var lockPath = Path.Combine(session.DirectoryPath, "wreck-claim-session.lock");
            ProfileProtocol.NoReparse(lockPath);
            sessionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            profile = session;
            warning = log;
        }

        internal static void Disable()
        {
            profile = null;
            active = null;
            blockedCharacter = null;
            nextPoll = 0;
            warning = null;
            sessionLock?.Dispose();
            sessionLock = null;
        }

        internal static void Tick()
        {
            if (!IsAvailable || busy || Time.realtimeSinceStartupAsDouble < nextPoll) return;
            nextPoll = Time.realtimeSinceStartupAsDouble + 0.5;
            if (!TryGetWorld(out var world)) return;
            if (active != null && !world.SameCharacter(active.World)) active = null;
            if (blockedCharacter == world.CharacterPath) return;
            busy = true;
            try
            {
                if (!Directory.Exists(world.Pending)) return;
                if (active == null)
                {
                    var journal = WreckClaimJournal.Open(world.PendingFile(WreckClaimJournal.Manifest),
                        profile!.ProfileId, world.Character, world.WorldSeed);
                    if (journal.System != world.Star.SaveString()) return;
                    active = new Claim(world, journal);
                }
                if (active.Journal.System == world.Star.SaveString()) Advance(world, active);
            }
            catch (Exception exception) { Stop(world, exception); }
            finally { busy = false; }
        }

        private static void Advance(ClaimWorld world, Claim claim)
        {
            var id = claim.Journal.Identity;
            var matches = LiveMatches(id);
            if (matches.Length > 1) throw new IOException("More than one live grid has the wreck identity.");
            var live = matches.SingleOrDefault();
            bool npcRemains = NPCManager.NpcEntities.Any(npc => npc != null && SameId(npc.SGUID, id));
            if ((live != null && ReferenceEquals(live, claim.Source)) || (npcRemains && live == null)) return;
            if (live != null && !live.IsInitialized) return;
            if (npcRemains || live != null && (!ReplacementMatches(live, world)
                || (!live.saveInWorld && !ReferenceEquals(live, claim.Replacement))))
                throw new IOException("The live identity belongs to an unexpected or respawned source.");

            var files = new NativeFiles(world, id);
            var index = ReadIndex(world);
            int saved = files.Count(index);
            if (saved > 1 || files.HasLegacyPayload)
                throw new IOException("More than one stock save entry or a legacy body has the wreck identity.");
            if (EntitiesLocationMapping.Current.Mapping.TryGetValue(id, out var mapped) && !mapped.Equals(world.Star))
                throw new IOException("The wreck identity is mapped to another system.");
            if (saved == 1)
            {
                claim.Journal.ReadPayload(files.Body, files.Metadata);
                ValidatePayload(files.Body, id);
                if (live == null) return; // Let the stock indexed object load rather than create a duplicate.
                Complete(world, claim, live);
                return;
            }
            if (live != null && !ReferenceEquals(live, claim.Replacement))
                throw new IOException("An unexpected unindexed live grid has the wreck identity.");

            var body = world.PendingFile(WreckClaimJournal.Body);
            var metadata = world.PendingFile(WreckClaimJournal.Metadata);
            var bytes = claim.Journal.ReadPayload(body, metadata);
            ValidatePayload(body, id);
            if (live == null)
            {
                if (claim.Replacement != null) return;
                var p = claim.Journal.Position;
                var q = claim.Journal.Rotation;
                var pose = new WorldTransform(new Vector3l(p.X, p.Y, p.Z), new Quaternion(q.X, q.Y, q.Z, q.W));
                if (!ClearForLoad(pose.systemPosition, world.Star, null)) return;
                files.Publish(bytes);
                claim.Replacement = Dependency.PlayerNetwork.ServerLoadBlueprint(new EntityInstanceData
                {
                    entityName = ParentEntity.GetMetaData(files.Body).name,
                    blueprintFullPath = files.Body,
                    ownerPlayerNetID = world.PlayerId,
                    npcBehavior = NpcSpaceshipCommon.Behavior.playerControllable,
                    isNewInstance = false,
                    // The stock autosave may run while this grid is initializing.
                    saveInWorld = false,
                    buildPermission = BuildPermission.canBeBuilt
                }, pose, world.Star);
                if (claim.Replacement == null) throw new IOException("The stock loader did not create the replacement.");
                return;
            }

            files.Publish(bytes);
            index.instances.Add(new EntityInstance_mp { fileName = id, worldTransform = new WorldTransform(live.TransformData) });
            EntityInstanceSystem_mp.SaveSystemDataToDisk(index, world.Star, world.Character);
            if (files.Count(ReadIndex(world)) != 1) throw new IOException("Stock index did not retain one wreck entry.");
            Complete(world, claim, live);
        }

        private static void Complete(ClaimWorld world, Claim claim, ParentEntity live)
        {
            var files = new NativeFiles(world, claim.Journal.Identity);
            FlushFile(files.Body);
            FlushFile(files.Metadata);
            FlushFile(world.Index);
            EntitiesLocationMapping.UpdateEntityLocation(claim.Journal.Identity, world.Star);
            var finished = world.Child(".keel-wreck-complete-" + Guid.NewGuid().ToString("N"));
            Directory.Move(world.Pending, finished);
            live.Sync.instanceData.saveInWorld = true;
            live.saveInWorld = true;
            claim.Operation.ClaimedGrid = live;
            claim.Operation.State = WreckClaimState.Claimed;
            active = null;
            CleanOwnedDirectory(finished);
        }

        private static void Stop(ClaimWorld world, Exception exception)
        {
            blockedCharacter = world.CharacterPath;
            var reason = exception.GetBaseException().Message;
            if (active != null)
            {
                active.Operation.FailureReason = reason;
                active.Operation.State = WreckClaimState.NeedsAttention;
            }
            warning?.Invoke("Wreck claim paused with its recovery journal intact: " + reason);
        }

        private static bool TryGetWorld(out ClaimWorld world)
        {
            world = default;
            if (!IsAvailable || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || GameloopManager.jobStarted || !FunctionalCommandState.IsSolo || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || PlayerState.InMenu || PlanetarySystem.InSystemTransition
                || !NetworkServer.active || !NetworkServer.dontListen || BlockEntityManager.instance == null
                || Dependency.PlayerManager is not { IsLocal: true } player || player.Health == null
                || player.Health.IsDead || player.Health.IsRespawning || Dependency.PlayerNetwork == null
                || Dependency.PlayerNetwork.netId == 0 || EntitiesLocationMapping.Current?.Mapping == null
                || SaveManager.playerSave is not { seed: > 0 } save || PlayerSave_mp.Load_Legacy
                || string.IsNullOrWhiteSpace(SaveManager.CharacterName)
                || SaveManager.CharacterName != Path.GetFileName(SaveManager.CharacterName))
                return false;

            var star = PlanetarySystem.CurrentStarData;
            if (star.seed == 0 || !star.Equals(player.SkyTransform.CurrentSystem)) return false;
            try
            {
                world = new ClaimWorld(SaveManager.CharacterName, save.seed, star, Dependency.PlayerNetwork.netId);
                return Directory.Exists(world.CharacterPath);
            }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool Eligible(ParentEntity wreck, ClaimWorld world)
            => wreck != null && wreck.IsInitialized && wreck.isDestroyed && !wreck.WillBeUnspawned
                && wreck.Type == EntityType.spaceship && !wreck.saveInWorld && wreck.attachedNodeId == 0
                && !wreck.EntityMetaData.isWorldWreck && wreck.Identity.IsNPC && !wreck.Identity.IsPlayerOwned
                && wreck.Identity.Squadron == null && wreck.Identity.missionsLinked.Count == 0
                && NPCManager.NpcEntities.Contains(wreck) && wreck.SkyTransform.CurrentSystem.Equals(world.Star)
                && wreck.Sync != null && wreck.Sync.instanceData.ownerPlayerNetID == world.PlayerId
                && wreck.Sync.instanceData.pilotId == 0 && wreck.controlMode == BaseVehicle.VehicleControlMode.notControlled
                && !wreck.IsLocalPlayerParent && wreck.Rigidbody != null && !wreck.Rigidbody.isParented
                && wreck.Rigidbody.Mass > 0.02f && wreck.Dock != null && !wreck.Dock.IsDockedOrLanded
                && !wreck.Dock.IsAutoDockingToPort && wreck.directDockedChildrens.Count == 0
                && wreck.EntityCrew.crews.Count == 0 && wreck.EntityCrew.npcCharactersIds.Count == 0
                && wreck.BrickCount.totalBricks > 0 && WreckClaimJournal.IsIdentity(wreck.SGUID)
                && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(wreck.SGUID), wreck)
                && LiveMatches(wreck.SGUID).Length == 1;

        private static bool ReplacementMatches(ParentEntity live, ClaimWorld world)
            => live.isDestroyed && !live.WillBeUnspawned && live.IsInitialized && live.Type == EntityType.spaceship
                && live.SkyTransform.CurrentSystem.Equals(world.Star) && live.attachedNodeId == 0
                && live.Identity.IsPlayerOwned && !live.Identity.IsNPC && !live.EntityMetaData.isWorldWreck
                && live.Sync != null && live.Sync.instanceData.ownerPlayerNetID == world.PlayerId
                && live.Sync.instanceData.buildPermission == BuildPermission.canBeBuilt
                && live.BrickCount.totalBricks > 0;

        private static bool ClearForLoad(Vector3l position, StarData star, ParentEntity? source)
            => !PlanetarySystem.SolarBodies.Any(body => body.IsShipTooClose(position))
                && !SkyRigidbody.Entities.Any(entity => entity != null && !ReferenceEquals(entity, source) && !entity.WillBeUnspawned
                    && entity.SkyTransform.CurrentSystem.Equals(star)
                    && (entity.TransformData.systemPosition - position).Magnitude < 120f + entity.ParentRadius);

        private static ParentEntity[] LiveMatches(string id)
            => BlockEntityManager.Entities.Where(entity => entity != null && SameId(entity.SGUID, id)).Take(2).ToArray();

        private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static EntityInstanceSystem_mp ReadIndex(ClaimWorld world)
        {
            _ = world.Child(Path.Combine(Paths.instances_path, world.Star.SaveString()) + ".tmp");
            _ = world.Index;
            var index = EntityInstanceSystem_mp.LoadFromDisk(world.Star, world.Character, useInstallFolder: false);
            if (index?.instances == null || !index.starData.Equals(world.Star) || index.instances.Any(row => row == null))
                throw new IOException("The current stock system index is missing or unreadable.");
            return index;
        }

        private static void ValidatePayload(string path, string id)
        {
            using var file = File.OpenRead(path);
            if (file.Length <= 0 || file.Length > WreckClaimJournal.MaxBody) throw new IOException("Wreck body is oversized or empty.");
            var xml = Path.ChangeExtension(path, ".xml");
            if (!File.Exists(xml) || new FileInfo(xml).Length is <= 0 or > WreckClaimJournal.MaxMetadata)
                throw new IOException("Wreck metadata is oversized or empty.");
            var meta = ParentEntity.GetMetaData(path);
            var body = BrickEntity_mp.GetSaveFromFile(file, isNewInstance: false);
            if (body == null || body.SGUID != id || !body.isDestroyed || body.assembly != null
                || meta.type != EntityMetaType.spaceship || meta.isWorldWreck || meta.includesDockedEntities
                || meta.dockedEntityCount != 0 || string.IsNullOrWhiteSpace(meta.name))
                throw new IOException("Native wreck snapshot did not retain the expected identity and defeat state.");
        }

        private static byte[] FlushSnapshot(string path, int limit)
        {
            FlushFile(path);
            return WreckClaimFiles.Read(path, limit);
        }

        private static void FlushFile(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            file.Flush(flushToDisk: true);
        }

        private static void CleanOwnedDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var name in new[] { WreckClaimJournal.Body, WreckClaimJournal.Metadata, WreckClaimJournal.Manifest })
                    File.Delete(Path.Combine(path, name));
                Directory.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Claim
        {
            internal readonly ClaimWorld World;
            internal readonly WreckClaimJournal Journal;
            internal readonly WreckClaimOperation Operation;
            internal ParentEntity? Source;
            internal ParentEntity? Replacement;

            internal Claim(ClaimWorld world, WreckClaimJournal journal)
            {
                World = world;
                Journal = journal;
                Operation = new WreckClaimOperation(journal.Identity);
            }
        }

        private readonly struct ClaimWorld
        {
            internal readonly string Character;
            internal readonly string CharacterPath;
            internal readonly ulong WorldSeed;
            internal readonly StarData Star;
            internal readonly uint PlayerId;
            internal string Pending => Child(PendingDirectory);
            internal string Index => Child(Path.Combine(Paths.instances_path, Star.SaveString()));

            internal ClaimWorld(string character, ulong worldSeed, StarData star, uint playerId)
            {
                Character = character;
                WorldSeed = worldSeed;
                Star = star;
                PlayerId = playerId;
                var root = ProfileProtocol.Canonical(Application.persistentDataPath);
                var expected = Path.Combine(root, "Save_Data", "Characters");
                if (!string.Equals(expected, ProfileProtocol.Canonical(Paths.SAVE_CHARACTER), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Wreck claiming requires the stock character save root.");
                CharacterPath = Path.Combine(expected, character);
                CheckPath(CharacterPath, root);
            }

            internal bool SameCharacter(ClaimWorld other)
                => WorldSeed == other.WorldSeed && string.Equals(CharacterPath, other.CharacterPath, StringComparison.OrdinalIgnoreCase);

            internal string PendingFile(string name) => Child(Path.Combine(PendingDirectory, name));

            internal string Child(string name)
            {
                var path = Path.GetFullPath(Path.Combine(CharacterPath, name));
                CheckPath(path, CharacterPath);
                return path;
            }

            private static void CheckPath(string path, string root)
            {
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Wreck claim path is outside its character directory.");
                for (string? current = path; current != null && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
                    current = Path.GetDirectoryName(current))
                    if ((File.Exists(current) || Directory.Exists(current))
                        && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Wreck claim refused a redirected save path.");
            }
        }

        private sealed class NativeFiles
        {
            private readonly string identity;
            internal readonly string Body;
            internal readonly string Metadata;
            private readonly string legacy;
            internal bool HasLegacyPayload => File.Exists(legacy);
            internal bool HasNativePayload => File.Exists(Body) || File.Exists(Metadata) || HasLegacyPayload;

            internal NativeFiles(ClaimWorld world, string identity)
            {
                this.identity = identity;
                Body = world.Child(Path.Combine(Paths.entities_path, identity + ".sevo"));
                Metadata = world.Child(Path.Combine(Paths.entities_path, identity + ".xml"));
                legacy = world.Child(Path.Combine(Paths.entities_path, identity + ".mps"));
                if (!string.Equals(Body, Path.GetFullPath(EntityInstanceSystem_mp.GetPlayerInstanceFullPath(world.Character, identity)),
                    StringComparison.OrdinalIgnoreCase)) throw new IOException("Native wreck file path changed.");
            }

            internal int Count(EntityInstanceSystem_mp index) => index.instances.Count(row => SameId(row.fileName, identity));

            internal void Publish((byte[] Body, byte[] Metadata) bytes)
            {
                bool hasBody = WreckClaimFiles.MatchesExisting(Body, bytes.Body);
                bool hasMetadata = WreckClaimFiles.MatchesExisting(Metadata, bytes.Metadata);
                if (!hasBody) WreckClaimFiles.Publish(Body, bytes.Body);
                if (!hasMetadata) WreckClaimFiles.Publish(Metadata, bytes.Metadata);
            }
        }
    }
}
