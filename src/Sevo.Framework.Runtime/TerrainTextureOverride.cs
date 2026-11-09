using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Common;
using HarmonyLib;
using Sevo.Framework.Sdk;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using World;
using Object = UnityEngine.Object;

namespace Sevo.Framework.Runtime
{
    /// <summary>A pair of mod-supplied terrain texture layers. A missing layer retains its stock texture.</summary>
    public sealed class TerrainTextureLayers
    {
        public TerrainTextureLayers(Texture2D? diffuse = null, Texture2D? normal = null)
        {
            if (diffuse == null && normal == null) throw new ArgumentException("At least one terrain texture layer is required.");
            Diffuse = diffuse;
            Normal = normal;
        }

        public Texture2D? Diffuse { get; }
        public Texture2D? Normal { get; }
    }

    /// <summary>Replaces the rendered texture layers of existing named terrain materials.</summary>
    public static class TerrainTextureOverride
    {
        private const string DiffuseGlobal = "WORLD_DIFFUSE_TEX";
        private const string NormalGlobal = "WORLD_NORMAL_TEX";
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Declaration> Declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        private static Declaration[]? sealedDeclarations;
        private static Action<string>? warning;
        private static FieldInfo? loaderDiffuse;
        private static FieldInfo? loaderNormal;
        private static FieldInfo? repoDiffuse;
        private static Binding? active;
        private static bool bridgeInstalled;

        public static bool IsAvailable { get; private set; }

        /// <summary>Registers a factory in Awake. It runs on the game thread during terrain resource publication.</summary>
        public static void Register(string ownerId, string localName, string nativeMaterialName, Func<TerrainTextureLayers> createLayers)
        {
            string key = ownerId + ":" + localName;
            ContentAllocator.ValidateKey(key);
            if (string.IsNullOrWhiteSpace(nativeMaterialName))
                throw new ArgumentException("A native terrain material ShaderName is required.", nameof(nativeMaterialName));
            if (createLayers == null) throw new ArgumentNullException(nameof(createLayers));
            lock (Gate)
            {
                if (sealedDeclarations != null) throw new InvalidOperationException("Terrain texture declarations are closed for this process.");
                if (Declarations.ContainsKey(key)) throw new InvalidOperationException("Duplicate terrain texture override: " + key);
                Declarations.Add(key, new Declaration(key, nativeMaterialName, createLayers));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Install(Harmony harmony, Action<string> reportWarning)
        {
            loaderDiffuse = RequireField(typeof(ResourcesLoader), "worldTerrainDiffuse");
            loaderNormal = RequireField(typeof(ResourcesLoader), "worldTerrainNormal");
            repoDiffuse = RequireField(typeof(WorldTerrainResources), "ta_worldTerrainDiffuse");
            var quality = AccessTools.DeclaredMethod(typeof(ResourcesLoader), nameof(ResourcesLoader.ApplyTerrainTextureQuality));
            harmony.Patch(quality, postfix: new HarmonyMethod(typeof(TerrainTextureOverride), nameof(RefreshQuality)));
            EnsurePublicationBridge(harmony, reportWarning);
            IsAvailable = true;
        }

        internal static void EnsurePublicationBridge(Harmony harmony, Action<string> reportWarning)
        {
            if (bridgeInstalled) return;
            var push = AccessTools.DeclaredMethod(typeof(WorldTerrainResources), "PushMaterialDatas", Type.EmptyTypes)
                ?? throw new MissingMethodException("WorldTerrainResources.PushMaterialDatas");
            var unload = AccessTools.DeclaredMethod(typeof(WorldTerrainResources), nameof(WorldTerrainResources.UnloadResourcesOnQuitToTitle));
            var release = AccessTools.DeclaredMethod(typeof(WorldTerrainResources), nameof(WorldTerrainResources.ReleaseResources));
            var cleanup = new HarmonyMethod(typeof(TerrainTextureOverride), nameof(EndPublication));
            warning = reportWarning;
            harmony.Patch(push, transpiler: new HarmonyMethod(typeof(TerrainTextureOverride), nameof(BridgeUpload)));
            harmony.Patch(unload, postfix: cleanup);
            harmony.Patch(release, postfix: cleanup);
            bridgeInstalled = true;
        }

        internal static void Disable() => IsAvailable = false;

        private static FieldInfo RequireField(Type type, string name) =>
            AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

        private static IEnumerable<CodeInstruction> BridgeUpload(IEnumerable<CodeInstruction> instructions)
        {
            var target = AccessTools.DeclaredMethod(typeof(ComputeBufferHandler<TerrainMaterialData>), nameof(ComputeBufferHandler<TerrainMaterialData>.PushData));
            var bridge = AccessTools.DeclaredMethod(typeof(TerrainTextureOverride), nameof(PublishRows));
            var code = instructions.ToList();
            var upload = code.SingleOrDefault(instruction => instruction.opcode == OpCodes.Callvirt && Equals(instruction.operand, target));
            if (upload == null || upload.blocks.Count != 0)
                throw new InvalidOperationException("Terrain material buffer publication is missing, ambiguous, or protected by a new exception boundary.");
            int index = code.IndexOf(upload);
            upload.opcode = OpCodes.Ldarg_0;
            upload.operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, bridge));
            return code;
        }

        private static Declaration[] GetDeclarations()
        {
            lock (Gate)
            {
                if (sealedDeclarations != null) return sealedDeclarations;
                var selected = new List<Declaration>();
                foreach (var group in Declarations.Values.OrderBy(value => value.Key, StringComparer.Ordinal)
                    .GroupBy(value => value.MaterialName, StringComparer.Ordinal))
                {
                    var entries = group.ToArray();
                    if (entries.Length == 1) selected.Add(entries[0]);
                    else warning!("Terrain texture override retains stock material " + group.Key + " due to conflicting declarations: "
                        + string.Join(", ", entries.Select(entry => entry.Key)));
                }
                return sealedDeclarations = selected.ToArray();
            }
        }

        private static void PublishRows(ComputeBufferHandler<TerrainMaterialData> buffer, NativeArray<TerrainMaterialData> rows,
            int count, WorldTerrainResources repo)
        {
            if (IsAvailable)
            {
                try
                {
                    var declarations = GetDeclarations();
                    if (declarations.Length != 0 && TryPublishOverrides(buffer, rows, count, repo, declarations)) return;
                }
                catch (Exception exception)
                {
                    Disable();
                    ReleaseBindings();
                    warning!("Terrain texture overrides disabled: " + exception.Message);
                }
            }
            UploadRows(buffer, rows, count, repo);
        }

        private static bool TryPublishOverrides(ComputeBufferHandler<TerrainMaterialData> buffer, NativeArray<TerrainMaterialData> rows,
            int count, WorldTerrainResources repo, Declaration[] declarations)
        {
            var diffuse = loaderDiffuse!.GetValue(null) as Texture2DArray;
            var normal = loaderNormal!.GetValue(null) as Texture2DArray;
            if (!IsSupported(diffuse) || !IsSupported(normal) || repoDiffuse!.GetValue(repo) as Texture2DArray != diffuse
                || rows.Length != count || (repo.terrainTextures.Count != count && !TerrainMaterialRegistration.OwnsTable(rows)))
                throw new InvalidOperationException("The live terrain resource aliases, readable mip chains, or row count are incompatible.");
            if (Shader.GetGlobalTexture(DiffuseGlobal) != (active?.Diffuse ?? diffuse)
                || Shader.GetGlobalTexture(NormalGlobal) != (active?.Normal ?? normal))
                throw new InvalidOperationException("The shared terrain texture bindings changed outside Keel.");
            if (active == null) active = PrepareBinding(rows, repo, declarations, diffuse!, normal!);
            if (active == null) return false;
            var binding = active;
            var originals = new TerrainMaterialData[binding.Layers.Length];
            for (int i = 0; i < originals.Length; i++)
            {
                var layer = binding.Layers[i];
                if ((uint)layer.Row >= (uint)count || repo.terrainTextures[layer.Row].ShaderName != layer.Name
                    || rows[layer.Row].textureId != layer.StockLayer || binding.StockDiffuse != diffuse || binding.StockNormal != normal)
                    throw new InvalidOperationException("The stock terrain material changed during texture re-publication: " + layer.Name);
                originals[i] = rows[layer.Row];
            }
            try
            {
                Shader.SetGlobalTexture(DiffuseGlobal, binding.Diffuse);
                Shader.SetGlobalTexture(NormalGlobal, binding.Normal);
                if (Shader.GetGlobalTexture(DiffuseGlobal) != binding.Diffuse || Shader.GetGlobalTexture(NormalGlobal) != binding.Normal)
                    throw new InvalidOperationException("Unity did not retain the terrain texture bindings.");
                for (int i = 0; i < originals.Length; i++)
                {
                    var layer = binding.Layers[i];
                    var row = originals[i];
                    row.textureId = binding.FirstLayer + i;
                    row.fadeValue = (Vector4)layer.Fade;
                    rows[layer.Row] = row;
                }
                UploadRows(buffer, rows, count, repo);
                return true;
            }
            catch
            {
                for (int i = 0; i < originals.Length; i++) rows[binding.Layers[i].Row] = originals[i];
                throw;
            }
        }

        private static void UploadRows(ComputeBufferHandler<TerrainMaterialData> buffer, NativeArray<TerrainMaterialData> rows,
            int count, WorldTerrainResources repo)
        {
            if (!TerrainMaterialRegistration.TryPublish(buffer, rows, count, repo)) buffer.PushData(rows, count);
        }

        private static Binding? PrepareBinding(NativeArray<TerrainMaterialData> rows, WorldTerrainResources repo,
            Declaration[] declarations, Texture2DArray diffuse, Texture2DArray normal)
        {
            var accepted = new List<Layer>();
            foreach (var declaration in declarations)
            {
                int row = repo.terrainTextures.FindIndex(material => material.ShaderName == declaration.MaterialName);
                if (row < 0 || repo.terrainTextures.FindLastIndex(material => material.ShaderName == declaration.MaterialName) != row)
                {
                    warning!("Terrain texture override skipped for " + declaration.Key + ": missing or ambiguous material " + declaration.MaterialName);
                    continue;
                }
                try
                {
                    int stockLayer = rows[row].textureId;
                    if ((uint)stockLayer >= (uint)diffuse.depth || (uint)stockLayer >= (uint)normal.depth)
                        throw new InvalidOperationException("The original texture layer is missing.");
                    var textures = declaration.CreateLayers();
                    if (textures == null || (textures.Diffuse == null && textures.Normal == null))
                        throw new InvalidOperationException("The factory did not return a live terrain texture.");
                    ValidateTexture(textures.Diffuse, diffuse);
                    ValidateTexture(textures.Normal, normal);
                    accepted.Add(new Layer(row, declaration.MaterialName, stockLayer, textures));
                }
                catch (Exception exception)
                {
                    warning!("Terrain texture override skipped for " + declaration.Key + ": " + exception.Message);
                }
            }
            if (accepted.Count == 0) return null;
            int firstLayer = Math.Max(diffuse.depth, normal.depth);
            if (accepted.Count > SystemInfo.maxTextureArraySlices - firstLayer)
                throw new InvalidOperationException("There are not enough supported terrain texture slices.");
            Texture2DArray? newDiffuse = null;
            Texture2DArray? newNormal = null;
            try
            {
                newDiffuse = Expand(diffuse, firstLayer, accepted, layer => layer.Textures.Diffuse);
                newNormal = Expand(normal, firstLayer, accepted, layer => layer.Textures.Normal);
                for (int i = 0; i < accepted.Count; i++)
                    accepted[i].Fade = newDiffuse.GetPixels(firstLayer + i, newDiffuse.mipmapCount - 1)[0];
                return new Binding(diffuse, normal, newDiffuse, newNormal, firstLayer, accepted.ToArray());
            }
            catch
            {
                if (newDiffuse != null) Object.Destroy(newDiffuse);
                if (newNormal != null) Object.Destroy(newNormal);
                throw;
            }
        }

        private static bool IsSupported(Texture2DArray? atlas) => atlas != null && atlas.isReadable
            && atlas.depth > 0 && atlas.width > 0 && atlas.height > 0
            && (Math.Max(atlas.width, atlas.height) >> (atlas.mipmapCount - 1)) == 1;

        private static void ValidateTexture(Texture2D? texture, Texture2DArray atlas)
        {
            if (texture == null) return;
            if (!texture.isReadable || texture.width != atlas.width || texture.height != atlas.height
                || texture.graphicsFormat != atlas.graphicsFormat || texture.mipmapCount != atlas.mipmapCount)
                throw new ArgumentException("Texture layers must be readable and match the native atlas dimensions, GPU format and mip chain.");
            for (int mip = 0; mip < atlas.mipmapCount; mip++)
            {
                if (texture.GetPixelData<byte>(mip).Length != atlas.GetPixelData<byte>(mip, 0).Length)
                    throw new ArgumentException("Texture mip data does not match the native atlas layout.");
            }
        }

        private static Texture2DArray Expand(Texture2DArray stock, int firstLayer, List<Layer> layers, Func<Layer, Texture2D?> select)
        {
            var mipLimit = new MipmapLimitDescriptor(!stock.ignoreMipmapLimit, stock.mipmapLimitGroup);
            var expanded = new Texture2DArray(stock.width, stock.height, firstLayer + layers.Count,
                stock.graphicsFormat, TextureCreationFlags.MipChain, stock.mipmapCount, mipLimit);
            try
            {
                if (!IsSupported(expanded) || expanded.graphicsFormat != stock.graphicsFormat || expanded.mipmapCount != stock.mipmapCount
                    || expanded.ignoreMipmapLimit != stock.ignoreMipmapLimit || expanded.mipmapLimitGroup != stock.mipmapLimitGroup)
                    throw new InvalidOperationException("Unity did not create a compatible expanded terrain texture array.");
                expanded.name = "Keel " + stock.name;
                expanded.wrapModeU = stock.wrapModeU;
                expanded.wrapModeV = stock.wrapModeV;
                expanded.wrapModeW = stock.wrapModeW;
                expanded.filterMode = stock.filterMode;
                CopyQuality(stock, expanded);
                for (int slice = 0; slice < stock.depth; slice++)
                    for (int mip = 0; mip < stock.mipmapCount; mip++)
                        stock.GetPixelData<byte>(mip, slice).CopyTo(expanded.GetPixelData<byte>(mip, slice));
                for (int index = 0; index < layers.Count; index++)
                {
                    var layer = layers[index];
                    var texture = select(layer);
                    for (int mip = 0; mip < stock.mipmapCount; mip++)
                    {
                        var source = texture != null ? texture.GetPixelData<byte>(mip) : stock.GetPixelData<byte>(mip, layer.StockLayer);
                        source.CopyTo(expanded.GetPixelData<byte>(mip, firstLayer + index));
                    }
                }
                expanded.Apply(false, false);
                return expanded;
            }
            catch
            {
                Object.Destroy(expanded);
                throw;
            }
        }

        private static void CopyQuality(Texture2DArray stock, Texture2DArray target)
        {
            target.anisoLevel = stock.anisoLevel;
            target.mipMapBias = stock.mipMapBias;
        }

        private static void RefreshQuality()
        {
            var binding = active;
            if (binding == null) return;
            CopyQuality(binding.StockDiffuse, binding.Diffuse);
            CopyQuality(binding.StockNormal, binding.Normal);
        }

        private static void ReleaseBindings()
        {
            var binding = active;
            active = null;
            if (binding == null) return;
            if (Shader.GetGlobalTexture(DiffuseGlobal) == binding.Diffuse)
                Shader.SetGlobalTexture(DiffuseGlobal, binding.StockDiffuse);
            if (Shader.GetGlobalTexture(NormalGlobal) == binding.Normal)
                Shader.SetGlobalTexture(NormalGlobal, binding.StockNormal);
            Object.Destroy(binding.Diffuse);
            Object.Destroy(binding.Normal);
        }

        private static void EndPublication()
        {
            TerrainMaterialRegistration.EndPublication();
            ReleaseBindings();
        }

        private sealed class Declaration
        {
            internal Declaration(string key, string materialName, Func<TerrainTextureLayers> createLayers)
            {
                Key = key;
                MaterialName = materialName;
                CreateLayers = createLayers;
            }

            internal string Key { get; }
            internal string MaterialName { get; }
            internal Func<TerrainTextureLayers> CreateLayers { get; }
        }

        private sealed class Layer
        {
            internal Layer(int row, string name, int stockLayer, TerrainTextureLayers textures)
            {
                Row = row;
                Name = name;
                StockLayer = stockLayer;
                Textures = textures;
            }

            internal int Row { get; }
            internal string Name { get; }
            internal int StockLayer { get; }
            internal TerrainTextureLayers Textures { get; }
            internal Color Fade { get; set; }
        }

        private sealed class Binding
        {
            internal Binding(Texture2DArray stockDiffuse, Texture2DArray stockNormal, Texture2DArray diffuse, Texture2DArray normal,
                int firstLayer, Layer[] layers)
            {
                StockDiffuse = stockDiffuse;
                StockNormal = stockNormal;
                Diffuse = diffuse;
                Normal = normal;
                FirstLayer = firstLayer;
                Layers = layers;
            }

            internal Texture2DArray StockDiffuse { get; }
            internal Texture2DArray StockNormal { get; }
            internal Texture2DArray Diffuse { get; }
            internal Texture2DArray Normal { get; }
            internal int FirstLayer { get; }
            internal Layer[] Layers { get; }
        }
    }
}
