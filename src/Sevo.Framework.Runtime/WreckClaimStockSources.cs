using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Common.SkyApplication;
using HarmonyLib;
using Manager;
using Manager.Save;
using Mirror;
using VoxelEngine;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    public static partial class WreckClaimService
    {
        private const string StockProfileMarker = "stock-wreck-retirement-required";
        private static readonly FieldInfo HarmonyLabels = typeof(CodeInstruction).GetField("labels")!;
        private static ProfileSession? stockProfile;
        private static bool stockClaimsEnabled;
        private static bool stockRetirementRequired;
        private static Action<string>? stockWarning;
        private static Func<OctreeNodeData, string>? nativeIdentity;
        private static FieldInfo? nodeEntities;
        private static MethodInfo? hasNode, getAttached, containsAttached, removeAttached;
        private static ConditionalWeakTable<ParentEntity, StockObservation> observedStock = new();

        /// <summary>Whether this managed profile enables claims on audited stock terrain wrecks.</summary>
        public static bool IsStockClaimAvailable => IsAvailable && stockClaimsEnabled;
        internal static bool IsStockRetirementProtected => stockProfile != null;

        /// <summary>Claims a loaded stock terrain wreck and transfers it to a clear-space pose in the current system.</summary>
        /// <remarks>The caller chooses when to offer the transfer and supplies the destination in native system coordinates.
        /// The source and the replacement share the same persistent identity. The starter wreck is outside this contract.</remarks>
        public static bool TryClaimStockTerrainWreck(ParentEntity wreck, WorldTransform recoveryPose, out WreckClaimOperation? operation)
        {
            operation = null;
            return IsStockClaimAvailable && TryClaim(wreck, recoveryPose, out operation);
        }

        internal static void EnableStockSources(Harmony harmony, ProfileSession session, bool allowNewClaims, Action<string> log)
        {
            var sourceType = typeof(WorldEntities);
            var identity = AccessTools.Method(sourceType, "GetWreckSGUID", new[] { typeof(OctreeNodeData) })
                ?? throw new MissingMethodException(sourceType.FullName, "GetWreckSGUID");
            nativeIdentity = (Func<OctreeNodeData, string>)Delegate.CreateDelegate(typeof(Func<OctreeNodeData, string>), identity);
            nodeEntities = AccessTools.Field(sourceType, "nodeEntities") ?? throw new MissingFieldException(sourceType.FullName, "nodeEntities");
            var collection = nodeEntities.FieldType;
            hasNode = collection.GetMethod("ContainsKey", new[] { typeof(int) });
            getAttached = collection.GetMethod("get_Item", new[] { typeof(int) });
            containsAttached = collection.GetMethod("Contains", new[] { typeof(int), typeof(ParentEntity) });
            removeAttached = collection.GetMethod("Remove", new[] { typeof(int), typeof(ParentEntity) });
            if (hasNode?.ReturnType != typeof(bool) || getAttached?.ReturnType != typeof(IReadOnlyCollection<ParentEntity>)
                || containsAttached?.ReturnType != typeof(bool) || removeAttached?.ReturnType != typeof(bool))
                throw new InvalidOperationException("The stock terrain attachment dictionary contract changed.");

            var fresh = AccessTools.Method(sourceType, nameof(WorldEntities.SpawnWreckOnNewTerrainNode),
                new[] { typeof(OctreeNodeData), typeof(int) });
            var saved = AccessTools.Method(sourceType, nameof(WorldEntities.OnNodeLoad),
                new[] { typeof(OctreeNodeData), typeof(int), typeof(List<TerrainEntityInstance_mp>) });
            var postfix = Hook(nameof(ObserveStockSource));
            harmony.Patch(fresh, prefix: Hook(nameof(BeforeFreshWreck)), postfix: postfix);
            harmony.Patch(saved, prefix: Hook(nameof(BeforeSavedWrecks)), postfix: postfix,
                transpiler: Hook(nameof(GuardSavedFallback)));
            stockProfile = session;
            stockWarning = log;
            stockClaimsEnabled = allowNewClaims;
            stockRetirementRequired = HasStockRetirementState(session);

            HarmonyMethod Hook(string name) => new(typeof(WreckClaimService), name);
        }

        internal static void DisableStockSources()
        {
            stockClaimsEnabled = false;
            stockRetirementRequired = false;
            stockProfile = null;
            stockWarning = null;
            observedStock = new ConditionalWeakTable<ParentEntity, StockObservation>();
        }

        internal static bool HasStockRetirementState(ProfileSession session)
        {
            if (MarkerPresent(Path.Combine(session.DirectoryPath, StockProfileMarker))) return true;
            var root = UnityEngine.Application.persistentDataPath;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return true;
            return MarkerPresent(Path.Combine(root, "Save_Data", "Characters", "." + StockProfileMarker));
        }

        private static bool MarkerPresent(string path)
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        }

        private static void RequireStockRetirementOnFutureLoads(ClaimWorld world)
        {
            var native = Path.Combine(Path.GetDirectoryName(world.CharacterPath)!, "." + StockProfileMarker);
            var profilePath = Path.Combine(stockProfile!.DirectoryPath, StockProfileMarker);
            ProfileProtocol.NoReparse(native);
            ProfileProtocol.NoReparse(profilePath);
            WreckClaimFiles.Publish(native, new byte[] { 1 });
            WreckClaimFiles.Publish(profilePath, new byte[] { 1 });
            stockRetirementRequired = true;
        }

        private static bool BeforeFreshWreck(OctreeNodeData __0)
            => __0.isDensityFromSave || __0.VoxelSize != 16 || !TryGetRetirement(__0, out _);

        private static void BeforeSavedWrecks(OctreeNodeData __0, ref List<TerrainEntityInstance_mp>? __2)
        {
            if (__2 is not { Count: > 0 } || !TryGetRetirement(__0, out var retired)) return;
            // This list belongs to the stock terrain payload. Leave the original and any other rows intact.
            __2 = retired == null ? new List<TerrainEntityInstance_mp>()
                : __2.Where(row => row == null || !SameId(row.fileName, retired)).ToList();
        }

        private static IEnumerable<CodeInstruction> GuardSavedFallback(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int load = code.FindIndex(instruction => instruction.operand is MethodInfo call
                && call.DeclaringType == typeof(Game.MissionToolbox) && call.Name == "LoadEntityFromPlayerInstanceAtPath");
            if (load < 0 || load + 2 >= code.Count || (code[load + 1].opcode != OpCodes.Brtrue_S
                && code[load + 1].opcode != OpCodes.Brtrue) || code[load + 1].operand is not Label skip
                || ((IList)HarmonyLabels.GetValue(code[load + 2])!).Count != 0 || code[load + 2].blocks.Count != 0)
                throw new InvalidOperationException("The stock saved wreck fallback branch changed.");

            // A missing file in another row also enters the node-derived wreck fallback.
            code.InsertRange(load + 2, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WreckClaimService), nameof(IsRetiredNode))),
                new CodeInstruction(OpCodes.Brtrue, skip)
            });
            return code;
        }

        private static bool IsRetiredNode(OctreeNodeData node) => TryGetRetirement(node, out _);

        private static bool TryGetRetirement(OctreeNodeData node, out string? identity)
        {
            identity = null;
            if (stockProfile == null || !stockRetirementRequired || !TryGetSourceScope(node, out var world, out var source)) return false;
            var name = MarkerName(world, source);
            try
            {
                var permanent = world.Child(name);
                var pending = world.PendingFile(name);
                identity = WreckClaimStockSource.ReadMarkerIfPresent(permanent)
                    ?? WreckClaimStockSource.ReadMarkerIfPresent(pending);
                return identity != null;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                stockWarning?.Invoke("Stock wreck retirement could not be read for this node: " + error.Message);
                return true;
            }
        }

        private static bool TryGetSourceScope(OctreeNodeData node, out ClaimWorld world, out WreckClaimStockSource source)
        {
            world = default;
            source = null!;
            if (!GameloopManager.IsMainThread || !PlayerState.IsHostOrSolo || !NetworkServer.dontListen
                || PlayerSave_mp.Load_Legacy || SaveManager.playerSave is not { seed: > 0 } save || node.asteroidId != 0
                || node.planetGenerationId <= 0 || node.treeGuid == 0)
                return false;
            var star = PlanetarySystem.CurrentStarData;
            var character = SaveManager.CharacterName;
            if (star.seed == 0 || string.IsNullOrWhiteSpace(character) || character != Path.GetFileName(character)) return false;
            var bodies = PlanetarySystem.SolarBodies.Where(body => body != null && body.planetGenerationId == node.planetGenerationId
                && body.planetIdForShader == node.planetShaderId && body.Seed != 0).Take(2).ToArray();
            if (bodies.Length != 1) return false;
            try { world = new ClaimWorld(character, save.seed, star, 0); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
            source = new WreckClaimStockSource(bodies[0].Seed, node.treeGuid, null, null);
            return true;
        }

        private static void ObserveStockSource(OctreeNodeData __0, int __1)
        {
            if (!stockClaimsEnabled || !TryGetSourceScope(__0, out var world, out var source)) return;
            var id = nativeIdentity!(__0);
            var attached = nodeEntities!.GetValue(null);
            if (attached == null || WorldEntities.terrainEntities == null || hasNode!.Invoke(attached, new object[] { __1 }) is not true)
                return;
            if (getAttached!.Invoke(attached, new object[] { __1 }) is not IReadOnlyCollection<ParentEntity> grids) return;
            var matches = grids.Where(grid => grid != null && grid.attachedNodeId == __1
                && string.Equals(grid.SGUID, id, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length == 1)
            {
                observedStock.Remove(matches[0]);
                observedStock.Add(matches[0], new StockObservation(world, source, id, __1));
            }
        }

        private static bool IsObservedSource(ParentEntity wreck, ClaimWorld world, WreckClaimStockSource source)
            => observedStock.TryGetValue(wreck, out var observation) && observation.World.SameCharacter(world)
                && observation.World.Star.Equals(world.Star) && observation.Source.BodySeed == source.BodySeed
                && observation.Source.TreeGuid == source.TreeGuid && wreck.attachedNodeId == observation.NodeId
                && string.Equals(wreck.SGUID, observation.Identity, StringComparison.Ordinal)
                && containsAttached!.Invoke(nodeEntities!.GetValue(null), new object[] { observation.NodeId, wreck }) is true
                && WorldEntities.terrainEntities.Contains(wreck);

        private static bool EligibleStock(ParentEntity wreck, ClaimWorld world)
            => wreck != null && wreck.IsInitialized && wreck.isDestroyed && !wreck.WillBeUnspawned
                && wreck.Type == EntityType.spaceship && !wreck.saveInWorld && wreck.attachedNodeId != 0
                && wreck.EntityMetaData.isWorldWreck && !wreck.Identity.IsNPC && !wreck.Identity.IsPlayerOwned
                && wreck.Identity.Squadron == null && wreck.Identity.missionsLinked.Count == 0
                && !NPCManager.NpcEntities.Contains(wreck) && wreck.SkyTransform.CurrentSystem.Equals(world.Star)
                && wreck.Sync != null && wreck.Sync.instanceData.ownerPlayerNetID == world.PlayerId
                && wreck.Sync.instanceData.pilotId == 0 && wreck.controlMode == BaseVehicle.VehicleControlMode.notControlled
                && !wreck.IsLocalPlayerParent && wreck.Rigidbody != null && !wreck.Rigidbody.isParented
                && wreck.Dock != null && !wreck.Dock.IsDockedOrLanded && !wreck.Dock.IsAutoDockingToPort
                && wreck.directDockedChildrens.Count == 0 && wreck.EntityCrew.crews.Count == 0 && wreck.EntityCrew.npcCharactersIds.Count == 0
                && wreck.BrickCount.totalBricks > 0 && WreckClaimJournal.IsIdentity(wreck.SGUID)
                && ReferenceEquals(BlockEntityManager.instance.GetEntityBySGUID(wreck.SGUID), wreck)
                && LiveMatches(wreck.SGUID).Length == 1 && observedStock.TryGetValue(wreck, out var observation)
                && IsObservedSource(wreck, world, observation.Source);

        private static WreckClaimStockSource CaptureStockSource(ClaimWorld world, ParentEntity wreck, NativeFiles files)
        {
            if (!observedStock.TryGetValue(wreck, out var observation))
                throw new IOException("Stock wreck source was not observed.");
            var source = observation.Source;
            var marker = MarkerName(world, source);
            if (File.Exists(world.Child(marker)) || File.Exists(world.PendingFile(marker)))
                throw new IOException("The stock wreck source was already retired.");
            if (!files.HasNativePayload) return source;

            ValidatePayload(files.Body, wreck.SGUID, worldWreck: true);
            return new WreckClaimStockSource(source.BodySeed, source.TreeGuid,
                WreckClaimFiles.Digest(WreckClaimFiles.Read(files.Body, WreckClaimJournal.MaxBody)),
                WreckClaimFiles.Digest(WreckClaimFiles.Read(files.Metadata, WreckClaimJournal.MaxMetadata)));
        }

        private static void RetireSource(Claim claim, ParentEntity wreck)
        {
            if (claim.Journal.StockSource is { } stock)
            {
                if (!IsObservedSource(wreck, claim.World, stock))
                    throw new IOException("The stock wreck is no longer attached to its observed terrain node.");
                if (removeAttached!.Invoke(nodeEntities!.GetValue(null), new object[] { wreck.attachedNodeId, wreck }) is not true
                    || !WorldEntities.terrainEntities.Remove(wreck))
                    throw new IOException("The stock terrain lists did not release the wreck.");
                wreck.attachedNodeId = 0;
                observedStock.Remove(wreck);
            }
            claim.Source = wreck;
            BlockEntityManager.instance.RemoveAndDestroy(wreck, removeFromLocationMapping: true);
        }

        private static void PublishRetirement(ClaimWorld world, WreckClaimJournal journal)
        {
            var marker = MarkerName(world, journal.StockSource!);
            var bytes = WreckClaimStockSource.MarkerBytes(journal.Identity);
            if (!WreckClaimFiles.MatchesExisting(world.PendingFile(marker), bytes))
                throw new IOException("The accepted stock wreck retirement marker is missing.");
            WreckClaimFiles.Publish(world.Child(marker), bytes);
        }

        private static string MarkerName(ClaimWorld world, WreckClaimStockSource source)
            => source.MarkerName(world.WorldSeed, world.Star.SaveString());

        private sealed class StockObservation
        {
            internal readonly ClaimWorld World;
            internal readonly WreckClaimStockSource Source;
            internal readonly string Identity;
            internal readonly int NodeId;

            internal StockObservation(ClaimWorld world, WreckClaimStockSource source, string identity, int nodeId)
            {
                World = world;
                Source = source;
                Identity = identity;
                NodeId = nodeId;
            }
        }
    }
}
