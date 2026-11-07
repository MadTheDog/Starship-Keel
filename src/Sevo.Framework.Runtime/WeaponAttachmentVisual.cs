using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sevo.Framework.Runtime
{
    /// <summary>A caller-owned readable mesh and one baked template material slot per triangle.</summary>
    public sealed class WeaponAttachmentVisual
    {
        public WeaponAttachmentVisual(Mesh mesh, IEnumerable<int> triangleMaterialSlots)
        {
            Mesh = mesh != null ? mesh : throw new ArgumentNullException(nameof(mesh));
            if (triangleMaterialSlots == null) throw new ArgumentNullException(nameof(triangleMaterialSlots));
            TriangleMaterialSlots = Array.AsReadOnly(triangleMaterialSlots.Take(AttachmentVisualResources.MaxTriangles+1).ToArray());
        }

        public Mesh Mesh { get; }
        public IReadOnlyList<int> TriangleMaterialSlots { get; }
    }
}
