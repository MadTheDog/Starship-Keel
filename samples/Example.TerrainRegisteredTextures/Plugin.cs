using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using Common;
using Sevo.Framework.Runtime;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using World;

namespace Sevo.Example.TerrainRegisteredTextures
{
    [BepInPlugin(Id, "Keel Registered Terrain Texture Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.10")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Id = "example.terrain-registered-textures";
        private const string Diffuse = "WORLD_DIFFUSE_TEX";
        private const string Normal = "WORLD_NORMAL_TEX";
        private const string Sand = "TEX_SAND_A";
        private const string Iron = "RAW_IRON_A";
        private readonly List<Texture2D> owned = new List<Texture2D>();
        private Texture2DArray? stockDiffuse;
        private Texture2DArray? stockNormal;
        private Texture2DArray? verified;
        private Texture2D? normalOverride;
        private Texture2D? customDiffuse;
        private bool enabledProbe;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE") != "1") return;
            try
            {
                string? session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
                string? profile = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
                bool legacyLab = Application.companyName == "SEVO Keel Hooks Lab016" && Application.productName == "SEVO HK Test";
                bool worldLab = Application.companyName == "SEVO Keel Gen Lab 0110" && Application.productName == "SEVO WG Test";
                if (!Guid.TryParseExact(session, "N", out _) || profile == null || (!legacyLab && !worldLab))
                    throw new InvalidOperationException("The disposable Keel hooks lab is required.");
                ProfileProtocol.Open(profile, Paths.GameRootPath, session!);
                TerrainTextureOverride.Register(Id, "stock-normal", Sand, () =>
                    new TerrainTextureLayers(normal: normalOverride = CopyStockLayer(Normal, 4)));
                TerrainMaterialRegistration.Register(Id, "colored", 1, Sand, () =>
                    new TerrainTextureLayers(diffuse: customDiffuse = CopyStockLayer(Diffuse, 3)));
                TerrainMaterialRegistration.Register(Id, "fallback", 1, Iron, () =>
                {
                    var invalid = new Texture2D(16, 16);
                    owned.Add(invalid);
                    return new TerrainTextureLayers(diffuse: invalid);
                });
                enabledProbe = true;
            }
            catch (Exception exception) { Logger.LogError("KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE FAIL " + exception); }
        }

        private Texture2D CopyStockLayer(string property, int source)
        {
            stockDiffuse = Shader.GetGlobalTexture(Diffuse) as Texture2DArray;
            stockNormal = Shader.GetGlobalTexture(Normal) as Texture2DArray;
            var atlas = (Texture2DArray)Shader.GetGlobalTexture(property);
            var texture = new Texture2D(atlas.width, atlas.height, atlas.graphicsFormat, atlas.mipmapCount, TextureCreationFlags.MipChain);
            owned.Add(texture);
            for (int mip = 0; mip < atlas.mipmapCount; mip++)
                atlas.GetPixelData<byte>(mip, source).CopyTo(texture.GetPixelData<byte>(mip));
            texture.Apply(false, false);
            return texture;
        }

        private void Update()
        {
            if (!enabledProbe) return;
            var rows = WorldTerrainResources.TerrainMaterialDatas;
            if (!rows.IsCreated || rows.Length == 0) return;
            var diffuse = Shader.GetGlobalTexture(Diffuse) as Texture2DArray;
            if (diffuse == verified) return;
            try
            {
                Verify(rows, diffuse, Shader.GetGlobalTexture(Normal) as Texture2DArray);
                verified = diffuse;
                Logger.LogInfo("KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE PASS customLayer=1 independentStockOverride=1 invalidFallback=1 gpuRow=true");
            }
            catch (Exception exception)
            {
                enabledProbe = false;
                Logger.LogError("KEEL_REGISTERED_TERRAIN_TEXTURE_PROBE FAIL " + exception);
            }
        }

        private void Verify(NativeArray<TerrainMaterialData> rows, Texture2DArray? diffuse, Texture2DArray? normal)
        {
            if (diffuse == null || normal == null || stockDiffuse == null || stockNormal == null
                || normalOverride == null || customDiffuse == null || !TerrainTextureOverride.IsAvailable
                || !TerrainMaterialRegistration.TryGetId(Id + ":colored", out byte custom)
                || !TerrainMaterialRegistration.TryGetId(Id + ":fallback", out byte fallback))
                throw new InvalidOperationException("The registered materials or atlas bindings were not published.");
            int firstLayer = Math.Max(stockDiffuse.depth, stockNormal.depth);
            if (diffuse.depth != firstLayer + 2 || normal.depth != firstLayer + 2)
                throw new InvalidOperationException("The stock and registered materials did not share one paired append.");
            var repo = Resources.FindObjectsOfTypeAll<WorldTerrainResources>().Single(asset =>
                asset.terrainTextures.Count < rows.Length && asset.terrainTextures.Any(material => material.ShaderName == Iron));
            int sand = repo.terrainTextures.FindIndex(material => material.ShaderName == Sand);
            int iron = repo.terrainTextures.FindIndex(material => material.ShaderName == Iron);
            int stockLayer = WorldTerrainResources.DUMMY.textures.IndexOf(repo.terrainTextures[sand].textureDiffuse);
            int originalSand = rows[sand].textureId;
            int customSand = rows[custom].textureId;
            if (custom < repo.terrainTextures.Count || originalSand < firstLayer || customSand < firstLayer
                || originalSand == customSand || !rows[fallback].Equals(rows[iron]))
                throw new InvalidOperationException("A texture leaked across material rows or the invalid layer did not retain stock backing.");
            for (int mip = 0; mip < diffuse.mipmapCount; mip++)
            {
                AssertMip(stockDiffuse.GetPixelData<byte>(mip, stockLayer), diffuse.GetPixelData<byte>(mip, originalSand));
                AssertMip(customDiffuse.GetPixelData<byte>(mip), diffuse.GetPixelData<byte>(mip, customSand));
            }
            for (int mip = 0; mip < normal.mipmapCount; mip++)
            {
                AssertMip(normalOverride.GetPixelData<byte>(mip), normal.GetPixelData<byte>(mip, originalSand));
                AssertMip(stockNormal.GetPixelData<byte>(mip, stockLayer), normal.GetPixelData<byte>(mip, customSand));
            }
            var handler = (ComputeBufferHandler<TerrainMaterialData>)typeof(WorldTerrainResources)
                .GetField("MaterialDataBuffer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var buffer = (ComputeBuffer)typeof(ComputeBufferHandler<TerrainMaterialData>)
                .GetField("computeBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            var color = diffuse.GetPixels(customSand, diffuse.mipmapCount - 1)[0];
            AssertGpuRow(buffer, custom, customSand, color);
        }

        private static void AssertGpuRow(ComputeBuffer buffer, byte id, int layer, Color fade)
        {
            if (buffer.stride != Marshal.SizeOf<TerrainMaterialData>())
                throw new InvalidOperationException("The terrain material buffer stride changed.");
            var request = AsyncGPUReadback.Request(buffer, buffer.stride, id * buffer.stride);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("The terrain material row readback failed.");
            byte[] bytes = request.GetData<byte>().ToArray();
            int textureOffset = Marshal.OffsetOf<TerrainMaterialData>(nameof(TerrainMaterialData.textureId)).ToInt32();
            int fadeOffset = Marshal.OffsetOf<TerrainMaterialData>(nameof(TerrainMaterialData.fadeValue)).ToInt32();
            if (BitConverter.ToInt32(bytes, textureOffset) != layer || BitConverter.ToSingle(bytes, fadeOffset) != fade.r
                || BitConverter.ToSingle(bytes, fadeOffset + 4) != fade.g || BitConverter.ToSingle(bytes, fadeOffset + 8) != fade.b
                || BitConverter.ToSingle(bytes, fadeOffset + 12) != fade.a)
                throw new InvalidOperationException("The custom material layer and fade color were not uploaded.");
        }

        private static void AssertMip(NativeArray<byte> left, NativeArray<byte> right)
        {
            if (left.Length != right.Length) throw new InvalidOperationException("Terrain mip lengths differ.");
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) throw new InvalidOperationException("Terrain mip contents differ.");
        }

        private void OnDestroy()
        {
            foreach (var texture in owned) Destroy(texture);
        }
    }
}
