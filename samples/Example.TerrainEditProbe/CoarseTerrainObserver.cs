#nullable enable
using System;
using System.Reflection;
using HarmonyLib;
using Manager;
using UnityEngine;
using World.Terrain;
using World.Terrain.Planet;

namespace Sevo.Example.TerrainEditProbe
{
    public sealed partial class Probe
    {
        private static readonly MethodInfo? CoarseCallback = AccessTools.Method(typeof(GameIntroductionResources),
            nameof(GameIntroductionResources.OnNodeComputeFinished),
            new[] { typeof(OctreeNodeData), typeof(OctreeNodeJobs.Job_GeoCompute) });
        private static readonly MethodInfo? CoarseObserver = AccessTools.Method(typeof(Probe), nameof(ObserveCompletedCoarseMesh));
        private static Probe? coarseOwner;
        private Harmony? coarseHarmony;
        private Vector3l? coarseApproach;

        // Call after Unity Update and only after the disposable fixture has passed its session gates.
        private bool InstallCoarseApproachObserver()
        {
            if (coarseHarmony != null) return true;
            if (coarseOwner != null || CoarseCallback == null || !CoarseCallback.IsStatic
                || CoarseCallback.ReturnType != typeof(void) || CoarseObserver == null)
                return false;

            try
            {
                var harmony = new Harmony(Id + ".coarse");
                harmony.Patch(CoarseCallback, postfix: new HarmonyMethod(CoarseObserver));
                coarseHarmony = harmony;
                coarseOwner = this;
                return true;
            }
            catch (Exception exception)
            {
                Logger.LogWarning("KEEL_TERRAIN_FIXTURE coarse_observer_install_failed=" + exception.Message);
                return false;
            }
        }

        private void UninstallCoarseApproachObserver()
        {
            if (coarseOwner == this) coarseOwner = null;
            coarseApproach = null;
            var harmony = coarseHarmony;
            coarseHarmony = null;
            if (harmony == null || CoarseCallback == null || CoarseObserver == null) return;

            try { harmony.Unpatch(CoarseCallback, CoarseObserver); }
            catch (Exception exception)
            {
                Logger.LogWarning("KEEL_TERRAIN_FIXTURE coarse_observer_uninstall_failed=" + exception.Message);
            }
        }

        private bool TryTakeCoarseApproach(out Vector3l candidate)
        {
            candidate = coarseApproach.GetValueOrDefault();
            bool found = coarseApproach.HasValue;
            coarseApproach = null;
            return found;
        }

        private static void ObserveCompletedCoarseMesh(OctreeNodeData __0, OctreeNodeJobs.Job_GeoCompute __1)
        {
            var owner = coarseOwner;
            if (owner == null || !owner.autoArmed || owner.planetId <= 0 || owner.coarseApproach.HasValue
                || !GameloopManager.IsMainThread || __0.planetGenerationId != owner.planetId
                || __0.VoxelSize != 64f || __0.geoState != NodeGeoState.hasGeo || __0.HeightDelta > 250f)
                return;

            try { owner.FindDryCoarseVertex(__0, __1); }
            catch (Exception exception)
            {
                // Stock terrain processing must continue if this diagnostic observer fails.
                coarseOwner = null;
                owner.Logger.LogWarning("KEEL_TERRAIN_FIXTURE coarse_observer_read_failed=" + exception.Message);
            }
        }

        private void FindDryCoarseVertex(OctreeNodeData node, OctreeNodeJobs.Job_GeoCompute job)
        {
            var triangles = job.trianglesElements;
            if (!triangles.IsCreated || triangles.Length <= 1
                || node.planetGenerationId >= PlanetResources.Datas.Length
                || node.transformId <= 0 || node.transformId >= SkyTransformData.datas.Length)
                return;

            var planet = PlanetResources.Datas[node.planetGenerationId];
            if (planet.transformId <= 0 || planet.transformId >= SkyTransformData.datas.Length
                || !float.IsFinite(planet.oceanRadius) || planet.oceanRadius <= 0f)
                return;

            var nodeTransform = SkyTransformData.datas[node.transformId];
            var planetTransform = SkyTransformData.datas[planet.transformId];
            Vector3 towardStar = (-(Vector3)planetTransform.systemPosition.ToFloat3()).normalized;
            const float minSlopeDot = 0.9659258f; // cos(15 degrees)
            const int maxTrianglesPerMesh = 128;
            int count = Mathf.Min(triangles.Length - 1, maxTrianglesPerMesh);
            for (int sample = 0; sample < count; sample++)
            {
                int index = 1 + (int)((long)sample * (triangles.Length - 1) / count);
                var triangle = triangles[index];
                Vector3 vertex = triangle.GetVertex(0);
                Vector3 planetLocal = (Vector3)node.position + vertex;
                float height = planetLocal.magnitude - planet.oceanRadius;
                if (!(height >= 50f && height <= 400f)) continue;

                Vector3 radial = planetLocal.normalized;
                if (!(Vector3.Dot(radial, towardStar) >= 0.6f)
                    || !(Vector3.Dot(-radial, triangle.GetNormal(0)) >= minSlopeDot))
                    continue;

                // Keep the actual native vertex and stay above the coarse mesh while the finer LOD loads.
                coarseApproach = nodeTransform.LocalToSystem(vertex + 30f * radial);
                Logger.LogInfo(FormattableString.Invariant(
                    $"KEEL_TERRAIN_FIXTURE request={autoId:N} coarse_candidate planet={planetId} height={height:R}"));
                return;
            }
        }
    }
}
