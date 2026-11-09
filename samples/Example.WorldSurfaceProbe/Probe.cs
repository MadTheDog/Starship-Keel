using System;
using BepInEx;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using World;
using World.Terrain;
using World.Terrain.Planet;

namespace Sevo.Example.WorldSurfaceProbe
{
    [BepInPlugin("example.keel.worldsurface", "Keel World Surface Probe", "0.1.0")]
    [BepInDependency(Plugin.Id)]
    public sealed class Probe : BaseUnityPlugin
    {
        private const float Range = 160f;
        private bool pending;
        private bool automated;
        private bool requireResidentWater;
        private bool terrain;
        private bool nominalWater;
        private bool residentWater;
        private bool occlusion;
        private float nextProbeTime;
        private float deadline;
        private int scan;
        private string session = "";
        private VoxelRaycast? nativeWet;
        private VoxelRaycast? nativeDry;

        private void Awake() => pending = Environment.GetEnvironmentVariable("SEVO_KEEL_WORLD_SURFACE_PROBE") == "1";

        private void Update()
        {
            if (!pending) return;
            pending = false;
            try
            {
                var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
                if (state == null || !Guid.TryParseExact(session, "N", out _)
                    || Application.companyName != "SEVO Keel Conn Lab 010" || Application.productName != "SEVO CT Test")
                    throw new InvalidOperationException("A managed disposable connector lab is required.");
                ProfileProtocol.Open(state, Paths.GameRootPath, session);
                automated = Environment.GetEnvironmentVariable("SEVO_KEEL_WORLD_SURFACE_PROBE_AUTOMATED") == "1";
                requireResidentWater = Environment.GetEnvironmentVariable("SEVO_KEEL_WORLD_SURFACE_PROBE_REQUIRE_RESIDENT_WATER") == "1";
                nativeWet = new VoxelRaycast { includeWater = true };
                nativeDry = new VoxelRaycast();
                deadline = Time.realtimeSinceStartup + 180f;
                GameloopManager.OnTransformJobFinished += Sample;
                Logger.LogInfo($"KEEL_SURFACE_PROBE armed session={session} automated={automated}");
            }
            catch (Exception exception) { Finish(false, exception.Message); }
        }

        private void OnDestroy() => GameloopManager.OnTransformJobFinished -= Sample;

        private void Sample()
        {
            float now = Time.realtimeSinceStartup;
            if (automated && now > deadline)
            {
                Finish(false, "bounded scan did not obtain all required evidence");
                return;
            }
            if (now < nextProbeTime || !WorldSurfaceQuery.IsAvailable || !GameloopManager.IsMainThread
                || GameloopManager.IsNativeContainerAccessUnsafe || PlayerState.InLoadingScreen
                || PlayerState.IsQuittingToDesktopOrTitle || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || !NetworkServer.active || !NetworkServer.dontListen || Dependency.PlayerManager == null
                || !Dependency.PlayerManager.IsLocal)
                return;

            nextProbeTime = now + (automated ? 0.25f : 1f);
            try
            {
                if (!automated)
                {
                    var camera = Camera.main;
                    if (camera == null) return;
                    var pose = camera.transform;
                    Compare(pose.position, pose.forward.normalized, null, "view");
                    return;
                }

                var player = Dependency.PlayerManager;
                if (!player.SkyTransform.CurrentSystem.Equals(GameIntroductionResources.STARTING_SYSTEM)) return;
                var bodies = PlanetarySystem.SolarBodies;
                int body = GameIntroductionResources.STARTING_PLANETID;
                if (bodies == null || body < 0 || body >= bodies.Count) return;
                int generation = bodies[body].planetGenerationId;
                if (generation <= 0 || generation >= PlanetResources.Datas.Length) return;
                var planet = PlanetResources.Datas[generation];
                if (planet.transformId <= 0 || planet.transformId >= SkyTransformData.datas.Length
                    || !float.IsFinite(planet.radius) || planet.radius <= 0f) return;
                var transform = SkyTransformData.datas[planet.transformId];
                Vector3 radial = (Vector3)(player.SkyTransform.SystemPosition - transform.systemPosition).ToFloat3();
                if (radial.magnitude > planet.radius * 1.2f || radial.sqrMagnitude < 1f) return;
                radial.Normalize();
                Vector3 playerPosition = PositionManager.ToWorldPosition(player.SkyTransform.SystemPosition);
                Vector3 east = Vector3.Cross(radial, Vector3.up);
                if (east.sqrMagnitude < 0.01f) east = Vector3.Cross(radial, Vector3.right);
                east.Normalize();
                Vector3 north = Vector3.Cross(radial, east);
                int index = scan++;
                int local = index % 25;
                Vector3 lateral = (local % 5 - 2) * 6f * east + (local / 5 - 2) * 6f * north;
                float? ocean = planet.oceanRadius > 0f && float.IsFinite(planet.oceanRadius) ? planet.oceanRadius : null;
                Vector3 near = playerPosition + lateral + 24f * radial;
                Compare(near, -radial, SphereDistance(near, -radial, transform.WorldPosition, ocean), "near-player");

                if (ocean.HasValue && (!nominalWater || requireResidentWater && !residentWater))
                {
                    // Independent bounded nominal-sphere queries need no target relocation or terrain generation.
                    float angle = index * 2.39996323f;
                    float spread = requireResidentWater ? 0.0005f : 0.045f;
                    Vector3 rayUp = (radial + spread * (1 + index % 16)
                        * (Mathf.Cos(angle) * east + Mathf.Sin(angle) * north)).normalized;
                    Vector3 origin = PositionManager.ToWorldPosition(transform.systemPosition + new Vector3l((ocean.Value + 24f) * rayUp));
                    Compare(origin, -rayUp, 24f, "ocean-sphere");
                }

                if (terrain && nominalWater && (!requireResidentWater || residentWater && occlusion))
                    Finish(true, "native_surface_queries_match");
            }
            catch (Exception exception) { Finish(false, exception.Message); }
        }

        private void Compare(Vector3 origin, Vector3 direction, float? expectedOcean, string site)
        {
            var dry = Read(origin, direction, false);
            var wet = Read(origin, direction, true);
            if (!automated)
                Logger.LogInfo(FormattableString.Invariant($"KEEL_SURFACE_PROBE view wet={wet?.Kind} dry={dry?.Kind} distance={wet?.Distance:R} normal={wet?.HasNormal} session={session}"));

            if (!terrain && dry is { Kind: WorldSurfaceKind.Terrain, HasNormal: true })
            {
                terrain = true;
                Logger.LogInfo($"KEEL_SURFACE_PROBE TERRAIN PASS site={site} session={session}");
            }
            if (wet is { Kind: WorldSurfaceKind.Water } water)
            {
                if (water.HasNormal || water.Normal != Vector3.zero || dry is { Kind: WorldSurfaceKind.Water })
                    throw new InvalidOperationException("Water classification or normal semantics differed.");
                if (expectedOcean.HasValue && Mathf.Abs(water.Distance - expectedOcean.Value) < 0.15f && !nominalWater)
                {
                    nominalWater = true;
                    Logger.LogInfo(FormattableString.Invariant($"KEEL_SURFACE_PROBE WATER_NOMINAL PASS distance={water.Distance:R} session={session}"));
                }
                if (dry is { Kind: WorldSurfaceKind.Terrain } submerged && submerged.Distance > water.Distance && !residentWater)
                {
                    residentWater = true;
                    Logger.LogInfo($"KEEL_SURFACE_PROBE WATER_LOADED PASS site={site} session={session}");
                }
            }
            if (!occlusion && expectedOcean is > 0f and < Range && dry is { Kind: WorldSurfaceKind.Terrain } blocker
                && blocker.Distance + 0.15f < expectedOcean && wet is { Kind: WorldSurfaceKind.Terrain } retained
                && Mathf.Abs(retained.Distance - blocker.Distance) < 0.01f)
            {
                occlusion = true;
                Logger.LogInfo($"KEEL_SURFACE_PROBE OCCLUSION PASS site={site} session={session}");
            }
        }

        private WorldSurfaceHit? Read(Vector3 origin, Vector3 direction, bool includeWater)
        {
            var nativeRay = includeWater ? nativeWet! : nativeDry!;
            bool nativeFound = nativeRay.Raycast(origin, direction, out var native, Range) && native.distance <= Range;
            var result = WorldSurfaceQuery.Raycast(origin, 2f * direction, Range, includeWater, out var hit);
            if (!nativeFound)
            {
                if (result != WorldSurfaceQueryResult.Miss) throw new InvalidOperationException("Native miss disagreed with Keel.");
                return null;
            }

            var expected = native.type switch
            {
                VoxelRaycastHit.Type.voxel => WorldSurfaceKind.Grid,
                VoxelRaycastHit.Type.terrain or VoxelRaycastHit.Type.newTerrain => WorldSurfaceKind.Terrain,
                VoxelRaycastHit.Type.water => WorldSurfaceKind.Water,
                _ => WorldSurfaceKind.Other
            };
            if (result != WorldSurfaceQueryResult.Hit || hit.Kind != expected || Mathf.Abs(hit.Distance - native.distance) > 0.02f
                || Vector3.Distance(hit.Point, origin + direction * native.distance) > 0.02f)
                throw new InvalidOperationException("Keel and native type, distance, or ray point differed.");
            if (expected is WorldSurfaceKind.Grid or WorldSurfaceKind.Terrain)
            {
                var normal = native.WorldNormal;
                bool nativeNormal = float.IsFinite(normal.x) && float.IsFinite(normal.y) && float.IsFinite(normal.z)
                    && normal.sqrMagnitude > 0.000001f;
                if (hit.HasNormal != nativeNormal || nativeNormal && Vector3.Distance(hit.Normal, normal) > 0.001f)
                    throw new InvalidOperationException("Keel and native surface normals differed.");
            }
            return hit;
        }

        private static float? SphereDistance(Vector3 origin, Vector3 direction, Vector3 centre, float? radius)
        {
            if (!radius.HasValue) return null;
            Vector3 offset = origin - centre;
            double b = Vector3.Dot(offset, direction);
            double c = (double)offset.x * offset.x + (double)offset.y * offset.y + (double)offset.z * offset.z
                - (double)radius.Value * radius.Value;
            double discriminant = b * b - c;
            if (c <= 0 || discriminant < 0) return null;
            return (float)(-b - Math.Sqrt(discriminant));
        }

        private void Finish(bool accepted, string detail)
        {
            GameloopManager.OnTransformJobFinished -= Sample;
            Logger.LogInfo($"KEEL_SURFACE_PROBE accepted={accepted} terrain={terrain} nominalWater={nominalWater}"
                + $" residentWater={residentWater} occlusion={occlusion} session={session} detail={detail}");
        }
    }
}
