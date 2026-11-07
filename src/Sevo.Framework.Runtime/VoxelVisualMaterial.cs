using System;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A static native voxel surface with mod-owned albedo and an optional same-color glow.</summary>
    public sealed class VoxelVisualMaterial
    {
        /// <summary>Creates a solid RGB surface. The color's alpha is ignored.</summary>
        public VoxelVisualMaterial(int fallbackMaterialSlot,Color32 albedo,byte emission=0)
            : this(fallbackMaterialSlot,albedo,false,emission) { }

        /// <summary>Creates a solid surface. When paintable, the red channel supplies native grayscale shading.</summary>
        public VoxelVisualMaterial(int fallbackMaterialSlot,Color32 albedo,bool paintable,byte emission=0)
        {
            FallbackMaterialSlot=fallbackMaterialSlot;
            Color=albedo;
            Paintable=paintable;
            Emission=emission;
        }

        /// <summary>Creates a surface from a caller-owned, readable texture matching the game's voxel atlas dimensions.</summary>
        /// <param name="fallbackMaterialSlot">The baked slot in the selected stock template to use on rejection.</param>
        /// <param name="albedo">RGB in the game's gamma-encoded albedo convention. Keel reads mip zero during resource admission.</param>
        /// <param name="emission">Overall glow strength from zero to 255.</param>
        /// <param name="useAlphaAsEmissionMask">Whether each texel's alpha scales the glow strength.</param>
        public VoxelVisualMaterial(int fallbackMaterialSlot,Texture2D albedo,byte emission=0,bool useAlphaAsEmissionMask=false)
            : this(fallbackMaterialSlot,albedo,false,emission,useAlphaAsEmissionMask) { }

        /// <summary>Creates a textured surface. When paintable, red supplies native grayscale shading per texel.</summary>
        public VoxelVisualMaterial(int fallbackMaterialSlot,Texture2D albedo,bool paintable,byte emission=0,bool useAlphaAsEmissionMask=false)
        {
            FallbackMaterialSlot=fallbackMaterialSlot;
            Texture=albedo!=null ? albedo : throw new ArgumentNullException(nameof(albedo));
            Paintable=paintable;
            Emission=emission;
            UseAlphaAsEmissionMask=useAlphaAsEmissionMask;
        }

        public int FallbackMaterialSlot { get; }
        public bool Paintable { get; }
        public byte Emission { get; }
        public bool UseAlphaAsEmissionMask { get; }
        internal Color32 Color { get; }
        internal Texture2D? Texture { get; }
    }
}
