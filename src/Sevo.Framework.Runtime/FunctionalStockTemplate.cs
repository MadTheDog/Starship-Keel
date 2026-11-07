using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UI.Codex;
using UnityEngine;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal sealed class FunctionalStockTemplate
    {
        private static readonly ConditionalWeakTable<Brick,FunctionalStockTemplate> Bindings =
            new ConditionalWeakTable<Brick,FunctionalStockTemplate>();
        private static HashSet<string> receiverTemplateTypes = new HashSet<string>(StringComparer.Ordinal);

        private readonly Brick source;
        private readonly ResizeMode resizeMode;
        private readonly object[] recipe;
        private readonly ContentKind kind;
        private Mesh? visual;

        private FunctionalStockTemplate(Brick source,ResizeMode resizeMode,object[] recipe,ContentKind kind)
        {
            this.source=source;
            this.resizeMode=resizeMode;
            this.recipe=recipe;
            this.kind=kind;
        }

        internal static bool DiscoverReceiverTemplates(ModuleDefinition module)
        {
            const string weaponType="VoxelEngine.SystemWeapon";
            receiverTemplateTypes=module.Types.Where(type=>!type.IsAbstract && type.Methods.Any(method=>
                method.Name=="OnInitialize" && method.HasBody && method.Body.Instructions.Any(instruction=>
                    instruction.OpCode.Code==Code.Call && instruction.Operand is MethodReference called
                    && called.DeclaringType.FullName==weaponType && called.Name=="SpawnSideAddon"
                    && called.HasThis && called.ReturnType.FullName=="System.Void" && called.Parameters.Count==1
                    && called.Parameters[0].ParameterType.FullName=="System.Boolean")))
                .Select(type=>type.FullName).ToHashSet(StringComparer.Ordinal);
            return receiverTemplateTypes.Count>0;
        }

        internal static bool TryPrepare(Brick? source,ContentKind kind,out FunctionalStockTemplate? template,out string reason)
        {
            template=null;
            if(source==null || source.brickID<0 || source.brickID>=Brick.BRICK_INDEX.Length
                || !ReferenceEquals(Brick.BRICK_INDEX[source.brickID],source))
            {
                reason="The selector did not return a registered native brick.";
                return false;
            }
            var type=source.GetType();
            bool stockCommand=type.IsGenericType && type.GetGenericTypeDefinition()==typeof(CommandBrick<>);
            bool staticCollider=type==typeof(Brick) || stockCommand && type.GetField("isDynDataCollider")?.GetValue(source) is false;
            if(kind==ContentKind.Attachment)
            {
                if(!IsSupportedAttachmentSource(source))
                {
                    reason="The template is not a native, nonstackable side attachment using local grid snapping.";
                    return false;
                }
                staticCollider=true;
            }
            bool receiver=kind==ContentKind.AttachmentReceiver;
            if(receiver && (!stockCommand || !receiverTemplateTypes.Contains(type.GetGenericArguments()[0].FullName ?? "")))
            {
                reason="The template does not declare the native side-slot layout.";
                return false;
            }
            if(!staticCollider || source.isConveyorBelt || source.isPipeSeeThrough || source.isFuelSeeThrough
                || source.minGridSize<0 || source.maxGridSize<source.minGridSize)
            {
                reason="The template requires an unsupported collider, transport, or grid contract.";
                return false;
            }
            if(!StockTemplateResources.IsTemplateAvailable(source,out reason,allowStockRepeat:receiver)) return false;
            var (mode,args)=StockTemplateResources.CopyRecipe(source);
            if(!(mode.unit>0f) || float.IsInfinity(mode.unit) || source.VoxelMaterials.Any(material=>
                material.dynDataVert!=DynData_Vert.none || material.dynDataPixel!=DynData_Pixel.none))
            {
                reason="The template requires an unsupported resize or dynamic material contract.";
                return false;
            }
            if((receiver || kind==ContentKind.Attachment) && !SupportsAttachmentResize(mode,receiver))
            {
                reason="The receiver or attachment does not match the native side-slot resize rules.";
                return false;
            }
            template=new FunctionalStockTemplate(source,mode,args,kind);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SupportsAttachmentResize(ResizeMode mode,bool receiver) => receiver
            ? mode.canResize && mode.vertical && mode.maxScale.x==0 && mode.maxScale.z==0
            : !mode.canResize;

        internal Brick Create(int id,string displayName,FunctionalBinding binding) => kind==ContentKind.AttachmentReceiver
            ? CreateReceiver(id,displayName,binding)
            : CreateCommand<KeelFunctionalCommand>(id,displayName,binding);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsSupportedAttachmentSource(Brick source)
        {
            const BindingFlags fields=BindingFlags.DeclaredOnly|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            var type=source.GetType();
            return type==typeof(AddonBrick) && type.GetField("stackable",fields)?.GetValue(source) is false
                && Equals(type.GetField("addonType",fields)?.GetValue(source),AddonCommand.Type.weaponAddon) && source.forceLocalGrid;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Brick CreateReceiver(int id,string displayName,FunctionalBinding binding)
            => CreateCommand<WeaponAttachmentReceiverCommand>(id,displayName,binding);

        private Brick CreateCommand<T>(int id,string displayName,FunctionalBinding binding) where T : Command,new()
        {
            var brick=new CommandBrick<T>(id,displayName,source.nameMesh,resizeMode,recipe);
            brick.SetCommandArgs(new object[]{binding});
            brick.Tab(CodexTab.COMMANDS,false);
            return Complete(brick);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal Brick CreateAttachment(int id,string displayName,string key)
        {
            var brick=new ManagedWeaponAttachmentBrick(id,displayName,source.nameMesh,resizeMode,recipe,key);
            brick.Tab(CodexTab.COMMANDS,false);
            return Complete(brick);
        }

        private Brick Complete(Brick brick)
        {
            brick.colliderShape=source.colliderShape;
            brick.boundsOverride=source.boundsOverride;
            brick.boundsCulling=source.boundsCulling;
            brick.CollisionWidth=source.CollisionWidth;
            brick.CollisionScaleFactor=source.CollisionScaleFactor;
            brick.isDynamicGridSize=source.isDynamicGridSize;
            brick.gridLevel=source.gridLevel;
            brick.minGridSize=source.minGridSize;
            brick.maxGridSize=source.maxGridSize;
            brick.gridLockOffset=source.gridLockOffset;
            brick.IsFacingForward=source.IsFacingForward;
            brick.NoMirroring=kind==ContentKind.Attachment || source.NoMirroring;
            brick.scaleUV=source.scaleUV;
            brick.massForUnitShape=source.massForUnitShape;
            brick.useMaterial=source.useMaterial;
            brick.canPaint=source.canPaint;
            brick.hasGlowPaint=source.hasGlowPaint;
            brick.defaultPaint=source.defaultPaint;
            brick.PreviewPaint=source.PreviewPaint;
            if(kind==ContentKind.AttachmentReceiver) CopyReceiverRepeat(brick);
            Bindings.Add(brick,this);
            return brick;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CopyReceiverRepeat(Brick brick)
        {
            brick.repeatBrickId=source.repeatBrickId;
            brick.repeatBrickStart=source.repeatBrickStart;
            brick.repeatBrickEndOffset=source.repeatBrickEndOffset;
        }

        internal static bool IsSupported(Brick brick,ContentKind kind)
        {
            return Bindings.TryGetValue(brick,out var template) && template.kind==kind
                && brick.IsCommand==(kind!=ContentKind.Attachment)
                && StockTemplateResources.IsSupported(brick,template.source,allowStockRepeat:kind==ContentKind.AttachmentReceiver);
        }

        internal static bool TryLoad(Brick brick)
        {
            if(!Bindings.TryGetValue(brick,out var template)) return false;
            if(template.visual!=null) AttachmentVisualResources.Load(brick,template.source,template.visual);
            else StockTemplateResources.Load(brick,template.source,allowStockRepeat:template.kind==ContentKind.AttachmentReceiver);
            return true;
        }

        internal void PrepareVisual(string key,Func<WeaponAttachmentVisual?> selectVisual)
            => visual=Plugin.PrepareAttachmentVisual(key,source,selectVisual);
    }
}
