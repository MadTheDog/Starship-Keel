using System;
using System.Collections.Generic;
using System.Linq;
using Common;
using HarmonyLib;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine;
using World;

namespace Sevo.Framework.Runtime
{
    /// <summary>Registers a persistent terrain identity backed by an existing native material.</summary>
    public static class TerrainMaterialRegistration
    {
        // Leave the top byte value unused until all native shader consumers have proven its semantics.
        private const int Capacity = byte.MaxValue;
        private const string FallbackMaterial = "TEX_ROCK_A";
        private const string CountGlobal = "_TerrainMaterialCount";
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Declaration> Declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        private static ProfileSession? profile;
        private static Action<string>? warning;
        private static bool enabled;
        private static bool sealedDeclarations;
        private static Publication? active;
        private static string? failure;

        internal static bool IsEnabled => enabled;

        public static bool IsAvailable => enabled && failure == null && active is Publication publication && OwnsTable(publication.Rows);

        /// <summary>Declares a terrain material identity and its case-sensitive stock backing material during Awake.</summary>
        public static void Register(string ownerId, string localName, int revision, string stockBackingMaterial)
            => RegisterCore(ownerId, localName, revision, stockBackingMaterial, null);

        /// <summary>Declares a stock-backed material with optional replacement layers from a game-thread factory.</summary>
        public static void Register(string ownerId, string localName, int revision, string stockBackingMaterial,
            Func<TerrainTextureLayers> createLayers)
            => RegisterCore(ownerId, localName, revision, stockBackingMaterial,
                createLayers ?? throw new ArgumentNullException(nameof(createLayers)));

        /// <summary>Uses another stock surface's art and recolors its diffuse layer without changing the gameplay backing.</summary>
        public static void RegisterStockVisual(string ownerId, string localName, int revision, string stockBackingMaterial,
            string stockVisualMaterial, Color32 tint, float brightness = 1f)
        {
            if (string.IsNullOrWhiteSpace(stockVisualMaterial))
                throw new ArgumentException("A stock visual shader name is required.", nameof(stockVisualMaterial));
            if (!float.IsFinite(brightness) || brightness <= 0 || brightness > 4)
                throw new ArgumentOutOfRangeException(nameof(brightness));
            RegisterCore(ownerId, localName, revision, stockBackingMaterial, null,
                new TerrainTextureOverride.StockTint(stockVisualMaterial, tint, brightness));
        }

        private static void RegisterCore(string ownerId, string localName, int revision, string stockBackingMaterial,
            Func<TerrainTextureLayers>? createLayers, TerrainTextureOverride.StockTint? stockTint = null)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
            if (string.IsNullOrWhiteSpace(stockBackingMaterial))
                throw new ArgumentException("A stock terrain material ShaderName is required.", nameof(stockBackingMaterial));
            lock (Gate)
            {
                if (sealedDeclarations) throw new InvalidOperationException("Terrain material declarations are closed for this process.");
                if (Declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate terrain material declaration: " + key);
                Declarations.Add(key, new Declaration(revision, stockBackingMaterial, createLayers, stockTint));
            }
        }

        /// <summary>Resolves a complete namespaced key after the material table has been safely published.</summary>
        public static bool TryGetId(string key, out byte id)
        {
            id = 0;
            var publication = active;
            return key != null && enabled && failure == null && publication != null && OwnsTable(publication.Rows)
                && publication.Ids.TryGetValue(key, out id);
        }

        internal static bool TryGetStockCount(out int count)
        {
            var publication = active;
            count = 0;
            if (publication == null || !enabled || failure != null || !OwnsTable(publication.Rows)) return false;
            count = publication.StockCount;
            return true;
        }

        internal static bool TryGetKey(int id, out string? key)
        {
            var publication = active;
            key = null;
            return publication != null && enabled && failure == null && OwnsTable(publication.Rows)
                && publication.Keys.TryGetValue(id, out key);
        }

        internal static void Install(Harmony harmony, ProfileSession session, Action<string> reportWarning)
        {
            TerrainTextureOverride.EnsurePublicationBridge(harmony, reportWarning);
            profile = session;
            warning = reportWarning;
            enabled = true;
        }

        internal static void Disable() => enabled = false;

        internal static TerrainTextureOverride.LayerRequest[] GetTextureRequests()
        {
            if (!enabled) return Array.Empty<TerrainTextureOverride.LayerRequest>();
            lock (Gate)
            {
                sealedDeclarations = true;
                return Declarations.Where(pair => pair.Value.CreateLayers != null || pair.Value.StockTint.HasValue).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new TerrainTextureOverride.LayerRequest(pair.Key, pair.Value.StockTint?.Material ?? pair.Value.BackingMaterial,
                        pair.Value.CreateLayers, isRegisteredMaterial: true, stockTint: pair.Value.StockTint)).ToArray();
            }
        }

        internal static bool HasPersistentTerrainMaterialState(ProfileSession session)
        {
            try
            {
                string? json = session.ReadFeatureRegistry(ResourceKind.TerrainMaterial);
                return json != null && RegistryJson.Read(json).Mappings.Count != 0;
            }
            catch
            {
                // An unreadable registry may still describe material IDs present in saved terrain.
                return true;
            }
        }

        internal static string? GetWorldAdmissionFailure(ProfileSession? session, bool remoteLobby)
        {
            if (session == null) return null;
            try
            {
                string? json = session.ReadFeatureRegistry(ResourceKind.TerrainMaterial);
                if (json == null || RegistryJson.Read(json).Mappings.Count == 0) return null;
                if (remoteLobby) return "This profile contains persistent Keel terrain materials. Use it in solo play.";
                if (session != profile || !IsAvailable || !string.Equals(active?.Registry, json, StringComparison.Ordinal)
                    || Shader.GetGlobalInt(CountGlobal) != active!.Rows.Length || !TerrainTextureOverride.BindingsIntact())
                    return "Keel could not restore the terrain materials required by this profile. "
                        + (failure ?? "Enable terrain material registration and restart with a compatible mod profile.");
                var publication = active!;
                for (int i = 0; i < publication.BackingRows.Length; i++)
                {
                    int rowId = publication.StockCount + i;
                    if (!publication.Rows[rowId].Equals(GetBackingRow(publication, rowId)))
                        return "A live Keel terrain material row was overwritten. Restart with a compatible mod profile.";
                }
                return null;
            }
            catch (Exception exception)
            {
                return "Keel could not validate the terrain material registry. " + exception.Message;
            }
        }

        internal static bool OwnsTable(NativeArray<TerrainMaterialData> rows)
        {
            var publication = active;
            return publication != null && rows.IsCreated && publication.Rows.Equals(rows)
                && WorldTerrainResources.TerrainMaterialDatas.Equals(rows);
        }

        internal static void EndPublication()
        {
            var publication = active;
            active = null;
            failure = null;
            if (publication == null) return;
            // Stock has released the active table. Keel still owns the earlier stock allocation.
            try { publication.StockRows.Dispose(); }
            catch (Exception exception) { warning!("Could not release the previous terrain material table: " + exception.Message); }
        }

        internal static bool TryPublish(ComputeBufferHandler<TerrainMaterialData> buffer, NativeArray<TerrainMaterialData> rows,
            int count, WorldTerrainResources repo)
        {
            if (!enabled) return false;
            if (OwnsTable(rows))
            {
                // Stock has rewritten its original rows. Restore the same custom backing choices before uploading again.
                var publication = active!;
                try
                {
                    if (count != rows.Length || repo.terrainTextures.Count != publication.StockCount)
                        throw new InvalidOperationException("The live terrain material table changed before republishing.");
                    SetBackingRows(publication);
                    buffer.PushData(rows, count);
                    failure = null;
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                    throw;
                }
                return true;
            }

            NativeArray<TerrainMaterialData> candidate = default;
            try
            {
                if (active != null || count != rows.Length || count != repo.terrainTextures.Count || count <= 0 || count > Capacity
                    || !WorldTerrainResources.TerrainMaterialDatas.Equals(rows))
                    throw new InvalidOperationException("Stock terrain rows do not match the live byte-sized terrain table.");
                var session = profile!;
                var json = session.ReadFeatureRegistry(ResourceKind.TerrainMaterial);
                KeyValuePair<string, Declaration>[] declarations;
                lock (Gate)
                {
                    sealedDeclarations = true;
                    declarations = Declarations.ToArray();
                }
                if (json == null && declarations.Length == 0) return false;
                var spaces = new[]
                {
                    new ResourceSpace(ResourceKind.TerrainMaterial, Capacity, Array.Empty<int>(), Enumerable.Range(0, count))
                };
                var allocator = json == null ? new ContentAllocator(spaces, session.ProfileId) : ContentAllocator.FromJson(json, spaces);
                var prior = json == null ? Array.Empty<Mapping>() : RegistryJson.Read(json).Mappings.ToArray();
                var collision = prior.FirstOrDefault(mapping => mapping.Id < count);
                if (collision != null)
                    throw new InvalidOperationException("Stock terrain now occupies persisted material " + collision.Key + " (" + collision.Id + ").");

                int fallback = FindMaterial(repo, FallbackMaterial);
                var requested = declarations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var sources = requested.ToDictionary(pair => pair.Key, pair => FindMaterial(repo, pair.Value.BackingMaterial), StringComparer.Ordinal);
                var ids = allocator.Apply(declarations.Select(pair => new ContentRequest(pair.Key, ResourceKind.TerrainMaterial, pair.Value.Revision)));
                string updated = allocator.ToJson();
                var mappings = RegistryJson.Read(updated).Mappings;
                if (mappings.Count == 0) return false;
                int length = mappings.Max(mapping => mapping.Id) + 1;
                var backingRows = Enumerable.Repeat(fallback, length - count).ToArray();
                foreach (var id in ids) backingRows[id.Value - count] = sources[id.Key];

                candidate = new NativeArray<TerrainMaterialData>(length, Allocator.Persistent);
                NativeArray<TerrainMaterialData>.Copy(rows, candidate, count);
                var publication = new Publication(rows, candidate, backingRows, ids, updated);
                SetBackingRows(publication);
                try
                {
                    buffer.PushData(candidate, length);
                    Shader.SetGlobalInt(CountGlobal, length);
                    if (Shader.GetGlobalInt(CountGlobal) != length)
                        throw new InvalidOperationException("Unity did not retain the terrain material count.");
                    if (!string.Equals(updated, json, StringComparison.Ordinal)) session.WriteFeatureRegistry(ResourceKind.TerrainMaterial, updated);
                }
                catch
                {
                    // The stock table still owns CPU data. Restore its GPU contents before reporting failure.
                    buffer.PushData(rows, count);
                    Shader.SetGlobalInt(CountGlobal, count);
                    throw;
                }
                WorldTerrainResources.TerrainMaterialDatas = candidate;
                active = publication;
                candidate = default;
                failure = null;
                return true;
            }
            catch (Exception exception)
            {
                failure = exception.Message;
                warning!("Terrain material registration retained the stock table: " + exception.Message);
                return false;
            }
            finally
            {
                if (candidate.IsCreated) candidate.Dispose();
            }
        }

        private static int FindMaterial(WorldTerrainResources repo, string name)
        {
            int index = repo.terrainTextures.FindIndex(material => material.ShaderName == name);
            if (index < 0 || repo.terrainTextures.FindLastIndex(material => material.ShaderName == name) != index)
                throw new InvalidOperationException("Missing or ambiguous stock terrain backing material: " + name);
            return index;
        }

        private static void SetBackingRows(Publication publication)
        {
            var rows = publication.Rows;
            for (int id = publication.StockCount; id < rows.Length; id++) rows[id] = GetBackingRow(publication, id);
        }

        private static TerrainMaterialData GetBackingRow(Publication publication, int id)
        {
            var row = publication.Rows[publication.BackingRows[id - publication.StockCount]];
            if (publication.Keys.TryGetValue(id, out var key)) TerrainTextureOverride.ApplyRegisteredTexture(key, ref row);
            return row;
        }

        private sealed class Declaration
        {
            internal Declaration(int revision, string backingMaterial, Func<TerrainTextureLayers>? createLayers,
                TerrainTextureOverride.StockTint? stockTint)
            {
                Revision = revision;
                BackingMaterial = backingMaterial;
                CreateLayers = createLayers;
                StockTint = stockTint;
            }

            internal int Revision { get; }
            internal string BackingMaterial { get; }
            internal Func<TerrainTextureLayers>? CreateLayers { get; }
            internal TerrainTextureOverride.StockTint? StockTint { get; }
        }

        private sealed class Publication
        {
            internal Publication(NativeArray<TerrainMaterialData> stockRows, NativeArray<TerrainMaterialData> rows, int[] backingRows,
                IReadOnlyDictionary<string, int> ids, string registry)
            {
                StockRows = stockRows;
                Rows = rows;
                BackingRows = backingRows;
                Ids = ids.ToDictionary(pair => pair.Key, pair => checked((byte)pair.Value), StringComparer.Ordinal);
                Keys = ids.ToDictionary(pair => pair.Value, pair => pair.Key);
                Registry = registry;
            }

            internal NativeArray<TerrainMaterialData> StockRows { get; }
            internal NativeArray<TerrainMaterialData> Rows { get; }
            internal int StockCount => StockRows.Length;
            internal int[] BackingRows { get; }
            internal IReadOnlyDictionary<string, byte> Ids { get; }
            internal IReadOnlyDictionary<int, string> Keys { get; }
            internal string Registry { get; }
        }
    }
}
