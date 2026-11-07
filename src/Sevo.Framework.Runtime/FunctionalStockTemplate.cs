using System;
using System.Linq;
using System.Runtime.CompilerServices;
using UI.Codex;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class FunctionalStockTemplate
    {
        private static readonly ConditionalWeakTable<Brick,FunctionalStockTemplate> Bindings =
            new ConditionalWeakTable<Brick,FunctionalStockTemplate>();

        private readonly Brick source;
        private readonly ResizeMode resizeMode;
        private readonly object[] recipe;

        private FunctionalStockTemplate(Brick source,ResizeMode resizeMode,object[] recipe)
        {
            this.source=source;
            this.resizeMode=resizeMode;
            this.recipe=recipe;
        }

        internal static bool TryPrepare(Brick? source,out FunctionalStockTemplate? template,out string reason)
        {
            template=null;
            if(source==null || source.brickID<0 || source.brickID>=Brick.BRICK_INDEX.Length
                || !ReferenceEquals(Brick.BRICK_INDEX[source.brickID],source))
            {
                reason="The selector did not return a registered native brick.";
                return false;
            }
            var type=source.GetType();
            bool staticCollider=type==typeof(Brick) || type.IsGenericType && type.GetGenericTypeDefinition()==typeof(CommandBrick<>)
                && type.GetField("isDynDataCollider")?.GetValue(source) is false;
            if(!staticCollider || source.isConveyorBelt || source.isPipeSeeThrough || source.isFuelSeeThrough
                || source.minGridSize<0 || source.maxGridSize<source.minGridSize)
            {
                reason="The template requires an unsupported collider, transport, or grid contract.";
                return false;
            }
            if(!StockTemplateResources.IsTemplateAvailable(source,out reason)) return false;
            var (mode,args)=StockTemplateResources.CopyRecipe(source);
            if(!(mode.unit>0f) || float.IsInfinity(mode.unit) || source.VoxelMaterials.Any(material=>
                material.dynDataVert!=DynData_Vert.none || material.dynDataPixel!=DynData_Pixel.none))
            {
                reason="The template requires an unsupported resize or dynamic material contract.";
                return false;
            }
            template=new FunctionalStockTemplate(source,mode,args);
            return true;
        }

        internal CommandBrick<KeelFunctionalCommand> Create(int id,string displayName,FunctionalBinding binding)
        {
            var brick=new CommandBrick<KeelFunctionalCommand>(id,displayName,source.nameMesh,resizeMode,recipe)
            {
                colliderShape=source.colliderShape,
                boundsOverride=source.boundsOverride,
                boundsCulling=source.boundsCulling,
                CollisionWidth=source.CollisionWidth,
                CollisionScaleFactor=source.CollisionScaleFactor,
                isDynamicGridSize=source.isDynamicGridSize,
                gridLevel=source.gridLevel,
                minGridSize=source.minGridSize,
                maxGridSize=source.maxGridSize,
                gridLockOffset=source.gridLockOffset,
                IsFacingForward=source.IsFacingForward,
                NoMirroring=source.NoMirroring,
                scaleUV=source.scaleUV,
                massForUnitShape=source.massForUnitShape,
                useMaterial=source.useMaterial,
                canPaint=source.canPaint,
                hasGlowPaint=source.hasGlowPaint,
                defaultPaint=source.defaultPaint,
                PreviewPaint=source.PreviewPaint
            };
            brick.SetCommandArgs(new object[]{binding});
            brick.Tab(CodexTab.COMMANDS,false);
            Bindings.Add(brick,this);
            return brick;
        }

        internal static bool IsSupported(Brick brick)
        {
            return Bindings.TryGetValue(brick,out var template) && brick.IsCommand
                && StockTemplateResources.IsSupported(brick,template.source);
        }

        internal static void Load(Brick brick)
        {
            if(!Bindings.TryGetValue(brick,out var template))
                throw new InvalidOperationException("Managed command brick has no prepared native template.");
            StockTemplateResources.Load(brick,template.source);
        }
    }
}
