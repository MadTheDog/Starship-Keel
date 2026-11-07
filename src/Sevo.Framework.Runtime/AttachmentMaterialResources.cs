using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using VoxelEngine;
using Object = UnityEngine.Object;

namespace Sevo.Framework.Runtime
{
    /// <summary>Extends the native atlases once, before the game uploads the material table.</summary>
    internal sealed class AttachmentMaterialResources
    {
        internal const int MaxPaletteSize=32;
        private const int MaxExtraLayers=64;
        private const string DiffuseName="MAIN_TEX", SpecularName="SPECULAR_TEX";
        private readonly List<PendingPalette> pending=new List<PendingPalette>();
        private Texture2DArray? liveDiffuse, liveSpecular;
        private VoxelMaterial[]? liveRows;

        private sealed class PendingPalette
        {
            internal PendingPalette(string key,Brick template,WeaponAttachmentVisual input,PreparedAttachmentVisual fallback)
            {
                Key=key; Template=template; Input=input; Fallback=fallback;
            }

            internal string Key { get; }
            internal Brick Template { get; }
            internal WeaponAttachmentVisual Input { get; }
            internal PreparedAttachmentVisual Fallback { get; }
        }

        internal void Stage(string key,Brick template,WeaponAttachmentVisual input,PreparedAttachmentVisual fallback)
            => pending.Add(new PendingPalette(key,template,input,fallback));

        internal string? Commit(bool allowPaint,Action<string> warn)
        {
            if(pending.Count==0) return null;
            var textures=new List<(Texture2D diffuse,Texture2D specular)>();
            var prepared=new List<(PendingPalette source,Mesh mesh,VoxelMaterial[] rows)>();
            Texture2DArray? diffuse=null, specular=null;
            bool published=false;
            try
            {
                var stockDiffuse=ResourcesLoader.VOXEL_DIFFUSE;
                var stockSpecular=Shader.GetGlobalTexture(SpecularName) as Texture2DArray;
                var stockRows=VoxelMaterialResources.materialParamDatas;
                int cursor=VoxelMaterialResources.MaterialCount;
                var nativeBuffer=typeof(VoxelMaterialResources).GetField("materialParamsBuffer",
                    BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                if(nativeBuffer==null || nativeBuffer.GetValue(null)!=null || stockRows==null
                    || cursor<0 || cursor>stockRows.Length || stockDiffuse!=Shader.GetGlobalTexture(DiffuseName)
                    || !SupportsAtlas(stockDiffuse) || !SupportsAtlas(stockSpecular)
                    || stockDiffuse.width!=stockSpecular!.width || stockDiffuse.height!=stockSpecular.height
                    || stockDiffuse.mipmapCount!=stockSpecular.mipmapCount)
                    throw new InvalidOperationException("The live native material table or readable BC3 atlas contract is unavailable.");

                int available=Math.Min(MaxExtraLayers,SystemInfo.maxTextureArraySlices-Math.Max(stockDiffuse.depth,stockSpecular.depth));
                foreach(var palette in pending)
                    PreparePalette(palette,stockDiffuse,stockSpecular,stockRows.Length,available,textures,prepared,allowPaint,warn);
                if(prepared.Count==0) return null;

                var rows=new VoxelMaterial[stockRows.Length+textures.Count];
                Array.Copy(stockRows,rows,stockRows.Length);
                foreach(var palette in prepared) Array.Copy(palette.rows,0,rows,palette.rows[0].Id,palette.rows.Length);
                diffuse=Expand(stockDiffuse,textures.ConvertAll(pair=>pair.diffuse));
                specular=Expand(stockSpecular,textures.ConvertAll(pair=>pair.specular));
                if(nativeBuffer.GetValue(null)!=null || !ReferenceEquals(stockRows,VoxelMaterialResources.materialParamDatas)
                    || cursor!=VoxelMaterialResources.MaterialCount || stockDiffuse!=ResourcesLoader.VOXEL_DIFFUSE
                    || stockDiffuse!=Shader.GetGlobalTexture(DiffuseName) || stockSpecular!=Shader.GetGlobalTexture(SpecularName))
                    throw new InvalidOperationException("Native material resources changed while Keel was preparing the palette.");

                try { Bind(diffuse,specular,rows,cursor); }
                catch(Exception publicationError)
                {
                    try { Bind(stockDiffuse,stockSpecular,stockRows,cursor); }
                    catch(Exception restorationError)
                    {
                        // Native globals may still reference these arrays. Stop registration and keep them alive.
                        published=true;
                        throw new MaterialRestorationException(publicationError,restorationError);
                    }
                    throw;
                }
                liveDiffuse=diffuse; liveSpecular=specular; liveRows=rows;
                published=true;
                foreach(var palette in prepared)
                {
                    var previous=palette.source.Fallback.Mesh;
                    palette.source.Fallback.Mesh=palette.mesh;
                    palette.source.Fallback.Palette=palette.rows;
                    Object.Destroy(previous);
                }
                return null;
            }
            catch(Exception exception) when(!published)
            {
                return "Using baked material fallbacks: "+exception.Message;
            }
            finally
            {
                if(!published)
                {
                    if(diffuse!=null) Object.Destroy(diffuse);
                    if(specular!=null) Object.Destroy(specular);
                    foreach(var palette in prepared) Object.Destroy(palette.mesh);
                }
                foreach(var (albedo,packed) in textures)
                {
                    Object.Destroy(albedo);
                    Object.Destroy(packed);
                }
                pending.Clear();
            }
        }

        internal void VerifyBindings()
        {
            if(liveRows!=null && !HasBindings(liveDiffuse,liveSpecular,liveRows))
                throw new InvalidOperationException("The published Keel voxel material bindings were replaced.");
        }

        private static void PreparePalette(PendingPalette palette,Texture2DArray diffuse,Texture2DArray specular,int stockCount,int available,
            List<(Texture2D diffuse,Texture2D specular)> textures,
            List<(PendingPalette source,Mesh mesh,VoxelMaterial[] rows)> prepared,bool allowPaint,Action<string> warn)
        {
            int start=textures.Count;
            Mesh? mesh=null;
            try
            {
                var materials=palette.Input.Materials!;
                if(materials.Count>available-start)
                    throw new InvalidOperationException("The session's custom texture slice limit is exhausted.");
                if(!allowPaint && materials.Any(material=>material.Paintable))
                    warn("Using fixed colors for "+palette.Key+": the native attachment paint contract is unavailable.");
                var rows=new VoxelMaterial[materials.Count];
                for(int index=0;index<rows.Length;index++)
                {
                    var source=materials[index];
                    int layer=start+index;
                    bool paintable=allowPaint && source.Paintable;
                    textures.Add(Encode(source,diffuse,paintable));
                    rows[index]=CreateRow(palette.Template.VoxelMaterials[source.FallbackMaterialSlot],source,paintable,
                        stockCount+layer,diffuse.depth+layer,specular.depth+layer);
                }
                mesh=AttachmentVisualResources.Prepare(palette.Input,palette.Template,palette.Key,useCustomPalette:true,
                    renderEnvelope:palette.Fallback.RenderEnvelope);
                prepared.Add((palette,mesh,rows));
            }
            catch(Exception exception)
            {
                if(mesh!=null) Object.Destroy(mesh);
                for(int index=start;index<textures.Count;index++)
                {
                    Object.Destroy(textures[index].diffuse);
                    Object.Destroy(textures[index].specular);
                }
                textures.RemoveRange(start,textures.Count-start);
                warn("Using baked material fallbacks for "+palette.Key+": "+exception.Message);
            }
        }

        private static VoxelMaterial CreateRow(VoxelMaterial row,VoxelVisualMaterial source,bool paintable,int id,int diffuse,int specular)
        {
            row.Id=id;
            row.TextureID=diffuse;
            row.specularID=specular;
            row.normalID=-1;
            row.BorderId=-1;
            row.borderNormalID=-1;
            row.borderSpecularID=-1;
            row.BorderMode=BorderMode.noBorder;
            row.AlphaMode=source.Emission==0 ? AlphaMode.noAlpha : AlphaMode.glowDiffuse;
            row.paintMode=paintable ? PaintMode.on : PaintMode.noPaint;
            row.dynDataVert=DynData_Vert.none;
            row.dynDataPixel=DynData_Pixel.none;
            row.uvProj=UV_Proj.off;
            row.Tiling=1;
            row.UVScale=Vector2.zero;
            row.recomputeNormal=RecomputeNormal.off;
            row.buildingOffset=BuildingOffset.off;
            row.fuelOverlay=FuelOverlay.off;
            return row;
        }

        private static (Texture2D diffuse,Texture2D specular) Encode(VoxelVisualMaterial material,Texture2DArray atlas,bool paintable)
        {
            int width=atlas.width, height=atlas.height;
            Color32[] albedo;
            if(material.Texture is { } source)
            {
                if(source==null || !source.isReadable || source.width!=width || source.height!=height)
                    throw new InvalidOperationException($"Expected a readable {width}x{height} albedo texture.");
                albedo=source.GetPixels32(0);
            }
            else
            {
                albedo=new Color32[width*height];
                Array.Fill(albedo,material.Color);
            }
            var packed=new Color32[albedo.Length];
            byte paintMask=paintable ? (byte)255 : (byte)0;
            for(int index=0;index<albedo.Length;index++)
            {
                byte mask=material.UseAlphaAsEmissionMask ? albedo[index].a : (byte)255;
                packed[index]=new Color32(0,0,paintMask,(byte)((material.Emission*mask+127)/255));
                albedo[index].a=255;
            }
            var diffuse=Compress(albedo,atlas);
            try { return (diffuse,Compress(packed,atlas)); }
            catch { Object.Destroy(diffuse); throw; }
        }

        private static Texture2D Compress(Color32[] pixels,Texture2DArray atlas)
        {
            var texture=new Texture2D(atlas.width,atlas.height,TextureFormat.RGBA32,true,true);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(true,false);
                texture.Compress(true);
                if(!texture.isReadable || texture.graphicsFormat!=atlas.graphicsFormat || texture.mipmapCount!=atlas.mipmapCount)
                    throw new InvalidOperationException("Unity did not produce readable BC3 voxel texture data with the native mip chain.");
                return texture;
            }
            catch { Object.Destroy(texture); throw; }
        }

        private static bool SupportsAtlas(Texture2DArray? atlas) => atlas!=null && atlas.isReadable && atlas.depth>0
            && atlas.width==256 && atlas.height==256 && atlas.mipmapCount==9
            && atlas.graphicsFormat==GraphicsFormat.RGBA_DXT5_UNorm;

        private static Texture2DArray Expand(Texture2DArray stock,IReadOnlyList<Texture2D> extra)
        {
            var mipLimit=new MipmapLimitDescriptor(!stock.ignoreMipmapLimit,stock.mipmapLimitGroup);
            var atlas=new Texture2DArray(stock.width,stock.height,stock.depth+extra.Count,
                stock.graphicsFormat,TextureCreationFlags.MipChain,stock.mipmapCount,mipLimit);
            try
            {
                if(atlas.graphicsFormat!=stock.graphicsFormat || atlas.mipmapCount!=stock.mipmapCount || !atlas.isReadable
                    || atlas.ignoreMipmapLimit!=stock.ignoreMipmapLimit
                    || !string.Equals(atlas.mipmapLimitGroup ?? "",stock.mipmapLimitGroup ?? "",StringComparison.Ordinal))
                    throw new InvalidOperationException("Unity did not create a compatible expanded voxel atlas.");
                atlas.name="Keel "+stock.name;
                atlas.wrapModeU=stock.wrapModeU;
                atlas.wrapModeV=stock.wrapModeV;
                atlas.wrapModeW=stock.wrapModeW;
                atlas.anisoLevel=stock.anisoLevel;
                atlas.filterMode=stock.filterMode;
                atlas.mipMapBias=stock.mipMapBias;
                for(int slice=0;slice<stock.depth;slice++)
                    for(int mip=0;mip<stock.mipmapCount;mip++)
                        CopyMip(stock.GetPixelData<byte>(mip,slice),atlas.GetPixelData<byte>(mip,slice));
                for(int slice=0;slice<extra.Count;slice++)
                    for(int mip=0;mip<stock.mipmapCount;mip++)
                        CopyMip(extra[slice].GetPixelData<byte>(mip),atlas.GetPixelData<byte>(mip,stock.depth+slice));
                // Stock decal loading also reads the diffuse atlas on the CPU.
                atlas.Apply(false,false);
                return atlas;
            }
            catch { Object.Destroy(atlas); throw; }
        }

        private static void CopyMip(NativeArray<byte> source,NativeArray<byte> target)
        {
            if(!source.IsCreated || !target.IsCreated || source.Length==0 || source.Length!=target.Length)
                throw new InvalidOperationException("The native voxel mip data has an unexpected layout.");
            source.CopyTo(target);
        }

        private static void Bind(Texture2DArray diffuse,Texture2DArray specular,VoxelMaterial[] rows,int cursor)
        {
            VoxelMaterialResources.SetMaterialParamDatas(rows);
            VoxelMaterialResources.MaterialCount=cursor;
            ResourcesLoader.VOXEL_DIFFUSE=diffuse;
            Shader.SetGlobalTexture(DiffuseName,diffuse);
            Shader.SetGlobalTexture(SpecularName,specular);
            if(!HasBindings(diffuse,specular,rows) || VoxelMaterialResources.MaterialCount!=cursor)
                throw new InvalidOperationException("The native material bindings did not retain the published values.");
        }

        private static bool HasBindings(Texture2DArray? diffuse,Texture2DArray? specular,VoxelMaterial[] rows)
            => diffuse!=null && specular!=null
                && diffuse==ResourcesLoader.VOXEL_DIFFUSE && diffuse==Shader.GetGlobalTexture(DiffuseName)
                && specular==Shader.GetGlobalTexture(SpecularName)
                && ReferenceEquals(rows,VoxelMaterialResources.materialParamDatas);

        private sealed class MaterialRestorationException : InvalidOperationException
        {
            internal MaterialRestorationException(Exception publication,Exception restoration)
                : base("The native material bindings could not be restored.",new AggregateException(publication,restoration)) { }
        }
    }
}
