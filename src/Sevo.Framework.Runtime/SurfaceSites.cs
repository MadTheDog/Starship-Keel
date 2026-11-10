using System;
using System.Collections.Generic;
using System.IO;
using Game;
using Manager;
using Manager.Save;
using Sevo.Framework.Sdk;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Framework.Runtime
{
    public enum SurfaceSiteResult { Unavailable, InvalidSite, TerrainUnsuitable, AlreadyExists, SpawnFailed, Spawned }

    /// <summary>Places a persistent native terrain building at a validated loaded planet site.</summary>
    public static class SurfaceSites
    {
        private static readonly HashSet<(string Character, ulong WorldSeed, string Site)> Attempted =
            new HashSet<(string, ulong, string)>();
        private static Action<string>? warning;
        private static bool enabled;

        public static bool IsAvailable => enabled && TerrainAnchor.IsAvailable;
        internal static void Enable(Action<string> reportWarning) { warning = reportWarning; enabled = true; }
        internal static void Disable() { enabled = false; Attempted.Clear(); }

        public static SurfaceSiteResult TrySpawn(string ownerId, string localName, TerrainAnchorPose site,
            MetaDataPath blueprint, string displayName, float yawDegrees, out ParentEntity? entity)
        {
            entity = null;
            if (!IsAvailable || !WorldSurfaceQuery.IsSafePhase || SaveManager.playerSave == null
                || Dependency.PlayerNetwork == null || BlockEntityManager.instance == null
                || PlanetarySystem.InSystemTransition)
                return SurfaceSiteResult.Unavailable;
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            var bounds = blueprint.metaData.Bounds;
            string character = SaveManager.CharacterName;
            if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(blueprint.directory)
                || string.IsNullOrEmpty(blueprint.fileName) || !File.Exists(blueprint.blueprintFullName)
                || string.IsNullOrWhiteSpace(displayName) || !float.IsFinite(yawDegrees)
                || !(bounds.size.sqrMagnitude > 0 && bounds.size.sqrMagnitude < 10000))
                return SurfaceSiteResult.InvalidSite;
            var body = LoadedRockyBody.Find(site.Body.StarSeed, site.Body.BodySeed);
            if (body == null) return SurfaceSiteResult.Unavailable;
            string id = OrbitalPoints.MakeId("keel-surface-", site.Body.StarSeed, key);
            var attempt = (character, SaveManager.playerSave.seed, id);
            entity = BlockEntityManager.instance.GetEntityBySGUID(id);
            if (entity != null || Attempted.Contains(attempt)
                || File.Exists(EntityInstanceSystem_mp.GetPlayerInstanceFullPath(character, id)))
                return SurfaceSiteResult.AlreadyExists;

            if (TerrainAnchor.Resolve(site, out var contact, out var normal) != TerrainAnchorResult.Resolved
                || Vector3.Dot(site.LocalNormal, site.LocalPosition.normalized) < 0.95f)
                return SurfaceSiteResult.TerrainUnsuitable;
            var rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0, yawDegrees, 0);
            var floor = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            var position = contact - rotation * floor + normal * 0.25f;
            for (int x = -1; x <= 1; x += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                var corner = contact + rotation * new Vector3(x * bounds.extents.x, 0, z * bounds.extents.z);
                var ray = WorldSurfaceQuery.Raycast(corner + normal * 2f, -normal, 4f, true, out var hit);
                if (ray != WorldSurfaceQueryResult.Hit || hit.Kind != WorldSurfaceKind.Terrain || !hit.HasNormal
                    || Mathf.Abs(hit.Distance - 2f) > 0.5f || Vector3.Dot(hit.Normal, normal) < 0.9f)
                    return SurfaceSiteResult.TerrainUnsuitable;
            }

            Attempted.Add(attempt);
            try
            {
                var data = new EntityInstanceData
                {
                    sguid = id,
                    seed = site.Body.BodySeed,
                    solarBodyId = body.planetGenerationId,
                    entityName = displayName,
                    blueprintFullPath = blueprint.blueprintFullName,
                    ownerPlayerNetID = Dependency.PlayerNetwork.netId,
                    entityType = EntityType.terrainBuilding,
                    buildPermission = BuildPermission.cannotBeBuilt,
                    isNewInstance = true,
                    saveInWorld = true
                };
                entity = Dependency.PlayerNetwork.ServerLoadBlueprint(data,
                    new WorldTransform(PositionManager.ToSystemPosition(position), rotation), PlanetarySystem.CurrentStarData);
                if (entity == null)
                {
                    Attempted.Remove(attempt);
                    return SurfaceSiteResult.SpawnFailed;
                }
                var spawned = entity;
                spawned.RegisterOnInitializedCallback(() =>
                {
                    if (spawned == null || spawned.WillBeUnspawned) return;
                    spawned.Rigidbody.IsForcedStaticSelf.Value = true;
                    spawned.SetDirty();
                });
                return SurfaceSiteResult.Spawned;
            }
            catch (Exception exception)
            {
                warning?.Invoke($"Surface site {key} did not finish spawning: {exception.Message}");
                return SurfaceSiteResult.SpawnFailed;
            }
        }
    }
}
