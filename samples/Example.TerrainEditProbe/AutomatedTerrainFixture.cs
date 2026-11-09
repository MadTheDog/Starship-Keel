#nullable enable
using System;
using System.IO;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Unity.Mathematics;
using UnityEngine;
using VoxelEngine;
using World;
using World.Terrain;
using World.Terrain.Planet;

namespace Sevo.Example.TerrainEditProbe
{
    public sealed partial class Probe
    {
        private enum AutoPhase { Approach, FindGround, FindPatch, RebuiltCut, RebuiltFill }
        private AutoPhase autoPhase;
        private bool autoArmed;
        private bool autoPending;
        private float autoDeadline;
        private float nextAttempt;
        private float nextStatus;
        private int safeSteps;
        private int scanIndex;
        private int planetId;
        private Vector3l site;
        private Vector3l measurementOrigin;
        private Vector3 up;
        private ulong tree;
        private float originalDistance;
        private float cutDistance;
        private string expectedHash = "";
        private Guid autoId;

        private void ArmAutomatedFixture()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE") != "1") return;
            try
            {
                string? game = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE_GAME");
                string? company = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE_COMPANY");
                string? product = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_FIXTURE_PRODUCT");
                if (string.IsNullOrWhiteSpace(game) || !Path.IsPathFullyQualified(game)
                    || string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(product)
                    || string.Equals(company, "Moonfire Entertainment", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(product, "Starship EVO", StringComparison.OrdinalIgnoreCase)
                    || Application.companyName != company || Application.productName != product
                    || !string.Equals(Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Exact verified isolated game path and application identity are required.");

                ProfileProtocol.Open(stateDirectory, Paths.GameRootPath, session);
                autoId = Guid.NewGuid();
                autoPending = true;
            }
            catch (Exception exception) { FinishAutomation(false, exception.Message); }
        }

        private void Update()
        {
            if (autoPending)
            {
                autoPending = false;
                autoDeadline = Time.realtimeSinceStartup + 360f;
                autoArmed = true;
                GameloopManager.OnTransformJobFinished += AdvanceAutomatedFixture;
                Logger.LogInfo($"KEEL_TERRAIN_FIXTURE armed request={autoId:N} session={session}");
            }
            if (autoArmed && Time.realtimeSinceStartup > autoDeadline)
                FinishAutomation(false, "timed_out_" + autoPhase);
            if (autoArmed && autoPhase != AutoPhase.Approach && Time.realtimeSinceStartup >= nextStatus)
            {
                nextStatus = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} phase={autoPhase} safeSteps={safeSteps}"
                    + $" loading={PlayerState.InLoadingScreen} systemEntered={PlayerState.SystemEntered} menu={PlayerState.InMenu}"
                    + $" respawning={Dependency.PlayerManager?.Health.IsRespawning}");
            }
        }

        private void FinishAutomation(bool accepted, string detail)
        {
            autoArmed = false;
            GameloopManager.OnTransformJobFinished -= AdvanceAutomatedFixture;
            Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} accepted={accepted} phase={autoPhase} detail={detail}");
        }

        private void OnDestroy() => GameloopManager.OnTransformJobFinished -= AdvanceAutomatedFixture;

        private void AdvanceAutomatedFixture()
        {
            if (!autoArmed || Time.realtimeSinceStartup < nextAttempt || !TerrainEditService.IsAvailable
                || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle
                || PlayerState.SystemEntered
                || PlayerState.sessionMode != PlayerState.SessionMode.solo || PlayerState.gameMode != PlayerState.GameMode.creative
                || !NetworkServer.active || !NetworkServer.dontListen
                || Dependency.PlayerManager == null || !Dependency.PlayerManager.isLocalPlayer
                || Dependency.PlayerManager.Health.IsRespawning)
                return;
            if (autoPhase == AutoPhase.Approach
                && (PlayerState.InMenu || GameIntroductionResources.instance?.Medbay?.inAnimation != false))
                return;
            safeSteps++;

            try
            {
                nextAttempt = Time.realtimeSinceStartup + 0.25f;
                var player = Dependency.PlayerManager.SkyTransform;
                if (!player.CurrentSystem.Equals(GameIntroductionResources.STARTING_SYSTEM))
                {
                    FinishAutomation(false, "starter_system_required");
                    return;
                }

                int bodyIndex = GameIntroductionResources.STARTING_PLANETID;
                var bodies = PlanetarySystem.SolarBodies;
                if (bodies == null || bodyIndex < 0 || bodyIndex >= bodies.Count) return;
                int generation = bodies[bodyIndex].planetGenerationId;
                if (generation <= 0 || generation >= PlanetResources.Datas.Length) return;
                var planet = PlanetResources.Datas[generation];
                if (planet.transformId <= 0 || planet.transformId >= SkyTransformData.datas.Length
                    || !float.IsFinite(planet.radius) || planet.radius <= 0f) return;
                if (planetId != 0 && generation != planetId)
                    throw new InvalidOperationException("Starter planet changed during the fixture.");
                planetId = generation;

                var transform = SkyTransformData.datas[planet.transformId];
                Vector3 centre = transform.WorldPosition;
                switch (autoPhase)
                {
                    case AutoPhase.Approach:
                        // This is the stock survival introduction's approach to this planet.
                        Vector3 radial = (-(Vector3)transform.systemPosition.ToFloat3()).normalized;
                        radial = (radial + 0.5f * Vector3.up).normalized;
                        site = transform.LocalToSystem(1.05f * planet.radius * radial);
                        player.SetTransform(site, Quaternion.identity);
                        autoPhase = AutoPhase.FindGround;
                        nextAttempt += 1f;
                        Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} approach planet={planetId} radius={planet.radius:R}");
                        break;
                    case AutoPhase.FindGround:
                        Vector3 approach = PositionManager.ToWorldPosition(site);
                        Vector3 approachUp = (approach - centre).normalized;
                        int ring = scanIndex++ % 97;
                        int offsetIndex = ring == 0 ? 0 : 1 + (ring - 1) % 48;
                        Vector3 highOrigin = approach + ScanOffset(approachUp, offsetIndex, ring <= 48 ? 250f : 1000f);
                        float range = Mathf.Clamp(planet.radius * 0.15f, 100f, 50000f);
                        if (!RayDry(highOrigin, approachUp, range, out var ground, out _))
                        {
                            if ((scanIndex - 1) % 32 == 0)
                            {
                                float playerDistance = Vector3.Distance(Dependency.PlayerManager.TransformData.WorldPosition, approach);
                                Logger.LogInfo(FormattableString.Invariant($"KEEL_TERRAIN_FIXTURE request={autoId:N} scan={scanIndex} hit={ground.type} distance={ground.distance:R} playerFromApproach={playerDistance:R}"));
                            }
                            return;
                        }
                        site = ground.SystemPosition;
                        up = (PositionManager.ToWorldPosition(site) - centre).normalized;
                        player.SetTransform(PositionManager.ToSystemPosition(PositionManager.ToWorldPosition(site) + 8f * up),
                            Quaternion.identity);
                        scanIndex = 0;
                        autoPhase = AutoPhase.FindPatch;
                        nextAttempt += 1f;
                        Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} descent range={ground.distance:R}");
                        break;
                    case AutoPhase.FindPatch:
                        Vector3 origin = PositionManager.ToWorldPosition(site) + 12f * up + ScanOffset(up, scanIndex++ % 49, 4f);
                        if (!RayDry(origin, up, RayDistance, out var hit, out var node)
                            || hit.distance < 2f || hit.distance > 17f || Mathf.Abs(Vector3.Dot(hit.WorldNormal, up)) < 0.85f
                            || !IsInterior(node, hit.localPoint) || !TryRead(hit, out _, out var before)) return;
                        measurementOrigin = PositionManager.ToSystemPosition(origin);
                        tree = node.treeGuid;
                        originalDistance = hit.distance;
                        WriteEvidence(autoId, "auto-baseline", node, hit.distance, before);
                        expectedHash = ApplyAutomated(origin, hit, node, before, ProbeOperation.Cut);
                        autoPhase = AutoPhase.RebuiltCut;
                        break;
                    case AutoPhase.RebuiltCut:
                    case AutoPhase.RebuiltFill:
                        CheckRebuilt(PositionManager.ToWorldPosition(measurementOrigin));
                        break;
                }
            }
            catch (Exception exception) { FinishAutomation(false, exception.Message); }
        }

        private bool RayDry(Vector3 origin, Vector3 outward, float range, out VoxelRaycastHit hit, out OctreeNodeData node)
        {
            hit = default;
            node = default;
            var table = OctreeNodeResources.OctreeNodeDatas;
            const uint terrainLayer = 64u; // Stock WorldColliderType.terrainNode layer.
            if (!table.IsCreated || !SkyPhysics.QueryRaycast.Raycast(PositionManager.ToSystemPosition(origin),
                    (float3)(-outward), range, terrainLayer, SkyPhysics.CONTEXT, out hit)
                || hit.type != VoxelRaycastHit.Type.newTerrain || hit.elementId <= 0 || hit.elementId >= table.Length
                || PlanetCommon.IsUnderwater(hit.SystemPosition))
                return false;
            node = table[hit.elementId];
            return node.planetGenerationId == planetId && node.transformId == hit.transformId
                && node.geoState == NodeGeoState.hasGeo && !node.isJobStarted;
        }

        private static Vector3 ScanOffset(Vector3 outward, int index, float stride)
        {
            if (index == 0) return Vector3.zero;
            Vector3 east = Vector3.Cross(outward, Vector3.up);
            if (east.sqrMagnitude < 0.001f) east = Vector3.Cross(outward, Vector3.right);
            east.Normalize();
            Vector3 north = Vector3.Cross(outward, east);
            int cell = index <= 24 ? index - 1 : index;
            return stride * ((cell % 7 - 3) * east + (cell / 7 - 3) * north);
        }

        private static bool IsInterior(OctreeNodeData node, Vector3 localPoint)
        {
            if (!node.densityComputed || !float.IsFinite(node.VoxelSize) || node.VoxelSize <= 0f || node.VoxelSize > 2f
                || !OctreeNodeResources.ShouldSaveNode((int)node.VoxelSize))
                return false;
            var table = OctreeNodeResources.OctreeNodeDatas;
            if (node.ultimateParentId <= 0 || node.ultimateParentId >= table.Length) return false;
            Vector3 centre = (Vector3)node.position + localPoint;
            Vector3 extent = BrushRadius * Vector3.one;
            var rootBounds = table[node.ultimateParentId].DensityBoundsTight;
            return rootBounds.Contains(centre - extent) && rootBounds.Contains(centre + extent);
        }

        private string ApplyAutomated(Vector3 origin, VoxelRaycastHit hit, OctreeNodeData node, Snapshot before,
            ProbeOperation operation)
        {
            bool fill = operation == ProbeOperation.Fill;
            var result = TerrainEditService.ApplyPlane(origin, -up, RayDistance, up, fill ? PlaneStep : -PlaneStep,
                BrushRadius, fill ? TerrainEditOperation.FillBelow : TerrainEditOperation.CutAbove);
            if (result != TerrainEditResult.Queued)
                throw new InvalidOperationException($"{operation} returned {result}.");
            if (!TryRead(hit, out var afterNode, out var after) || afterNode.treeGuid != node.treeGuid
                || afterNode.densitiesId != node.densitiesId)
                throw new InvalidOperationException($"{operation} lost the selected native density slot.");
            WriteEvidence(autoId, fill ? "auto-fill-write" : "auto-cut-write", afterNode, hit.distance, after);
            var delta = TerrainEvidence.Compare(before.Density, after.Density);
            Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} operation={operation} changed={delta.Changed}"
                + $" removedSolid={delta.RemovedSolid} addedSolid={delta.AddedSolid}");
            if (!delta.IsAccepted(operation))
                throw new InvalidOperationException($"{operation} did not produce the expected signed density changes.");
            return after.Hash;
        }

        private void CheckRebuilt(Vector3 origin)
        {
            if (!RayDry(origin, up, RayDistance, out var hit, out var node) || node.treeGuid != tree
                || !TryRead(hit, out _, out var snapshot) || snapshot.Hash != expectedHash)
                return;
            if (autoPhase == AutoPhase.RebuiltCut)
            {
                if (hit.distance <= originalDistance + 0.1f) return;
                cutDistance = hit.distance;
                WriteEvidence(autoId, "auto-cut-rebuilt", node, hit.distance, snapshot);
                expectedHash = ApplyAutomated(origin, hit, node, snapshot, ProbeOperation.Fill);
                autoPhase = AutoPhase.RebuiltFill;
                return;
            }
            if (hit.distance >= cutDistance - 0.1f) return;
            WriteEvidence(autoId, "auto-fill-rebuilt", node, hit.distance, snapshot);
            FinishAutomation(true, "cut_and_fill_native_geometry_rebuilt");
        }
    }
}
