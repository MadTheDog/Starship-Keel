using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine;
using VoxelEngine;
using World.Terrain;

namespace Sevo.Example.TerrainEditProbe
{
    [BepInPlugin(Id, "Keel Terrain Edit Acceptance Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed partial class Probe : BaseUnityPlugin
    {
        private const string Id = "example.keel.terrainedit";
        private const string RequestFile = "terrain-edit-probe.request";
        private const float RayDistance = 20f;
        private const float BrushRadius = 3f;
        private const float PlaneStep = 0.75f;
        private readonly HashSet<Guid> consumed = new HashSet<Guid>();
        private string stateDirectory = "";
        private string session = "";

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_EDIT_PROBE") != "1") return;
            string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            string? activeSession = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            if (string.IsNullOrWhiteSpace(state) || !Path.IsPathFullyQualified(state)
                || !Guid.TryParseExact(activeSession, "N", out _)
                || !File.Exists(Path.Combine(state, "sevo-runtime-profile.json")))
            {
                Logger.LogWarning("KEEL_TERRAIN_EDIT unavailable: managed test session is required");
                return;
            }

            stateDirectory = state;
            session = activeSession!;
            ArmAutomatedFixture();
            ContentRegistration.RegisterFunctionalBlock(Id, "anchor", 1, "Probe Terrain Edit Anchor",
                () => TabHull.HULL_A, context => new Anchor(this, context));
        }

        private void Execute(BrickCoord anchor)
        {
            if (PlayerState.sessionMode != PlayerState.SessionMode.solo || !TerrainEditService.IsAvailable
                || !GameloopManager.IsMainThread || GameloopManager.IsNativeContainerAccessUnsafe
                || PlayerState.InLoadingScreen || PlayerState.IsQuittingToDesktopOrTitle || anchor.Parent == null)
                return;

            try
            {
                using var request = new FileStream(Path.Combine(stateDirectory, RequestFile), FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                if (request.Length <= 0 || request.Length > TerrainEvidence.MaximumRequestLength)
                    throw new InvalidOperationException("Request file size is invalid.");
                using var reader = new StreamReader(request);
                if (!TerrainEvidence.TryParseRequest(reader.ReadToEnd(), session, out var id, out var operation))
                    throw new InvalidOperationException("Request is invalid or belongs to another session.");
                if (!consumed.Add(id))
                    throw new InvalidOperationException("Request has already been consumed.");

                Vector3 origin = anchor.WorldPosition;
                Vector3 up = anchor.WorldUp;
                var ray = new VoxelRaycast();
                if (!ray.Raycast(origin, -up, out var hit, RayDistance, anchor.Parent)
                    || hit.type != VoxelRaycastHit.Type.newTerrain
                    || !TryRead(hit, out var node, out var before))
                {
                    Logger.LogWarning($"KEEL_TERRAIN_EDIT request={id:N} rejected=resident_planet_terrain_hit_required");
                    return;
                }

                if (operation == ProbeOperation.Snapshot)
                {
                    WriteEvidence(id, "snapshot", node, hit.distance, before);
                    return;
                }

                WriteEvidence(id, "before", node, hit.distance, before);
                bool fill = operation == ProbeOperation.Fill;
                var outcome = TerrainEditService.ApplyPlane(origin, -up, RayDistance, up, fill ? PlaneStep : -PlaneStep,
                    BrushRadius, fill ? TerrainEditOperation.FillBelow : TerrainEditOperation.CutAbove, anchor.Parent);
                if (!TryRead(hit, out var sameNode, out var after)
                    || sameNode.treeGuid != node.treeGuid || sameNode.densitiesId != node.densitiesId)
                {
                    Logger.LogWarning($"KEEL_TERRAIN_EDIT request={id:N} operation={operation} result={outcome} evidence=lost_node");
                    return;
                }

                // Use the original node address for the immediate write check. Recast later to test rebuilt geometry.
                WriteEvidence(id, "after", sameNode, hit.distance, after);
                var change = TerrainEvidence.Compare(before.Density, after.Density);
                bool accepted = outcome == TerrainEditResult.Queued && change.IsAccepted(operation);
                Logger.LogInfo($"KEEL_TERRAIN_EDIT request={id:N} operation={operation} result={outcome} accepted={accepted}"
                    + $" changed={change.Changed} removedSolid={change.RemovedSolid} addedSolid={change.AddedSolid}"
                    + $" increased={change.Increased} decreased={change.Decreased}");
            }
            catch (Exception exception)
            {
                Logger.LogWarning("KEEL_TERRAIN_EDIT probe failure: " + exception.Message);
            }
        }

        private static bool TryRead(VoxelRaycastHit hit, out OctreeNodeData node, out Snapshot snapshot)
        {
            node = default;
            snapshot = null!;
            var nodes = OctreeNodeResources.OctreeNodeDatas;
            if (!OctreeNodeResources.saveParsed || !nodes.IsCreated || hit.elementId <= 0 || hit.elementId >= nodes.Length)
                return false;

            node = nodes[hit.elementId];
            if (node.treeGuid == 0 || node.planetGenerationId == 0 || node.transformId != hit.transformId
                || !node.densityComputed || node.densitiesId <= 0
                || node.densitiesId >= OctreeNodeResources.voxelNodeDatas.Length / OctreeNodeData.DENSITY_COUNT)
                return false;

            using var samples = new NativeArray<VoxelNodeData>(OctreeNodeData.DENSITY_COUNT, Allocator.Temp);
            OctreeNodeResources.CopyDensityDataToArray(node.densitiesId, samples);
            snapshot = new Snapshot(samples);
            return true;
        }

        private void WriteEvidence(Guid id, string phase, OctreeNodeData node, float distance, Snapshot snapshot)
        {
            string directory = Path.Combine(stateDirectory, "terrain-probe");
            Directory.CreateDirectory(directory);
            string filename = $"{id:N}-{phase}-{node.treeGuid:x16}.bin";
            using (var stream = new FileStream(Path.Combine(directory, filename), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read))
                stream.Write(snapshot.Packed, 0, snapshot.Packed.Length);

            var nativeSave = new FileInfo(OctreeNodeResources.GetNodeFileName(node));
            string saved = nativeSave.Exists ? nativeSave.LastWriteTimeUtc.ToString("O") : "absent";
            string metrics = FormattableString.Invariant(
                $"tree={node.treeGuid:x16} voxel={node.VoxelSize:R} rayDistance={distance:R} solid={snapshot.Solid}");
            Logger.LogInfo($"KEEL_TERRAIN_EDIT request={id:N} phase={phase} {metrics} sha256={snapshot.Hash}"
                + $" file={filename} nativeSave={nativeSave.Name} saveWriteUtc={saved}");
        }

        private sealed class Anchor : FunctionalBlockBehavior
        {
            private readonly Probe probe;
            private readonly FunctionalBlockContext context;
            private bool pending;

            internal Anchor(Probe probe, FunctionalBlockContext context)
            {
                this.probe = probe;
                this.context = context;
            }

            public override void OnSpawn() => GameloopManager.OnTransformJobFinished += Run;
            public override void OnDespawn()
            {
                pending = false;
                GameloopManager.OnTransformJobFinished -= Run;
            }

            public override void OnSustainedInput(bool active) => pending = active;

            private void Run()
            {
                if (!pending) return;
                pending = false;
                if (context.TryGetPlacement(out var anchor)) probe.Execute(anchor);
            }
        }

        private sealed class Snapshot
        {
            internal short[] Density { get; }
            internal byte[] Packed { get; }
            internal string Hash { get; }
            internal int Solid { get; }

            internal Snapshot(NativeArray<VoxelNodeData> samples)
            {
                Density = new short[samples.Length];
                Packed = new byte[samples.Length * 8];
                using var writer = new BinaryWriter(new MemoryStream(Packed));
                int solid = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    var sample = samples[i];
                    Density[i] = sample.density;
                    if (sample.density < 0) solid++;
                    writer.Write(sample.color);
                    writer.Write(sample.density);
                    writer.Write(sample.texId0);
                    writer.Write(sample.texId1);
                }
                Solid = solid;
                using var sha = SHA256.Create();
                Hash = BitConverter.ToString(sha.ComputeHash(Packed)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
