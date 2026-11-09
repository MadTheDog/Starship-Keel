using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.SeatInteractionProbe
{
    public sealed partial class Probe
    {
        private const string FootFlag = "SEVO_KEEL_SEATED_FOOT_FIXTURE";
        private const string FixtureShip = "V27 Hotwing";
        private static readonly Vector3 FixtureLeftFoot = new Vector3(0.48f, 0.12f, 0.18f);
        private static readonly Vector3 FixtureRightFoot = new Vector3(0.48f, 0.12f, -0.18f);
        private static Probe? footFixture;
        private Harmony? footHarmony;
        private IkReader? footReader;
        private string footSession = "";
        private string footCharacter = "";
        private string? ikError;
        private bool footPending;
        private bool footArmed;
        private bool footApplied;
        private bool footExit;
        private bool footCleared;
        private float footDeadline;
        private float footReadySince;
        private int footReadySamples;
        private float nextFootGateLog;
        private string footGate = "waiting for the world";
        private PlayerCommandManager? footReadyPlayer;
        private ulong footReadyStar;
        private Vector3l lastFootReadyPosition;
        private EnterCommandSeat? footSeat;
        private CommandYoke? footYoke;
        private bool nativeYokeSignal;
        private Vector3l expectedLeft, expectedRight;
        private float nextFootIkLog;
        private int expectedFrame = -1;
        private IkGoal nativeLeftHand, nativeRightHand;
        private int nativeFrame = -1;
        private int nativeLayer = -1;

        private void PrepareFootFixture()
        {
            if (Environment.GetEnvironmentVariable(FootFlag) != "1") return;
            try
            {
                Require(Environment.GetEnvironmentVariable("SEVO_KEEL_DISMOUNT_FIXTURE") != "1",
                    "Run the seated-foot and dismount fixtures separately.");
                footSession = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                var game = Environment.GetEnvironmentVariable(FootFlag + "_GAME");
                footCharacter = Environment.GetEnvironmentVariable(FootFlag + "_CHARACTER") ?? "";
                Require(Guid.TryParseExact(footSession, "N", out _) && !string.IsNullOrWhiteSpace(state)
                    && !string.IsNullOrWhiteSpace(game) && Path.IsPathFullyQualified(game)
                    && Application.companyName == "SEVO Keel Conn Lab 010" && Application.productName == "SEVO CT Test"
                    && string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    && footCharacter.EndsWith("-" + footSession.Substring(0, 8), StringComparison.Ordinal)
                    && Environment.GetEnvironmentVariable("SEVO_PLATFORM_EXPERIMENTAL_CONTENT") == "1"
                    && Environment.GetEnvironmentVariable("SEVO_KEEL_EXPERIMENTAL_SEATED_POSE_TARGETS") == "1",
                    "Use the exact disposable connector lab, profile and session character with foot targets enabled.");
                ProfileProtocol.Open(state!, Paths.GameRootPath, footSession);
                footPending = true;
                FootLog("armed", "waiting for the isolated Creative solo world");
            }
            catch (Exception exception) { FootLog("fail", exception.GetBaseException().Message); }
        }

        private void UpdateFootFixture()
        {
            if (!footPending) return;
            if (footDeadline == 0f) footDeadline = Time.realtimeSinceStartup + 150f;
            if (Time.realtimeSinceStartup > footDeadline)
            {
                footPending = false;
                FootLog("fail", "disposable world readiness: " + footGate);
                return;
            }
            if (!TryStableFootPlayer(out var player)) return;
            footPending = false;
            FootLog("ready", "positioned player is clear of native commands");
            StartCoroutine(GuardFootFixture(player!));
        }

        private IEnumerator GuardFootFixture(PlayerCommandManager player)
        {
            var routine = RunFootFixture(player);
            try
            {
                while (true)
                {
                    object? frame;
                    try
                    {
                        if (!routine.MoveNext()) break;
                        frame = routine.Current;
                    }
                    catch (Exception exception)
                    {
                        FootLog("fail", exception.GetBaseException().Message);
                        break;
                    }
                    yield return frame;
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
                DisposeFootFixture();
            }
        }

        private IEnumerator RunFootFixture(PlayerCommandManager player)
        {
            var position = default(WorldTransform);
            float deadline = Time.realtimeSinceStartup + 45f;
            while (WaitForFoot(() => TryStableFootPlayer(out var current) && ReferenceEquals(current, player)
                && (TryFootSite(player, out position) || ReportFootGate("waiting for a clear fixture site", reset: false)),
                deadline, "clear fixture spawn")) yield return null;
            var path = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "Starship EVO_Data", "StreamingAssets",
                "Default Blueprints", FixtureShip, FixtureShip + ".sevo"));
            Require(File.Exists(path), "The bundled stock Hotwing is unavailable.");
            var meta = ParentEntity.GetMetaData(path);
            Require(meta.name == FixtureShip && meta.type == EntityMetaType.spaceship && !meta.includesDockedEntities
                && !meta.isWorldWreck && meta.powerProduced > 0f, "Bundled ship metadata changed.");
            var name = "Keel Foot Fixture " + footSession.Substring(0, 8);
            var instance = new EntityInstanceData
            {
                entityName = name,
                blueprintFullPath = path,
                ownerPlayerNetID = player.NetworkSetup.netId,
                npcBehavior = NpcSpaceshipCommon.Behavior.playerControllable,
                entityType = EntityType.spaceship,
                isNewInstance = true,
                saveInWorld = false
            };
            Require(instance.ownerPlayerNetID != 0, "Local native ownership is unavailable.");
            FootLog("site", FormattableString.Invariant($"star={player.SkyTransform.CurrentSystem.seed};position={position.systemPosition}"));
            var grid = player.NetworkSetup.ServerLoadBlueprint(instance, position, player.SkyTransform.CurrentSystem);
            if (grid == null) throw new InvalidOperationException("The native loader refused the fixture ship.");
            FootLog("spawned", $"entity={grid.ID};initialized={grid.IsInitialized}");
            Spaceship? ship = null;
            deadline = Time.realtimeSinceStartup + 25f;
            while (WaitForFoot(() => TryResolveFootShip(grid, name, instance.ownerPlayerNetID, out ship),
                deadline, "initialized and powered native Hotwing")) yield return null;
            FootLog("ship-ready", $"grid={grid.SGUID};owner={instance.ownerPlayerNetID};cores={ship!.spaceshipCores.Count}");

            var core = ship!.spaceshipCores[0];
            var yoke = core.CommandYoke;
            var seat = yoke?.Seat;
            Require(yoke != null && seat != null && !core.IsRemoved && !yoke.IsRemoved && !seat.IsRemoved,
                "The native Hotwing did not resolve its stock linked controls.");
            Require(IsFootCommand(grid, core) && IsFootCommand(grid, yoke!) && IsFootCommand(grid, seat!)
                && yoke!.brickData.brickId == TabLogic.YOKE.brickID && seat!.brickData.brickId == TabInterior.SEAT_PILOT.brickID,
                "Stock pilot seat or yoke identity changed.");

            footReader = new IkReader();
            footHarmony = new Harmony(Id + ".native-seated-foot-fixture");
            var hook = AccessTools.DeclaredMethod(typeof(CharacterAnimator), nameof(CharacterAnimator.OnAnimatorIK), new[] { typeof(int) });
            footHarmony.Patch(hook, postfix: new HarmonyMethod(typeof(Probe), nameof(BeforeFootTargets))
            { priority = Priority.First, before = new[] { Plugin.Id + ".seatedPoseTargets" } });
            footHarmony.Patch(hook, postfix: new HarmonyMethod(typeof(Probe), nameof(AfterFootTargets))
            { priority = Priority.Last, after = new[] { Plugin.Id + ".seatedPoseTargets" } });
            var yokeHook = AccessTools.DeclaredMethod(typeof(CommandYoke), "UpdateArmIk",
                new[] { typeof(PlayerCommandManager), typeof(bool) });
            footHarmony.Patch(yokeHook, postfix: new HarmonyMethod(typeof(Probe), nameof(ObserveYoke)));
            footSeat = seat;
            footYoke = yoke;
            footFixture = this;
            bool originalPerspective = player.ThirdPersonOn;
            bool seatRequested = false, exitRequested = false;
            try
            {
                ResetFootReadiness();
                deadline = Time.realtimeSinceStartup + 25f;
                while (WaitForFoot(() => TryStableFootPlayer(out var current) && ReferenceEquals(current, player),
                    deadline, "stable player before stock seat entry")) yield return null;
                Require(!grid.isDestroyed && !grid.WillBeUnspawned && ReferenceEquals(grid.ID.Entity, grid)
                    && !ship.IsControlled && !seat!.IsRemoved && IsFootCommand(grid, seat),
                    "The exact stock seat became unavailable before entry.");
                player.SetPerspectiveMode(true);
                player.CommandActivator.ServerEnter(seat!.brickCoord);
                seatRequested = true;
                deadline = Time.realtimeSinceStartup + 10f;
                while (WaitForFoot(() => player.IsSeated && grid.controlMode == BaseVehicle.VehicleControlMode.localPlayer
                    && ReferenceEquals(player.LockedEntity?.Parent, grid), deadline, "stock seat and yoke entry")) yield return null;
                FootLog("seated", "waiting for a visible humanoid Animator and active stock yoke hands");
                float settledAt = Time.timeSinceLevelLoad + 1.1f;
                deadline = Time.realtimeSinceStartup + 8f;
                while (WaitForFoot(() => Time.timeSinceLevelLoad >= settledAt && player.IsSeated
                    && ReferenceEquals(player.LockedEntity?.Parent, grid), deadline, "stock seat body interpolation")) yield return null;
                footArmed = true;
                deadline = Time.realtimeSinceStartup + 15f;
                while (WaitForFoot(() => footApplied, deadline, "both native foot goals with unchanged active yoke hand goals")) yield return null;

                footExit = true;
                player.CommandActivator.ServerExit(core.brickCoord);
                player.CommandActivator.ServerExit(yoke!.brickCoord);
                player.CommandActivator.ServerExit(seat.brickCoord);
                exitRequested = true;
                deadline = Time.realtimeSinceStartup + 12f;
                while (WaitForFoot(() => footCleared && !player.IsSeated
                    && grid.controlMode == BaseVehicle.VehicleControlMode.notControlled, deadline, "stock exit and cleared foot weights")) yield return null;
                FootLog("pass", "both foot goals matched seat-local targets, stock yoke hand goals were unchanged, and foot weights cleared after exit");
            }
            finally
            {
                if (FootWorldReady())
                {
                    if (seatRequested && !exitRequested)
                    {
                        player.CommandActivator.ServerExit(core.brickCoord);
                        player.CommandActivator.ServerExit(yoke!.brickCoord);
                        player.CommandActivator.ServerExit(seat!.brickCoord);
                    }
                    player.SetPerspectiveMode(originalPerspective);
                }
            }
        }

        private static bool IsFootCommand(ParentEntity grid, Command command)
            => ReferenceEquals(command.brickCoord.Parent, grid) && ReferenceEquals(command.brickCoord.Command, command);

        private bool TryResolveFootShip(ParentEntity grid, string expectedName, uint owner, out Spaceship? ship)
        {
            ship = null;
            if (grid == null || grid.isDestroyed || grid.WillBeUnspawned)
                throw new InvalidOperationException("The native Hotwing was defeated or unloaded during load.");
            if (!grid.IsInitialized)
                return ReportFootGate("waiting for the native Hotwing load", reset: false);
            var manager = BlockEntityManager.instance;
            var key = grid.SGUID;
            var byKey = string.IsNullOrEmpty(key) || manager == null ? null : manager.GetEntityBySGUID(key);
            var byId = grid.ID.Entity;
            if (byKey == null || byId == null || grid.Sync == null || grid.Rigidbody == null || grid.Systems == null)
                return ReportFootGate($"waiting for native grid registration key={key} byKey={byKey != null} byId={byId != null} sync={grid.Sync != null} body={grid.Rigidbody != null} systems={grid.Systems != null}", reset: false);
            Require(ReferenceEquals(byKey, grid) && ReferenceEquals(byId, grid), "The native Hotwing identity was replaced.");
            Require(grid.EntityName == expectedName && !grid.saveInWorld && grid.Type == EntityType.spaceship,
                $"Unexpected fixture metadata name={grid.EntityName} saved={grid.saveInWorld} type={grid.Type}.");
            var data = grid.Sync.instanceData;
            Require(data.ownerPlayerNetID == owner && data.pilotId == 0,
                $"Unexpected fixture owner or pilot owner={data.ownerPlayerNetID} expected={owner} pilot={data.pilotId}.");
            Require(!grid.Rigidbody.isParented && grid.Dock != null && !grid.Dock.IsDockedOrLanded && !grid.Dock.IsAutoDockingToPort,
                "The native Hotwing is attached or docking.");

            var native = grid.GetComponent<Spaceship>();
            if (native == null || native.spaceshipCores.Count == 0 || grid.Systems.IsPowerDepleted)
                return ReportFootGate($"waiting for native flight components ship={native != null} cores={native?.spaceshipCores.Count} powerDepleted={grid.Systems.IsPowerDepleted}", reset: false);
            Require(!native.IsControlled && native.spaceshipCores.Count == 1
                && grid.EntityCrew.crews.Count == 0 && grid.EntityCrew.npcCharactersIds.Count == 0,
                $"Unexpected fixture occupancy controlled={native.IsControlled} cores={native.spaceshipCores.Count} crew={grid.EntityCrew.crews.Count} npc={grid.EntityCrew.npcCharactersIds.Count}.");
            ship = native;
            return true;
        }

        private bool TrySelectFootFixture(SeatInteractionContext context, out SeatedFootTargets? targets)
        {
            targets = null;
            if (!ReferenceEquals(footFixture, this) || !ReferenceEquals(context.Seat.Command, footSeat)) return false;
            if (footArmed && !footExit)
            {
                var seat = context.Seat;
                expectedLeft = seat.TransformData.LocalToSystem(seat.BlockToEntity(FixtureLeftFoot));
                expectedRight = seat.TransformData.LocalToSystem(seat.BlockToEntity(FixtureRightFoot));
                expectedFrame = Time.frameCount;
                targets = new SeatedFootTargets(FixtureLeftFoot, FixtureRightFoot);
            }
            return true;
        }

        private static void ObserveYoke(CommandYoke __instance, PlayerCommandManager __0, bool __1)
        {
            if (__1 && footFixture is { } fixture && ReferenceEquals(fixture.footYoke, __instance)
                && ReferenceEquals(__0, Dependency.PlayerManager))
                fixture.nativeYokeSignal = true;
        }

        private static void BeforeFootTargets(CharacterAnimator __instance, int __0)
        {
            if (footFixture is not { } fixture || !fixture.TryGetLocalAnimator(__instance, out var animator)) return;
            try
            {
                var read = fixture.footReader!;
                fixture.nativeFrame = -1;
                if (!fixture.footArmed || !fixture.nativeYokeSignal || fixture.footExit || fixture.footApplied) return;
                var left = read.Goal(animator, read.LeftHand);
                var right = read.Goal(animator, read.RightHand);
                if (left.Weight <= 0.05f || right.Weight <= 0.05f) return;
                fixture.nativeFrame = Time.frameCount;
                fixture.nativeLayer = __0;
                fixture.nativeLeftHand = left;
                fixture.nativeRightHand = right;
            }
            catch (Exception exception) { fixture.ikError = exception.GetBaseException().Message; }
        }

        private static void AfterFootTargets(CharacterAnimator __instance, int __0)
        {
            if (footFixture is not { } fixture || !fixture.TryGetLocalAnimator(__instance, out var animator)) return;
            try
            {
                var read = fixture.footReader!;
                if (fixture.footExit)
                {
                    if (!Dependency.PlayerManager.IsSeated && !fixture.footCleared
                        && read.Goal(animator, read.LeftFoot).Weight < 0.001f
                        && read.Goal(animator, read.RightFoot).Weight < 0.001f)
                    {
                        fixture.footCleared = true;
                        fixture.FootLog("cleared", "both native foot position weights are zero");
                    }
                    return;
                }
                int frame = Time.frameCount;
                if (fixture.footApplied || fixture.nativeFrame != frame || fixture.nativeLayer != __0
                    || fixture.expectedFrame < frame - 1) return;
                var left = read.Goal(animator, read.LeftFoot);
                var right = read.Goal(animator, read.RightFoot);
                var leftTarget = PositionManager.ToWorldPosition(fixture.expectedLeft);
                var rightTarget = PositionManager.ToWorldPosition(fixture.expectedRight);
                if (left.Weight < 0.999f || right.Weight < 0.999f)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now >= fixture.nextFootIkLog)
                    {
                        fixture.nextFootIkLog = now + 3f;
                        var leftBone = read.GetBone(animator, read.LeftFootBone);
                        var rightBone = read.GetBone(animator, read.RightFootBone);
                        float leftDistance = leftBone == null ? float.NaN : Vector3.Distance(leftBone.position, leftTarget);
                        float rightDistance = rightBone == null ? float.NaN : Vector3.Distance(rightBone.position, rightTarget);
                        fixture.FootLog("ik-wait", FormattableString.Invariant(
                            $"leftTarget={leftTarget:F4};rightTarget={rightTarget:F4};leftWeight={left.Weight:F3};rightWeight={right.Weight:F3};leftAnkleDistance={leftDistance:F3};rightAnkleDistance={rightDistance:F3}"));
                    }
                    return;
                }
                float leftError = Vector3.Distance(left.Position, leftTarget);
                float rightError = Vector3.Distance(right.Position, rightTarget);
                Require(leftError < 0.002f && rightError < 0.002f, "Native foot goals do not match the selected seat-local targets.");
                Require(fixture.nativeLeftHand.Matches(read.Goal(animator, read.LeftHand))
                    && fixture.nativeRightHand.Matches(read.Goal(animator, read.RightHand)), "Stock yoke hand goals changed across Keel's foot callback.");
                fixture.footApplied = true;
                fixture.FootLog("applied", FormattableString.Invariant(
                    $"left={left.Position:F4};right={right.Position:F4};leftWeight={left.Weight:F3};rightWeight={right.Weight:F3};leftError={leftError:F5};rightError={rightError:F5};leftHandWeight={fixture.nativeLeftHand.Weight:F3};rightHandWeight={fixture.nativeRightHand.Weight:F3};yokeSignal=true"));
            }
            catch (Exception exception) { fixture.ikError = exception.GetBaseException().Message; }
        }

        private bool TryGetLocalAnimator(CharacterAnimator character, out UnityEngine.Object animator)
        {
            animator = null!;
            var player = Dependency.PlayerManager;
            if (player == null || !ReferenceEquals(player.CharacterAnimator, character) || !character.enabled || footReader == null)
                return false;
            try
            {
                if (footReader.GetAnimator(character) is not { } native || native == null) return false;
                animator = native;
                return true;
            }
            catch (Exception exception)
            {
                ikError = exception.GetBaseException().Message;
                return false;
            }
        }

        private bool WaitForFoot(Func<bool> condition, float deadline, string stage)
        {
            if (ikError != null) throw new InvalidOperationException(ikError);
            if (FootWorldReady() && condition()) return false;
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(stage + " [" + footGate + "]");
            return true;
        }

        private bool FootWorldReady()
            => ReadyForSeatFixture() && NetworkServer.dontListen && SaveManager.CharacterName == footCharacter
                && GameIntroductionResources.instance?.Medbay?.inAnimation == false;

        private bool TryStableFootPlayer(out PlayerCommandManager? player)
        {
            player = null;
            if (!GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe || GameloopManager.jobStarted)
                return ReportFootGate("waiting for a native-safe frame", reset: false);
            if (!ReadyForSeatFixture() || !NetworkServer.dontListen || SaveManager.CharacterName != footCharacter)
                return ReportFootGate("waiting for the disposable Creative solo player");
            if (GameIntroductionResources.instance?.Medbay?.inAnimation != false)
                return ReportFootGate("waiting for the stock Medbay animation");

            var local = Dependency.PlayerManager;
            var sky = local.SkyTransform;
            if (sky == null || local.PlayerRigidbody == null)
                return ReportFootGate("waiting for the native player transform and body");
            var system = sky.CurrentSystem;
            var position = local.TransformData.systemPosition;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData) || position.Equals(Vector3l.zero)
                || PlanetarySystem.SolarBodies.Count == 0)
                return ReportFootGate("waiting for the player to be positioned in the active star");
            var activator = local.CommandActivator;
            if (activator == null || activator.IsInEnterCommand || PlayerState.SystemEntered || PlayerState.IsControllingVehicle
                || local.IsSeated || local.IsExitingSeat)
                return ReportFootGate($"waiting for native command release entered={PlayerState.SystemEntered} active={activator?.IsInEnterCommand} seated={local.IsSeated} exiting={local.IsExitingSeat}");

            float now = Time.realtimeSinceStartup;
            if (!ReferenceEquals(footReadyPlayer, local) || footReadyStar != system.seed
                || Vector3l.Distance(lastFootReadyPosition, position) > 2f)
            {
                footReadyPlayer = local;
                footReadyStar = system.seed;
                footReadySince = now;
                footReadySamples = 0;
            }
            lastFootReadyPosition = position;
            if (++footReadySamples < 3 || now - footReadySince < 0.75f)
                return ReportFootGate("waiting for the positioned player to settle", reset: false);
            player = local;
            return true;
        }

        private void ResetFootReadiness()
        {
            footReadyPlayer = null;
            footReadySamples = 0;
        }

        private bool ReportFootGate(string reason, bool reset = true)
        {
            if (reset) ResetFootReadiness();
            footGate = reason;
            float now = Time.realtimeSinceStartup;
            if (now >= nextFootGateLog)
            {
                nextFootGateLog = now + 5f;
                FootLog("readiness", reason);
            }
            return false;
        }

        private static bool TryFootSite(PlayerCommandManager player, out WorldTransform pose)
        {
            pose = default;
            var system = player.SkyTransform.CurrentSystem;
            var origin = player.TransformData.systemPosition;
            if (system.seed == 0 || !system.Equals(PlanetarySystem.CurrentStarData) || origin.Equals(Vector3l.zero)
                || PlanetarySystem.SolarBodies.Count == 0) return false;
            foreach (var direction in new[] { Vector3.up, Vector3.right, Vector3.forward })
            {
                var candidate = origin + new Vector3l(direction * 250f);
                if (PlanetarySystem.SolarBodies.Exists(body => body.IsShipTooClose(candidate))) continue;
                bool occupied = false;
                foreach (var other in SkyRigidbody.Entities)
                    if (other != null && !other.WillBeUnspawned && other.SkyTransform.CurrentSystem.Equals(system)
                        && Vector3l.Distance(other.TransformData.systemPosition, candidate) < 120f + other.ParentRadius)
                    {
                        occupied = true;
                        break;
                    }
                if (occupied) continue;
                pose = new WorldTransform(candidate, Quaternion.identity);
                return true;
            }
            return false;
        }

        private void DisposeFootFixture()
        {
            if (ReferenceEquals(footFixture, this)) footFixture = null;
            footHarmony?.UnpatchSelf();
            footHarmony = null;
        }

        private void FootLog(string stage, string message)
            => Logger.LogInfo($"KEEL_SEATED_FOOT|{footSession}|{stage}|{message.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ')}");

        private readonly struct IkGoal
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly float Weight;
            internal readonly float RotationWeight;

            internal IkGoal(Vector3 position, Quaternion rotation, float weight, float rotationWeight)
            {
                Position = position;
                Rotation = rotation;
                Weight = weight;
                RotationWeight = rotationWeight;
            }

            internal bool Matches(IkGoal other) => Vector3.Distance(Position, other.Position) < 0.001f
                && Quaternion.Angle(Rotation, other.Rotation) < 0.1f
                && Mathf.Abs(Weight - other.Weight) < 0.001f && Mathf.Abs(RotationWeight - other.RotationWeight) < 0.001f;
        }

        private sealed class IkReader
        {
            private readonly MethodInfo getAnimator, human, bone, position, rotation, weight, rotationWeight;
            internal readonly object LeftFoot, RightFoot, LeftHand, RightHand, LeftFootBone, RightFootBone;

            internal IkReader()
            {
                getAnimator = AccessTools.PropertyGetter(typeof(CharacterAnimator), "animator")
                    ?? throw new InvalidOperationException("Native Animator getter is unavailable.");
                var animator = getAnimator.ReturnType;
                var goal = animator.Assembly.GetType("UnityEngine.AvatarIKGoal", true)!;
                var bones = animator.Assembly.GetType("UnityEngine.HumanBodyBones", true)!;
                LeftFoot = Enum.Parse(goal, "LeftFoot");
                RightFoot = Enum.Parse(goal, "RightFoot");
                LeftHand = Enum.Parse(goal, "LeftHand");
                RightHand = Enum.Parse(goal, "RightHand");
                LeftFootBone = Enum.Parse(bones, "LeftFoot");
                RightFootBone = Enum.Parse(bones, "RightFoot");
                MethodInfo Method(string name, params Type[] parameters) => animator.GetMethod(name, parameters)
                    ?? throw new InvalidOperationException("Missing native Animator method " + name + ".");
                human = Method("get_isHuman");
                bone = Method("GetBoneTransform", bones);
                position = Method("GetIKPosition", goal);
                rotation = Method("GetIKRotation", goal);
                weight = Method("GetIKPositionWeight", goal);
                rotationWeight = Method("GetIKRotationWeight", goal);
            }

            internal UnityEngine.Object? GetAnimator(CharacterAnimator character)
            {
                var animator = getAnimator.Invoke(character, null) as UnityEngine.Object;
                return animator != null && human.Invoke(animator, null) is true ? animator : null;
            }

            internal Transform? GetBone(UnityEngine.Object animator, object requestedBone)
                => bone.Invoke(animator, new[] { requestedBone }) as Transform;

            internal IkGoal Goal(UnityEngine.Object animator, object requestedGoal)
            {
                var argument = new[] { requestedGoal };
                return new IkGoal((Vector3)position.Invoke(animator, argument)!, (Quaternion)rotation.Invoke(animator, argument)!,
                    (float)weight.Invoke(animator, argument)!, (float)rotationWeight.Invoke(animator, argument)!);
            }
        }
    }
}
