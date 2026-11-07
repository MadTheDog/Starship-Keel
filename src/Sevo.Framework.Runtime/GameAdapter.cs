using System;
using System.Collections.Generic;
using System.Linq;
using Manager;
using Sevo.Framework.Sdk;
using UI.Codex;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class GameAdapter : INativeRegistry, IFunctionalNativeRegistry
    {
        private Brick[]? originalTable;
        public int Capacity => Table.Length;
        private Brick[] Table
        {
            get
            {
                var table=Brick.BRICK_INDEX ?? throw new InvalidOperationException("Native brick table is missing.");
                if(originalTable==null) originalTable=table;
                if(!ReferenceEquals(originalTable,table)) throw new InvalidOperationException("Native brick table was replaced; restart required.");
                return table;
            }
        }
        public object? Read(int id) => Table[id];
        public int IdOf(object definition) => ((Brick)definition).brickID;
        public bool IsSupported(object definition)
        {
            return definition.GetType()==typeof(Brick) && !((Brick)definition).IsCommand
                && StockTemplateResources.IsSupported((Brick)definition,StockShape.HullTemplate(),legacyHull:true);
        }
        public bool IsSupportedPrepared(object definition,ContentKind kind) =>
            definition is Brick brick && FunctionalStockTemplate.IsSupported(brick,kind);

        public static bool IsStockHullAvailable(out string reason) =>
            StockTemplateResources.IsTemplateAvailable(TabHull.HULL_A,out reason);

        public void InitializeManagedResources(NativeRegistrationSession session)
        {
            session.VerifyOwnership();
            foreach (var brick in session.Owned.Values.Cast<Brick>().OrderBy(brick=>!brick.isRepeatBrick))
            {
                if (!FunctionalStockTemplate.TryLoad(brick))
                    StockTemplateResources.Load(brick,StockShape.HullTemplate(),legacyHull:true);
            }
        }
        public ResourceSpace[] AllocationSpaces(NativeLimits limits)
        {
            int capacity=Math.Min(Table.Length,limits.SafeBrickCapacity);
            if(capacity<=2 || !BrickCollider.Datas.IsCreated || BrickCollider.Datas.Length<capacity)
                throw new InvalidOperationException("Native collider/brick allocation capacity is unavailable.");
            var occupied=Enumerable.Range(0,capacity).Where(id=>Table[id]!=null).ToArray();
            // Collision templates are retained as a distinct persistence space, never allocated by this adapter.
            return new[]
            {
                new ResourceSpace(ResourceKind.Brick,capacity,new[]{0,1},occupied),
                new ResourceSpace(ResourceKind.CollisionShape,limits.ShapeCapacity,Array.Empty<int>(),Array.Empty<int>())
            };
        }
        public void ObserveBricks(RuntimeSnapshot snapshot)
        {
            snapshot.BrickCapacity=Table.Length;
            snapshot.OccupiedBricks=Table.Count(value=>value!=null);
        }
        public static void ObserveShapes(RuntimeSnapshot snapshot)
        {
            var table=ColliderShapeResources.GeometryDatas;
            snapshot.ShapeCapacity=table.IsCreated ? table.Length : 0;
            snapshot.OccupiedShapes=0;
            if(table.IsCreated)
                for(int index=0;index<table.Length;index++) if(table[index].vertexCount>0) snapshot.OccupiedShapes++;
        }
        public bool AuditResourceTablesReady()
        {
            return Brick.BrickProperties.IsCreated && Brick.BrickProperties.Length>=Table.Length
                && BrickCollider.Datas.IsCreated && BrickCollider.Datas.Length>=Table.Length
                && ColliderShapeResources.GeometryDatas.IsCreated && ColliderShapeResources.GeometryDatas.Length>0;
        }
        public void ValidateResources(NativeRegistrationSession session,NativeLimits limits,bool requireGeometry)
        {
            session.VerifyOwnership();
            if(!Brick.BrickProperties.IsCreated || !BrickCollider.Datas.IsCreated)
                throw new InvalidOperationException("Required native ID-indexed resources are unallocated.");
            foreach(var pair in session.Owned)
            {
                var brick=(Brick)pair.Value;
                FunctionalStockTemplate.VerifyRepeatBinding(brick);
                if(pair.Key>=limits.SafeBrickCapacity || pair.Key>=Brick.BrickProperties.Length || pair.Key>=BrickCollider.Datas.Length
                    || !Brick.BrickProperties[pair.Key].isValid || brick.VoxelMesh==null || brick.VoxelCollider==null
                    || brick.VoxelMaterials==null || brick.VoxelMaterials.Length==0
                    || brick.VoxelCollider.subShapes==null || brick.VoxelCollider.subShapes.Length!=1)
                    throw new InvalidOperationException("Managed brick native resources are incomplete or unsupported: "+pair.Key);
                if(requireGeometry)
                {
                    int shape=(int)brick.colliderShape;
                    if(!ColliderShapeResources.GeometryDatas.IsCreated || shape<0 || shape>=ColliderShapeResources.GeometryDatas.Length
                        || ColliderShapeResources.GeometryDatas[shape].vertexCount<=0)
                        throw new InvalidOperationException("Managed brick stock collision template is unavailable: "+pair.Key);
                }
            }
        }
    }

    /// <summary>Experimental factory helper for an ordinary cube using native hull geometry and raw material input.</summary>
    public static class StockShape
    {
        public static object CreateHull(int id,string displayName) => CreateHullInTab(id,displayName,CodexTab.HULL_BLOCKS);

        /// <summary>Creates an ordinary stock hull in a named native Codex category.</summary>
        public static object CreateHull(int id,string displayName,string codexCategory)
            => CreateHullInTab(id,displayName,Plugin.ResolveCodexCategory(
                ContentRegistration.RequireCodexCategory(codexCategory),CodexTab.HULL_BLOCKS,displayName));

        private static object CreateHullInTab(int id,string displayName,CodexTab tab)
        {
            var template=HullTemplate();
            var brick=new Brick(id,displayName,template.nameMesh,ResizeMode.BlockFull,"Hull/HullA");
            CopyHullFields(brick,template);
            brick.Tab(tab);
            return brick;
        }

        internal static Brick HullTemplate()
        {
            var template=TabHull.HULL_A ?? throw new InvalidOperationException("Stock hull declaration is unavailable.");
            if(string.IsNullOrWhiteSpace(template.nameMesh) || template.SubShapes!=null)
                throw new InvalidOperationException("Stock hull is no longer a supported single shape.");
            return template;
        }

        internal static void CopyHullFields(Brick brick,Brick template)
        {
            brick.colliderShape=template.colliderShape;
            brick.systemProfile=template.systemProfile;
            brick.massForUnitShape=template.massForUnitShape;
            brick.isDynamicGridSize=template.isDynamicGridSize;
            brick.minGridSize=template.minGridSize;
            brick.gridLevel=template.gridLevel;
            brick.useMaterial=template.useMaterial;
            brick.canPaint=template.canPaint;
            brick.defaultPaint=template.defaultPaint;
        }
    }
}
