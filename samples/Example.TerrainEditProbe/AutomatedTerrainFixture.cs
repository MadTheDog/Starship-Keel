#nullable enable
using System;
using System.IO;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
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
        private bool coarseRelocated;
        private float autoDeadline;
        private float nextAttempt;
        private float nextStatus;
        private int safeSteps;
        private int scanIndex;
        private int refusedEdits;
        private int planetId;
        private Vector3l site;
        private Vector3l measurementOrigin;
        private Vector3 up;
        private ulong tree;
        private float originalDistance;
        private float cutDistance;
        private string expectedHash = "";
        private Guid autoId;
        private bool verifyAnchor;
        private bool verifyFillMaterial;
        private TerrainAnchorPose cutAnchor;

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
                verifyAnchor = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_ANCHOR_FIXTURE") == "1";
                verifyFillMaterial = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_FILL_MATERIAL_FIXTURE") == "1";
                if (verifyFillMaterial)
                    TerrainMaterialRegistration.Register(Id, "fill", 1, "RAW_IRON_A");
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
            if (AdvanceSaveEvidence()) return;
            if (autoArmed && Time.realtimeSinceStartup > autoDeadline)
                FinishAutomation(false, "timed_out_" + autoPhase);
            if (!autoArmed) UninstallCoarseApproachObserver();
            if (autoArmed && Time.realtimeSinceStartup >= nextStatus)
            {
                nextStatus = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} phase={autoPhase} safeSteps={safeSteps}"
                    + $" loading={PlayerState.InLoadingScreen} systemEntered={PlayerState.SystemEntered} menu={PlayerState.InMenu}"
                    + $" respawning={Dependency.PlayerManager?.Health.IsRespawning}"
                    + $" medbayAnimating={GameIntroductionResources.instance?.Medbay?.inAnimation}");
            }
        }

        private void FinishAutomation(bool accepted, string detail)
        {
            autoArmed = false;
            GameloopManager.OnTransformJobFinished -= AdvanceAutomatedFixture;
            Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} accepted={accepted} phase={autoPhase} detail={detail}");
        }

        private void OnDestroy()
        {
            GameloopManager.OnTransformJobFinished -= AdvanceAutomatedFixture;
            UninstallCoarseApproachObserver();
        }

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
                && ((!verifyAnchor && PlayerState.InMenu) || GameIntroductionResources.instance?.Medbay?.inAnimation != false))
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
                        InstallCoarseApproachObserver();
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
                        float spacing = ring <= 48 ? 250f : 1000f;
                        if (coarseRelocated) spacing *= 0.016f;
                        Vector3 highOrigin = approach + ScanOffset(approachUp, offsetIndex, spacing);
                        float range = coarseRelocated ? 100f : Mathf.Clamp(planet.radius * 0.15f, 100f, 50000f);
                        if (!RayDry(highOrigin, approachUp, range, out var ground, out _))
                        {
                            if (!coarseRelocated && TryTakeCoarseApproach(out var candidate))
                            {
                                RelocateAboveCoarseMesh(player, candidate);
                                return;
                            }
                            if ((scanIndex - 1) % 32 == 0)
                            {
                                float playerDistance = Vector3.Distance(Dependency.PlayerManager.TransformData.WorldPosition, approach);
                                Logger.LogInfo(FormattableString.Invariant($"KEEL_TERRAIN_FIXTURE request={autoId:N} scan={scanIndex} hit={ground.type} distance={ground.distance:R} playerFromApproach={playerDistance:R}"));
                            }
                            return;
                        }
                        site = ground.SystemPosition;
                        Vector3 surface = PositionManager.ToWorldPosition(site);
                        up = (surface - centre).normalized;
                        player.SetTransform(PositionManager.ToSystemPosition(surface + 8f * up),
                            Quaternion.identity);
                        scanIndex = 0;
                        autoPhase = AutoPhase.FindPatch;
                        nextAttempt += 1f;
                        float hitPointError = Vector3.Distance(surface, highOrigin - approachUp * ground.distance);
                        Logger.LogInfo(FormattableString.Invariant($"KEEL_TERRAIN_FIXTURE request={autoId:N} descent range={ground.distance:R} nativePointError={hitPointError:R}"));
                        break;
                    case AutoPhase.FindPatch:
                        int index = scanIndex++;
                        Vector3 offset = ScanOffset(up, index % 49, 4f);
                        if (verifyAnchor || verifyFillMaterial)
                            offset += ScanOffset(up, index / 49 % 49, 0.75f);
                        Vector3 origin = PositionManager.ToWorldPosition(site) + 12f * up + offset;
                        bool dry = RayDry(origin, up, RayDistance, out var hit, out var node);
                        float slope = dry ? Mathf.Abs(Vector3.Dot(hit.WorldNormal, up)) : 0f;
                        bool interior = dry && IsInterior(node, hit.localPoint);
                        if (!dry || hit.distance < 2f || hit.distance > 17f || slope < 0.85f
                            || !interior || !TryRead(hit, out _, out var before))
                        {
                            if (scanIndex % 16 == 1)
                                Logger.LogInfo(FormattableString.Invariant($"KEEL_TERRAIN_FIXTURE request={autoId:N} patchScan={scanIndex} kind={hit.type} dry={dry} range={hit.distance:R} voxel={node.VoxelSize:R} slope={slope:R} interior={interior}"));
                            if (scanIndex >= 49 && !coarseRelocated && TryTakeCoarseApproach(out var dryCandidate))
                                RelocateAboveCoarseMesh(player, dryCandidate);
                            return;
                        }
                        measurementOrigin = PositionManager.ToSystemPosition(origin);
                        tree = node.treeGuid;
                        originalDistance = hit.distance;
                        if (!TryApplyAutomated(origin, hit, node, before, ProbeOperation.Cut, out expectedHash)) return;
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

        private void RelocateAboveCoarseMesh(SkyTransform player, Vector3l candidate)
        {
            site = candidate;
            coarseRelocated = true;
            scanIndex = 0;
            player.SetTransform(site, Quaternion.identity);
            autoPhase = AutoPhase.FindGround;
            nextAttempt += 1f;
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
            var nodeBounds = node.DensityBoundsTight;
            return rootBounds.Contains(centre - extent) && rootBounds.Contains(centre + extent)
                && nodeBounds.Contains(centre - extent) && nodeBounds.Contains(centre + extent);
        }

        private bool TryApplyAutomated(Vector3 origin, VoxelRaycastHit hit, OctreeNodeData node, Snapshot before,
            ProbeOperation operation, out string afterHash)
        {
            afterHash = "";
            bool fill = operation == ProbeOperation.Fill;
            if (verifyAnchor && !fill)
            {
                if (!TerrainAnchor.IsAvailable) throw new InvalidOperationException("Terrain anchor hook unavailable.");
                var bodies = PlanetarySystem.SolarBodies;
                var body = bodies[GameIntroductionResources.STARTING_PLANETID];
                var target = new BodyEnvironmentTarget(body.parentStar.seed, body.Seed);
                var captured = TerrainAnchor.Capture(target, origin, -up, RayDistance, out cutAnchor);
                if (captured != TerrainAnchorResult.Resolved)
                {
                    if (refusedEdits++ % 16 == 0)
                    {
                        var ray = WorldSurfaceQuery.Raycast(origin, -up, RayDistance, false, out var surface);
                        Logger.LogInfo($"KEEL_TERRAIN_ANCHOR_FIXTURE request={autoId:N} capture={captured} tree={node.treeGuid:x16}"
                            + $" ray={ray}/{surface.Kind} bodyTransform={body.transformId} nodeTransform={node.transformId}"
                            + $" dirty={node.isGeoDirty} fade={node.fadeState}");
                    }
                    return false;
                }
                var rebound = TerrainAnchor.Resolve(cutAnchor, out var point, out _);
                if (rebound != TerrainAnchorResult.Resolved)
                {
                    if (refusedEdits++ % 16 == 0)
                        Logger.LogInfo($"KEEL_TERRAIN_ANCHOR_FIXTURE request={autoId:N} resolve={rebound} tree={node.treeGuid:x16}");
                    return false;
                }
                if (Vector3.Distance(point, origin - up * hit.distance) > 0.1f)
                    throw new InvalidOperationException("Terrain anchor resolved away from the original surface ray.");
            }
            var result = fill && verifyFillMaterial
                ? TerrainEditService.ApplyPlaneFillWithMaterial(origin, -up, RayDistance, up, PlaneStep, BrushRadius, Id + ":fill")
                : TerrainEditService.ApplyPlane(origin, -up, RayDistance, up, fill ? PlaneStep : -PlaneStep,
                    BrushRadius, fill ? TerrainEditOperation.FillBelow : TerrainEditOperation.CutAbove);
            if (result is TerrainEditResult.TerrainNotReady or TerrainEditResult.NoTerrainHit
                or TerrainEditResult.NoChange or TerrainEditResult.FrameBudgetUsed)
            {
                if (refusedEdits++ % 16 == 0)
                    Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} retry={operation} result={result} tree={node.treeGuid:x16} voxel={node.VoxelSize:R}");
                return false;
            }
            if (result != TerrainEditResult.Queued)
                throw new InvalidOperationException($"{operation} returned {result}.");
            if (verifyAnchor && !fill && TerrainAnchor.Resolve(cutAnchor, out _, out _) != TerrainAnchorResult.SurfaceMissing)
                throw new InvalidOperationException("Queued terrain geometry left the original anchor active.");
            if (!TryRead(hit, out var afterNode, out var after) || afterNode.treeGuid != node.treeGuid
                || afterNode.densitiesId != node.densitiesId)
                throw new InvalidOperationException($"{operation} lost the selected native density slot.");
            WriteEvidence(autoId, fill ? "auto-cut-rebuilt" : "auto-baseline", node, hit.distance, before);
            WriteEvidence(autoId, fill ? "auto-fill-write" : "auto-cut-write", afterNode, hit.distance, after);
            var delta = TerrainEvidence.Compare(before.Density, after.Density);
            Logger.LogInfo($"KEEL_TERRAIN_FIXTURE request={autoId:N} operation={operation} changed={delta.Changed}"
                + $" removedSolid={delta.RemovedSolid} addedSolid={delta.AddedSolid}");
            if (!delta.IsAccepted(operation))
                throw new InvalidOperationException($"{operation} did not produce the expected signed density changes.");
            if (fill && verifyFillMaterial)
            {
                if (!TerrainMaterialRegistration.TryGetId(Id + ":fill", out byte material))
                    throw new InvalidOperationException("Registered fill material unavailable after edit.");
                for (int i = 0; i < before.Density.Length; i++)
                {
                    int offset = 8 * i + 6;
                    if (before.Density[i] >= 0 && after.Density[i] < 0)
                    {
                        if (after.Packed[offset] != material || after.Packed[offset + 1] != material)
                            throw new InvalidOperationException("A newly solid sample did not retain the registered material.");
                    }
                    else if (before.Packed[offset] != after.Packed[offset] || before.Packed[offset + 1] != after.Packed[offset + 1])
                        throw new InvalidOperationException("Fill changed the material of a sample that did not become solid.");
                }
                Logger.LogInfo($"KEEL_TERRAIN_FILL_MATERIAL_FIXTURE request={autoId:N} material={material} newSolid={delta.AddedSolid}");
            }
            afterHash = after.Hash;
            return true;
        }

        private void CheckRebuilt(Vector3 origin)
        {
            if (!RayDry(origin, up, RayDistance, out var hit, out var node) || node.treeGuid != tree
                || !TryRead(hit, out _, out var snapshot) || snapshot.Hash != expectedHash)
                return;
            if (autoPhase == AutoPhase.RebuiltCut)
            {
                if (hit.distance <= originalDistance + 0.1f) return;
                if (verifyAnchor)
                {
                    var outcome = TerrainAnchor.Resolve(cutAnchor, out _, out _);
                    if (outcome != TerrainAnchorResult.SurfaceMissing)
                        throw new InvalidOperationException("Removed terrain contact returned " + outcome + ".");
                    Logger.LogInfo($"KEEL_TERRAIN_ANCHOR_FIXTURE request={autoId:N} resolved_before_cut=True invalidated_after_rebuild=True");
                }
                cutDistance = hit.distance;
                if (!TryApplyAutomated(origin, hit, node, snapshot, ProbeOperation.Fill, out var fillHash)) return;
                expectedHash = fillHash;
                autoPhase = AutoPhase.RebuiltFill;
                return;
            }
            if (hit.distance >= cutDistance - 0.1f) return;
            WriteEvidence(autoId, "auto-fill-rebuilt", node, hit.distance, snapshot);
            if (BeginSaveEvidence(node, snapshot.Hash)) return;
            FinishAutomation(true, "cut_and_fill_native_geometry_rebuilt");
        }
    }
}
