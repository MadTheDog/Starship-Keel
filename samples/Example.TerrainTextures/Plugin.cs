using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx;
using Common;
using Sevo.Framework.Runtime;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using World;
using World.Terrain;

namespace Sevo.Example.TerrainTextures
{
    [BepInPlugin(Id, "Keel Terrain Texture Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Id = "example.terraintextures";
        private const string Diffuse = "WORLD_DIFFUSE_TEX";
        private const string Normal = "WORLD_NORMAL_TEX";
        private const string Sand = "TEX_SAND_A";
        private const string Iron = "RAW_IRON_A";
        private readonly Dictionary<string, Texture2D> replacements = new Dictionary<string, Texture2D>();
        private Texture2DArray? stockDiffuse;
        private Texture2DArray? stockNormal;
        private int verifiedCycles;
        private bool enabledProbe;
        private bool failed;
        private Texture2DArray? checkedDiffuse;
        private string? distantCapture;
        private bool distantCaptured;

        private void Awake()
        {
            enabledProbe = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_TEXTURE_PROBE") == "1";
            distantCapture = Environment.GetEnvironmentVariable("SEVO_KEEL_TERRAIN_TEXTURE_DISTANT_CAPTURE");
            if (!enabledProbe) return;
            TerrainTextureOverride.Register(Id, "sand-diffuse", Sand, () =>
                new TerrainTextureLayers(diffuse: CopyStockLayer(Sand, Diffuse, 3)));
            TerrainTextureOverride.Register(Id, "iron-normal", Iron, () =>
                new TerrainTextureLayers(normal: CopyStockLayer(Iron, Normal, 4)));
        }

        private Texture2D CopyStockLayer(string name, string global, int source)
        {
            stockDiffuse = Shader.GetGlobalTexture(Diffuse) as Texture2DArray;
            stockNormal = Shader.GetGlobalTexture(Normal) as Texture2DArray;
            var stock = (Texture2DArray)Shader.GetGlobalTexture(global);
            if (replacements.TryGetValue(name, out var previous)) Destroy(previous);
            var texture = new Texture2D(stock.width, stock.height, stock.graphicsFormat, stock.mipmapCount, TextureCreationFlags.MipChain);
            try
            {
                for (int mip = 0; mip < stock.mipmapCount; mip++)
                    stock.GetPixelData<byte>(mip, source).CopyTo(texture.GetPixelData<byte>(mip));
                texture.Apply(false, false);
                replacements[name] = texture;
                return texture;
            }
            catch
            {
                Destroy(texture);
                throw;
            }
        }

        private void Update()
        {
            if (!distantCaptured && distantCapture is "stock" or "override" && PlayerState.sessionMode == PlayerState.SessionMode.solo)
            {
                try { distantCaptured = CaptureDistantTerrain(distantCapture); }
                catch (Exception exception)
                {
                    distantCaptured = true;
                    Logger.LogError("KEEL_TERRAIN_TEXTURE_DISTANT FAIL: " + exception);
                }
            }
            if (!enabledProbe || failed) return;
            var rows = WorldTerrainResources.TerrainMaterialDatas;
            if (!rows.IsCreated || rows.Length == 0) return;
            var diffuse = Shader.GetGlobalTexture(Diffuse) as Texture2DArray;
            if (diffuse == checkedDiffuse) return;
            try
            {
                Verify(rows, diffuse, Shader.GetGlobalTexture(Normal) as Texture2DArray);
                checkedDiffuse = diffuse;
                Logger.LogInfo($"KEEL_TERRAIN_TEXTURE_PROBE PASS cycle={++verifiedCycles} diffuse={diffuse!.graphicsFormat} normal={stockNormal!.graphicsFormat}");
            }
            catch (Exception exception)
            {
                failed = true;
                Logger.LogError("KEEL_TERRAIN_TEXTURE_PROBE FAIL: " + exception);
            }
        }

        private bool CaptureDistantTerrain(string mode)
        {
            var texture = Shader.GetGlobalTexture("PLANET_TERRAIN_DIFFUSE") as Texture2DArray;
            var nodes = PlanetTextureNodeData.NodeDatas;
            if (texture == null || !nodes.IsCreated) return false;
            var candidates = Enumerable.Range(0, Math.Min(nodes.Length, texture.depth))
                .Select(index => (Index: index, Node: nodes[index]))
                .Where(candidate => candidate.Node.planetGenerationId > 0)
                .GroupBy(candidate => candidate.Node.planetGenerationId)
                .Where(group => group.Select(candidate => candidate.Node.parentFaceId).Distinct().Count() == 6)
                .OrderBy(group => group.Key)
                .FirstOrDefault();
            if (candidates == null) return false;
            if (TerrainTextureOverride.IsAvailable != (mode == "override"))
                throw new InvalidOperationException("Texture override availability does not match the capture mode.");
            if (mode == "override" && checkedDiffuse == null) return false;

            using var sha = SHA256.Create();
            foreach (var face in candidates.GroupBy(candidate => candidate.Node.parentFaceId).OrderBy(group => group.Key))
            {
                var selected = face.OrderBy(candidate => candidate.Node.level).First();
                var bytes = ReadGpuLayer(texture, selected.Index, 0);
                if (bytes.All(value => value == 0))
                    throw new InvalidOperationException("The distant planet texture has no published image.");
                var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                Logger.LogInfo(FormattableString.Invariant($"KEEL_TERRAIN_TEXTURE_DISTANT mode={mode} planet={selected.Node.planetGenerationId} face={face.Key} level={selected.Node.level} width={texture.width} sha256={hash}"));
            }
            Logger.LogInfo("KEEL_TERRAIN_TEXTURE_DISTANT PASS mode=" + mode);
            return true;
        }

        private void Verify(NativeArray<TerrainMaterialData> rows, Texture2DArray? diffuse, Texture2DArray? normal)
        {
            if (!TerrainTextureOverride.IsAvailable || stockDiffuse == null || stockNormal == null
                || diffuse == null || normal == null || diffuse == stockDiffuse || normal == stockNormal)
                throw new InvalidOperationException("The expanded terrain textures were not bound.");
            int firstLayer = Math.Max(stockDiffuse.depth, stockNormal.depth);
            if (diffuse.depth != firstLayer + 2 || normal.depth != firstLayer + 2)
                throw new InvalidOperationException("The two overrides did not publish paired layers.");
            var resources = Resources.FindObjectsOfTypeAll<WorldTerrainResources>().Single(asset =>
                asset.terrainTextures.Count <= rows.Length
                && asset.terrainTextures.Any(texture => texture.ShaderName == Sand)
                && asset.terrainTextures.Any(texture => texture.ShaderName == Iron));
            int sand = resources.terrainTextures.FindIndex(texture => texture.ShaderName == Sand);
            int iron = resources.terrainTextures.FindIndex(texture => texture.ShaderName == Iron);
            var handler = (ComputeBufferHandler<TerrainMaterialData>)typeof(WorldTerrainResources)
                .GetField("MaterialDataBuffer", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var upload = (ComputeBuffer)typeof(ComputeBufferHandler<TerrainMaterialData>)
                .GetField("computeBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            int sandLayer = WorldTerrainResources.DUMMY.textures.IndexOf(resources.terrainTextures[sand].textureDiffuse);
            int ironLayer = WorldTerrainResources.DUMMY.textures.IndexOf(resources.terrainTextures[iron].textureDiffuse);
            VerifyMaterial(upload, rows[sand], sand, sandLayer, firstLayer, diffuse, normal, replacements[Sand], null);
            VerifyMaterial(upload, rows[iron], iron, ironLayer, firstLayer, diffuse, normal, null, replacements[Iron]);
            VerifyUploadedLayer(stockDiffuse, 3, diffuse, rows[sand].textureId);
            VerifyUploadedLayer(stockNormal, 4, normal, rows[iron].textureId);

            for (int index = 0; index < resources.terrainTextures.Count; index++)
            {
                if (index != sand && index != iron && rows[index].textureId >= firstLayer)
                    throw new InvalidOperationException("An undeclared material was remapped.");
            }
            VerifyStockMips(stockDiffuse, diffuse);
            VerifyStockMips(stockNormal, normal);
        }

        private void VerifyMaterial(ComputeBuffer upload, TerrainMaterialData row, int rowId, int stockLayer,
            int firstLayer, Texture2DArray diffuse, Texture2DArray normal, Texture2D? expectedDiffuse, Texture2D? expectedNormal)
        {
            int layer = row.textureId;
            if (stockLayer < 0 || layer < firstLayer)
                throw new InvalidOperationException("A native terrain row did not upload its appended layer.");
            var color = diffuse.GetPixels(layer, diffuse.mipmapCount - 1)[0];
            if (row.fadeValue.x != color.r || row.fadeValue.y != color.g || row.fadeValue.z != color.b || row.fadeValue.w != color.a)
                throw new InvalidOperationException("The smallest diffuse mip color did not reach the native material buffer.");
            VerifyUploadedRow(upload, rowId, layer, color);
            for (int mip = 0; mip < diffuse.mipmapCount; mip++)
                Compare(expectedDiffuse != null ? expectedDiffuse.GetPixelData<byte>(mip) : stockDiffuse!.GetPixelData<byte>(mip, stockLayer),
                    diffuse.GetPixelData<byte>(mip, layer));
            for (int mip = 0; mip < normal.mipmapCount; mip++)
                Compare(expectedNormal != null ? expectedNormal.GetPixelData<byte>(mip) : stockNormal!.GetPixelData<byte>(mip, stockLayer),
                    normal.GetPixelData<byte>(mip, layer));
        }

        private static void VerifyUploadedRow(ComputeBuffer upload, int row, int layer, Color fade)
        {
            if (upload.stride != Marshal.SizeOf<TerrainMaterialData>())
                throw new InvalidOperationException("The live terrain buffer stride does not match its native row.");
            var request = AsyncGPUReadback.Request(upload, upload.stride, row * upload.stride);
            request.WaitForCompletion();
            if (request.hasError) throw new InvalidOperationException("Terrain material GPU readback failed.");
            var data = request.GetData<byte>().ToArray();
            int textureIdOffset = Marshal.OffsetOf<TerrainMaterialData>(nameof(TerrainMaterialData.textureId)).ToInt32();
            int fadeOffset = Marshal.OffsetOf<TerrainMaterialData>(nameof(TerrainMaterialData.fadeValue)).ToInt32();
            if (BitConverter.ToInt32(data, textureIdOffset) != layer
                || BitConverter.ToSingle(data, fadeOffset) != fade.r
                || BitConverter.ToSingle(data, fadeOffset + 4) != fade.g
                || BitConverter.ToSingle(data, fadeOffset + 8) != fade.b
                || BitConverter.ToSingle(data, fadeOffset + 12) != fade.a)
                throw new InvalidOperationException("The layer or diffuse mip color did not reach the native GPU material row.");
        }

        private static void VerifyUploadedLayer(Texture2DArray stock, int donorLayer, Texture2DArray expanded, int layer)
        {
            int distantMip = Math.Min(4, stock.mipmapCount - 1);
            foreach (int mip in new[] { 0, distantMip }.Distinct())
            {
                var expected = SampleGpuLayer(stock, donorLayer, mip);
                var uploaded = SampleGpuLayer(expanded, layer, mip);
                bool donorVaries = expected.Skip(4).Where((value, index) => value != expected[index % 4]).Any();
                if (!donorVaries || !uploaded.SequenceEqual(expected))
                    throw new InvalidOperationException("The uploaded terrain layer differs from its donor at mip " + mip + ".");
            }
        }

        private static byte[] SampleGpuLayer(Texture2DArray texture, int layer, int mip)
        {
            int width = Math.Max(1, texture.width >> mip);
            int height = Math.Max(1, texture.height >> mip);
            var slice = new Texture2D(width, height, texture.graphicsFormat, 1, TextureCreationFlags.None);
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(texture, layer, mip, slice, 0, 0);
                Graphics.Blit(slice, target);
                var request = AsyncGPUReadback.Request(target, 0);
                request.WaitForCompletion();
                if (request.hasError) throw new InvalidOperationException("Sampled terrain mip readback failed.");
                return request.GetData<byte>().ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Destroy(slice);
            }
        }

        private static byte[] ReadGpuLayer(Texture2DArray texture, int layer, int mip)
        {
            int width = Math.Max(1, texture.width >> mip);
            int height = Math.Max(1, texture.height >> mip);
            var request = AsyncGPUReadback.Request(texture, mip, 0, width, 0, height, layer, 1);
            request.WaitForCompletion();
            if (request.hasError)
                throw new InvalidOperationException("Terrain texture GPU readback failed at mip " + mip + ".");
            var bytes = request.GetData<byte>().ToArray();
            if (bytes.Length == 0)
                throw new InvalidOperationException("Terrain texture GPU readback was empty at mip " + mip + ".");
            return bytes;
        }

        private static void VerifyStockMips(Texture2DArray stock, Texture2DArray expanded)
        {
            for (int slice = 0; slice < stock.depth; slice++)
                for (int mip = 0; mip < stock.mipmapCount; mip++)
                    Compare(stock.GetPixelData<byte>(mip, slice), expanded.GetPixelData<byte>(mip, slice));
        }

        private static void Compare(NativeArray<byte> left, NativeArray<byte> right)
        {
            if (left.Length != right.Length) throw new InvalidOperationException("Terrain mip byte counts differ.");
            for (int index = 0; index < left.Length; index++)
                if (left[index] != right[index]) throw new InvalidOperationException("Terrain mip contents differ.");
        }

        private void OnDestroy()
        {
            foreach (var texture in replacements.Values) Destroy(texture);
        }
    }
}
