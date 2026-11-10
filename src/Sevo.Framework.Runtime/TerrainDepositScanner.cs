using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Manager;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    /// <summary>Displays managed labels for live registered terrain materials after a stock scanner pulse.</summary>
    public static class TerrainDepositScanner
    {
        private const int NodeTableLimit = 65536;
        private const int NodesPerPulse = 48;
        private const int ResultLimit = 32;
        private const float GroupMetres = 20f; // Stock widget grouping starts at 20 world metres.
        private static readonly SortedDictionary<string, Style> Styles =
            new SortedDictionary<string, Style>(StringComparer.Ordinal);
        private static readonly List<Mark> Marks = new List<Mark>();
        private static Action<string>? warning;
        private static TerrainDepositLabels? overlay;
        private static Vector3l origin;
        private static ulong system;
        private static float pulse;
        private static bool pending;
        private static int threadId;
        private static bool warnedBudget;
        private static bool trace;
        private static int validatedFrame = -2;

        public static bool IsAvailable => overlay != null && warning != null;

        public static void Register(string ownerId, string localName, string materialKey, string label, Color32 color)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            ContentAllocator.ValidateKey(materialKey);
            if (string.IsNullOrWhiteSpace(label) || label.Length > 24 || label.Any(char.IsControl))
                throw new ArgumentException("Use a single scanner label of 1 to 24 characters.", nameof(label));
            lock (Styles)
            {
                if (Styles.ContainsKey(key) || Styles.Values.Any(style => style.MaterialKey == materialKey))
                    throw new InvalidOperationException("Duplicate terrain deposit scanner registration: " + key);
                Styles.Add(key, new Style(materialKey, label, color));
            }
        }

        internal static void Install(Harmony harmony, GameObject host, Action<string> log)
        {
            var type = typeof(TerrainDepositScanner);
            var press = AccessTools.DeclaredMethod(typeof(OreScannerResources), nameof(OreScannerResources.OnScannerKeyPressed), Type.EmptyTypes);
            harmony.Patch(press, prefix: new HarmonyMethod(type, nameof(BeforePulse)),
                postfix: new HarmonyMethod(type, nameof(AfterPulse)) { priority = Priority.Last });
            harmony.Patch(AccessTools.DeclaredMethod(typeof(GameloopManager), "OnAfterRendering", Type.EmptyTypes),
                postfix: new HarmonyMethod(type, nameof(AfterFrame)) { priority = Priority.Last });
            harmony.Patch(AccessTools.DeclaredMethod(typeof(OreScannerResources), nameof(OreScannerResources.OnGameReload), Type.EmptyTypes),
                postfix: new HarmonyMethod(type, nameof(Clear)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(OctreeNodeJobs), "OnComputeGeoJobFinished",
                    new[] { typeof(OctreeNodeJobs.Job_GeoCompute) }),
                prefix: new HarmonyMethod(type, nameof(FilterRegisteredStockAliases)));
            overlay = host.AddComponent<TerrainDepositLabels>();
            threadId = Environment.CurrentManagedThreadId;
            warning = log;
            trace = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_DEPOSIT_SCANNER_TRACE") == "1";
            PlanetarySystem.OnNewSystem += Clear;
        }

        internal static void Disable()
        {
            PlanetarySystem.OnNewSystem -= Clear;
            if (overlay != null) UnityEngine.Object.Destroy(overlay);
            overlay = null;
            warning = null;
            Clear();
        }

        private static void BeforePulse(out float __state) => __state = OreScannerResources.lastActivateTime;

        private static void AfterPulse(float __state)
        {
            if (!IsAvailable || Environment.CurrentManagedThreadId != threadId || OreScannerResources.lastActivateTime == __state)
                return;
            Clear();
            lock (Styles) if (Styles.Count == 0) return;
            if (!WorldReady() || Dependency.PlayerCameraComponent == null) return;
            origin = PositionManager.ToSystemPosition(Dependency.PlayerCameraComponent.transform.position);
            system = PlanetarySystem.CurrentStarData.seed;
            pulse = OreScannerResources.lastActivateTime;
            pending = true;
        }

        private static bool WorldReady() => PlayerState.sessionMode == PlayerState.SessionMode.solo
            && !PlayerState.InLoadingScreen && !PlayerState.IsQuittingToDesktopOrTitle
            && !PlanetarySystem.InSystemTransition && PlanetarySystem.CurrentStarData.IsValid;

        private static bool ActivePulse() => IsAvailable && WorldReady() && PlanetarySystem.CurrentStarData.seed == system
            && OreScannerResources.lastActivateTime == pulse && OreScannerResources.IsScannerRunning;

        private static void AfterFrame()
        {
            if (Environment.CurrentManagedThreadId != threadId || (!pending && Marks.Count == 0)) return;
            if (!ActivePulse())
            {
                Clear();
                return;
            }
            if (GameloopManager.IsNativeContainerAccessUnsafe) return;
            try
            {
                if (pending)
                {
                    if (!OctreeNodeResources.saveParsed || !OctreeNodeResources.OctreeNodeDatas.IsCreated
                        || !OctreeNodeResources.voxelNodeDatas.IsCreated || !SkyTransformData.datas.IsCreated) return;
                    pending = false;
                    Scan();
                }
                for (int i = Marks.Count - 1; i >= 0; i--)
                {
                    var mark = Marks[i];
                    if (!TryRefresh(mark)) Marks.RemoveAt(i);
                }
                validatedFrame = Time.frameCount;
            }
            catch (Exception exception)
            {
                var log = warning;
                Disable();
                log?.Invoke("Terrain deposit scanner disabled after a native read failed: " + exception.Message);
            }
        }

        internal static bool HasDeclaredHighlight(int materialId)
        {
            if (!IsAvailable || !TerrainMaterialRegistration.TryGetKey(materialId, out var key)) return false;
            lock (Styles) return Styles.Values.Any(style => style.MaterialKey == key);
        }

        // Completed stock geometry jobs publish records with the exact originating voxel coordinate.
        // Remove only aliases for styled, admitted rows before they reach the native scanner.
        private static void FilterRegisteredStockAliases(OctreeNodeJobs.Job_GeoCompute __0)
        {
            if (TerrainDepositNativeHighlights.IsAvailable || !IsAvailable
                || Environment.CurrentManagedThreadId != threadId || !WorldReady()) return;
            try
            {
                lock (Styles) if (Styles.Count == 0) return;
                var records = __0.oreScannerDatas;
                var voxels = __0.voxelDatas;
                if (!records.IsCreated || records.Length == 0 || records.Length > OctreeNodeData.DENSITY_COUNT
                    || !voxels.IsCreated || voxels.Length != OctreeNodeData.DENSITY_COUNT)
                    return;

                List<int>? remove = null;
                for (int i = 0; i < records.Length; i++)
                {
                    var record = records[i];
                    var coord = record.coordinate;
                    if (record.transformId != __0.node.transformId || (uint)coord.x >= OctreeNodeData.DENSITY_WIDTH
                        || (uint)coord.y >= OctreeNodeData.DENSITY_WIDTH || (uint)coord.z >= OctreeNodeData.DENSITY_WIDTH)
                        continue;
                    var voxel = voxels[VoxelNodeData.ToDensityId(coord.x, coord.y, coord.z)];
                    int material = voxel.MainTexId;
                    if (voxel.density >= 0 || !TerrainMaterialRegistration.TryGetKey(material, out var key))
                        continue;
                    var table = WorldTerrainResources.TerrainMaterialDatas;
                    if ((uint)material >= table.Length
                        || MaterialGradientGenerator.GetMetalTextTexId(table[material].gradientId) != record.texId)
                        continue;
                    lock (Styles)
                    {
                        if (!Styles.Values.Any(style => style.MaterialKey == key)) continue;
                    }
                    (remove ??= new List<int>()).Add(i);
                }

                if (remove != null)
                    for (int i = remove.Count - 1; i >= 0; i--)
                        records.RemoveAt(remove[i]);
            }
            catch (Exception exception)
            {
                var log = warning;
                Disable();
                log?.Invoke("Terrain deposit scanner stopped after native alias inspection failed: " + exception.Message);
            }
        }
        private static void Scan()
        {
            var byMaterial = new Dictionary<byte, Style>();
            lock (Styles)
            {
                foreach (var style in Styles.Values)
                    if (TerrainMaterialRegistration.TryGetId(style.MaterialKey, out byte material))
                        byMaterial.TryAdd(material, style);
            }
            if (byMaterial.Count == 0) return;

            var table = OctreeNodeResources.OctreeNodeDatas;
            var candidates = new List<(int Id, float Score, float Spacing)>(NodesPerPulse);
            int limit = Math.Min(table.Length, NodeTableLimit + 1);
            for (int id = 1; id < limit; id++)
            {
                var node = table[id];
                if (!TryReadNode(node, out var transform)) continue;
                var bounds = node.DensityBoundsTight;
                bounds.center -= (Vector3)node.position;
                float distance = Mathf.Sqrt(bounds.SqrDistance(transform.SystemToLocal(origin))) * transform.scale;
                if (!(distance <= OreScannerResources.PLAYER_SCANNER_RANGE)) continue;
                float spacing = node.VoxelSize;
                int at = candidates.FindIndex(candidate => distance < candidate.Score
                    || (distance == candidate.Score && spacing < candidate.Spacing));
                if (at < 0) at = candidates.Count;
                if (at == NodesPerPulse) continue;
                if (candidates.Count == NodesPerPulse) candidates.RemoveAt(NodesPerPulse - 1);
                candidates.Insert(at, (id, distance, spacing));
            }
            if (table.Length > limit && !warnedBudget)
            {
                warnedBudget = true;
                warning?.Invoke($"Terrain deposit scanner sampled the first {NodeTableLimit} resident node slots.");
            }

            var groups = new List<(int Body, bool Asteroid, byte Material, Vector3 Anchor, Mark Mark)>(ResultLimit);
            var eligible = trace ? new Dictionary<byte, (int Solid, int HighlightEligible)>() : null;
            var materials = WorldTerrainResources.TerrainMaterialDatas;
            int farthest = 0;
            float maximumScore = float.PositiveInfinity;
            foreach (var candidate in candidates)
            {
                var node = table[candidate.Id];
                if (!TryReadNode(node, out var transform)) continue;
                var localOrigin = transform.SystemToLocal(origin);
                float localRange = OreScannerResources.PLAYER_SCANNER_RANGE / transform.scale;
                float squaredRange = localRange * localRange;
                int start = node.densitiesId * OctreeNodeData.DENSITY_COUNT;
                const int padding = OctreeNodeData.DENSITY_HW - OctreeNodeData.VOXEL_HW;
                const int end = padding + OctreeNodeData.VOXEL_WIDTH;
                for (int z = padding; z < end; z++)
                for (int y = padding; y < end; y++)
                for (int x = padding; x < end; x++)
                {
                    int index = VoxelNodeData.ToDensityId(x, y, z);
                    var voxel = OctreeNodeResources.voxelNodeDatas[start + index];
                    if (voxel.density >= 0) continue;
                    byte material = (byte)voxel.MainTexId;
                    if (!byMaterial.TryGetValue(material, out var style)) continue;
                    Vector3 position = node.GetPosition(x, y, z);
                    float distance = (position - localOrigin).sqrMagnitude;
                    if (!(distance <= squaredRange)) continue;
                    Vector3 bodyPosition = (Vector3)node.position + position;
                    bool asteroid = node.planetGenerationId == 0;
                    int body = asteroid ? node.asteroidId : node.planetGenerationId;
                    if (eligible != null)
                    {
                        eligible.TryGetValue(material, out var counts);
                        counts.Solid++;
                        if (node.VoxelSize == 4f && voxel.Density > -0.5f && voxel.Density < -0.1f
                            && materials.IsCreated && material < materials.Length && materials[material].IsOre)
                            counts.HighlightEligible++;
                        eligible[material] = counts;
                    }

                    float score = distance * transform.scale * transform.scale;
                    if (score >= maximumScore) continue;
                    int match = -1;
                    float matchDistance = GroupMetres * GroupMetres;
                    for (int i = 0; i < groups.Count; i++)
                    {
                        var group = groups[i];
                        if (group.Body != body || group.Asteroid != asteroid || group.Material != material) continue;
                        float separation = (group.Anchor - bodyPosition).sqrMagnitude * transform.scale * transform.scale;
                        if (separation >= matchDistance) continue;
                        match = i;
                        matchDistance = separation;
                    }

                    if (match >= 0)
                    {
                        var group = groups[match];
                        if (group.Mark.Score <= score) continue;
                        group.Mark = new Mark(candidate.Id, node, index, position, material, style, score);
                        groups[match] = group;
                    }
                    else
                    {
                        var group = (body, asteroid, material, bodyPosition,
                            new Mark(candidate.Id, node, index, position, material, style, score));
                        if (groups.Count < ResultLimit) groups.Add(group);
                        else groups[farthest] = group;
                    }

                    if (groups.Count != ResultLimit) continue;
                    maximumScore = -1f;
                    for (int i = 0; i < groups.Count; i++)
                    {
                        if (groups[i].Mark.Score <= maximumScore) continue;
                        farthest = i;
                        maximumScore = groups[i].Mark.Score;
                    }
                }
            }
            Marks.AddRange(groups.Select(group => group.Mark).OrderBy(mark => mark.Score));
            if (eligible != null)
            {
                string samples = string.Join(",", eligible.Select(pair =>
                    $"{byMaterial[pair.Key].MaterialKey}:{pair.Value.Solid}/{pair.Value.HighlightEligible}"));
                warning?.Invoke($"KEEL_DEPOSIT_SCAN pulse={pulse:F2} nodes={candidates.Count} marks={Marks.Count} labels={string.Join(",", Marks.Select(mark => mark.Style.Content.text).Distinct())} solid/stockHighlightEligible={samples}");
                TerrainDepositNativeHighlights.TraceNearby(origin, warning!);
            }
        }

        private static bool TryReadNode(OctreeNodeData node, out SkyTransformData transform)
        {
            transform = default;
            if (node.treeGuid == 0 || !node.densityComputed || node.isJobStarted || !node.IsLeaf
                || node.geoState != NodeGeoState.hasGeo || node.fadeState == NodeFadeState.fadeOut
                || (node.planetGenerationId == 0 && node.asteroidId <= 0)
                || node.densitiesId <= 0 || node.densitiesId >= OctreeNodeResources.voxelNodeDatas.Length / OctreeNodeData.DENSITY_COUNT
                || node.transformId <= 0 || node.transformId >= SkyTransformData.datas.Length
                || !float.IsFinite(node.VoxelSize) || node.VoxelSize <= 0f)
                return false;
            transform = SkyTransformData.datas[node.transformId];
            return float.IsFinite(transform.scale) && transform.scale > 0f;
        }

        private static bool TryRefresh(Mark mark)
        {
            var table = OctreeNodeResources.OctreeNodeDatas;
            if (!table.IsCreated || !OctreeNodeResources.voxelNodeDatas.IsCreated || !SkyTransformData.datas.IsCreated
                || mark.NodeId >= table.Length) return false;
            var node = table[mark.NodeId];
            if (node.treeGuid != mark.Guid || node.densitiesId != mark.DensityId || node.transformId != mark.TransformId
                || !TryReadNode(node, out var transform)
                || !TerrainMaterialRegistration.TryGetId(mark.Style.MaterialKey, out byte id) || id != mark.Material)
                return false;
            var voxel = OctreeNodeResources.voxelNodeDatas[node.densitiesId * OctreeNodeData.DENSITY_COUNT + mark.Index];
            if (voxel.density >= 0 || voxel.MainTexId != id) return false;
            mark.Position = transform.LocalToSystem(mark.LocalPosition);
            return true;
        }

        private static void Clear()
        {
            pending = false;
            Marks.Clear();
            validatedFrame = -2;
        }

        internal static void Draw(TerrainDepositBadge badge)
        {
            if (Event.current.type != EventType.Repaint || Marks.Count == 0 || !ActivePulse()
                || Time.frameCount - validatedFrame > 1 || PlayerState.InMenu || Dependency.PlayerCameraComponent == null) return;

            var camera = Dependency.PlayerCameraComponent;
            var original = GUI.color;
            try
            {
                badge.Begin(OreScannerResources.WidgetFadeAlpha);
                foreach (var mark in Marks)
                {
                    if (badge.AtCapacity) break;
                    var point = camera.WorldToScreenPoint(PositionManager.ToWorldPosition(mark.Position));
                    if (point.z <= 0 || point.x < 0 || point.x > Screen.width || point.y < 0 || point.y > Screen.height) continue;
                    badge.Draw(point, mark.Style.Content, mark.Style.Color);
                }
            }
            catch (Exception exception)
            {
                var log = warning;
                Disable();
                log?.Invoke("Terrain deposit scanner labels disabled: " + exception.Message);
            }
            finally { GUI.color = original; }
        }

        private sealed class Style
        {
            internal readonly string MaterialKey;
            internal readonly GUIContent Content;
            internal readonly Color32 Color;

            internal Style(string materialKey, string label, Color32 color)
            {
                MaterialKey = materialKey;
                Content = new GUIContent(label);
                Color = color;
            }
        }

        private sealed class Mark
        {
            internal readonly int NodeId, DensityId, TransformId, Index;
            internal readonly ulong Guid;
            internal readonly byte Material;
            internal readonly Vector3 LocalPosition;
            internal readonly Style Style;
            internal readonly float Score;
            internal Vector3l Position;

            internal Mark(int id, OctreeNodeData node, int index, Vector3 localPosition, byte material, Style style, float score)
            {
                NodeId = id;
                DensityId = node.densitiesId;
                TransformId = node.transformId;
                Guid = node.treeGuid;
                Index = index;
                LocalPosition = localPosition;
                Material = material;
                Style = style;
                Score = score;
            }
        }
    }

    internal sealed class TerrainDepositLabels : MonoBehaviour
    {
        private readonly TerrainDepositBadge badge = new TerrainDepositBadge();
        private void OnGUI() => TerrainDepositScanner.Draw(badge);
    }
}
