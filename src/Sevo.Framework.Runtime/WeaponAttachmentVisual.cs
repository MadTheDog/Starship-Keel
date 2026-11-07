using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A caller-owned readable mesh and one material slot per triangle.</summary>
    public sealed class WeaponAttachmentVisual
    {
        public WeaponAttachmentVisual(Mesh mesh, IEnumerable<int> triangleMaterialSlots)
        {
            Mesh = mesh != null ? mesh : throw new ArgumentNullException(nameof(mesh));
            if (triangleMaterialSlots == null) throw new ArgumentNullException(nameof(triangleMaterialSlots));
            TriangleMaterialSlots = Array.AsReadOnly(triangleMaterialSlots.Take(AttachmentVisualResources.MaxTriangles+1).ToArray());
        }

        /// <summary>Triangle slots index the supplied palette. Every entry declares a baked fallback.</summary>
        public WeaponAttachmentVisual(Mesh mesh,IEnumerable<int> triangleMaterialSlots,IEnumerable<VoxelVisualMaterial> materials)
            : this(mesh,triangleMaterialSlots)
        {
            if(materials==null) throw new ArgumentNullException(nameof(materials));
            Materials=Array.AsReadOnly(materials.Take(AttachmentMaterialResources.MaxPaletteSize+1).ToArray());
        }

        private WeaponAttachmentVisual(WeaponAttachmentVisual source, Color32? defaultPaint, Color32? previewPaint)
        {
            Mesh = source.Mesh;
            TriangleMaterialSlots = source.TriangleMaterialSlots;
            Materials = source.Materials;
            DefaultPaint = defaultPaint;
            PreviewPaint = previewPaint;
        }

        /// <summary>Returns a copy with native paint for new placement and item previews.</summary>
        /// <param name="defaultPaint">Initial placement and paint eraser color. Null inherits the stock template.</param>
        /// <param name="previewPaint">Item preview color. Null inherits the stock template.</param>
        /// <remarks>Native paint is per brick and visible on materials that consume it. Saved instance colors are preserved.</remarks>
        public WeaponAttachmentVisual WithPaint(Color32? defaultPaint = null, Color32? previewPaint = null) =>
            new WeaponAttachmentVisual(this, defaultPaint, previewPaint);

        public Mesh Mesh { get; }
        public IReadOnlyList<int> TriangleMaterialSlots { get; }
        public IReadOnlyList<VoxelVisualMaterial>? Materials { get; }
        public Color32? DefaultPaint { get; }
        public Color32? PreviewPaint { get; }
    }
}
