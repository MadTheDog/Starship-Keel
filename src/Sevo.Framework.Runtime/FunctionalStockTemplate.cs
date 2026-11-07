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
        private readonly Brick? stockRepeat;
        private PreparedAttachmentVisual? visual;
        private FunctionalStockTemplate? repeatTemplate;
        private Brick? ownedRepeat;
        private Bounds? modularBay;
        internal bool UsesModularSections => modularBay.HasValue;
        private StockRepeatUse RepeatUse => kind switch
        {
            ContentKind.AttachmentReceiver => StockRepeatUse.Receiver,
            ContentKind.ReceiverRepeat => StockRepeatUse.Segment,
            _ => StockRepeatUse.None
        };

        private FunctionalStockTemplate(Brick source,ResizeMode resizeMode,object[] recipe,ContentKind kind)
        {
            this.source=source;
            this.resizeMode=resizeMode;
            this.recipe=recipe;
            this.kind=kind;
            if(kind==ContentKind.AttachmentReceiver) stockRepeat=StockTemplateResources.GetStockRepeat(source);
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
            var repeatUse=receiver ? StockRepeatUse.Receiver
                : kind==ContentKind.ReceiverRepeat ? StockRepeatUse.Segment : StockRepeatUse.None;
            if(!StockTemplateResources.IsTemplateAvailable(source,out reason,repeatUse)) return false;
            var (mode,args)=StockTemplateResources.CopyRecipe(source);
            if(!(mode.unit>0f) || float.IsInfinity(mode.unit) || source.VoxelMaterials.Any(material=>
                material.dynDataVert!=DynData_Vert.none || material.dynDataPixel!=DynData_Pixel.none))
            {
                reason="The template requires an unsupported resize or dynamic material contract.";
                return false;
            }
            if((receiver || kind is ContentKind.Attachment or ContentKind.ReceiverRepeat) && !SupportsAttachmentResize(mode,receiver))
            {
                reason="The receiver or attachment does not match the native side-slot resize rules.";
                return false;
            }
            template=new FunctionalStockTemplate(source,mode,args,kind);
            return true;
        }

        internal bool TryPrepareRepeat(string key,Func<WeaponAttachmentVisual?> selectVisual,bool modularSections,out string reason)
        {
            if(!Plugin.ReceiverRepeatsAvailable(out reason)) return false;
            if(stockRepeat==null)
            {
                reason="The selected receiver does not have a supported native repeat definition.";
                return false;
            }
            if(!TryPrepare(stockRepeat,ContentKind.ReceiverRepeat,out repeatTemplate,out reason)) return false;
            if(modularSections)
                PrepareModularSections(key,selectVisual);
            else
                repeatTemplate!.PrepareVisual(key,selectVisual);
            return true;
        }

        private void PrepareModularSections(string key,Func<WeaponAttachmentVisual?> selectVisual)
        {
            if(!TryGetSectionEnvelope(out var bay))
            {
                Plugin.ReportVisualFallback(key,"The template cannot render one grid-aligned repeat at every native section.");
                return;
            }
            if(!Plugin.TryEnableModularReceiverRendering(out var reason))
            {
                Plugin.ReportVisualFallback(key,reason);
                return;
            }
            var repeat=repeatTemplate!;
            repeat.PrepareVisual(key,selectVisual,bay);
            if(repeat.visual==null)
            {
                Plugin.ReportVisualFallback(key,"The modular layout needs a valid full-section mesh.");
                return;
            }
            modularBay=bay;
            repeat.modularBay=bay;
        }

        private bool TryGetSectionEnvelope(out Bounds bay)
        {
            bay=default;
            int unitStud=resizeMode.unitStud;
            if(stockRepeat==null || unitStud<=0 || source.repeatBrickStart!=0 || source.repeatBrickEndOffset!=0
                || source.VoxelMesh.renderData1.clusterCount<=0 || source.maxGridSize>byte.MaxValue)
                return false;
            var sectionOffset=new BrickInstanceData { gridPosition=new Vector3i(0,unitStud,0) };
            var localStep=sectionOffset.Position;
            if(localStep.x!=0f || localStep.z!=0f || !(localStep.y>0f) || float.IsInfinity(localStep.y))
                return false;
            var center=localStep*0.5f;
            for(int level=source.minGridSize;level<=source.maxGridSize;level++)
            {
                float scale=BrickInstanceData.GetGridSize(source,level);
                float step=scale*unitStud;
                int nativeStep=Mathf.RoundToInt(step);
                if(scale!=BrickInstanceData.GetGridSize(stockRepeat,level) || nativeStep<=0 || Mathf.Abs(nativeStep-step)>0.0001f)
                    return false;
                // The audited stock side-slot layout uses integer UnitGrid/2 along local Y.
                var socket=new BrickInstanceData
                {
                    brickId=checked((ushort)source.brickID),gridLevel=(byte)level,
                    gridPosition=new Vector3i(0,nativeStep/2,0)
                };
                if(socket.UnitGrid!=nativeStep || (nativeStep&1)!=0
                    || (socket.Position-center*scale).sqrMagnitude>1e-8f)
                    return false;
            }
            bay=new Bounds(center,Vector3.one*localStep.y);
            return true;
        }

        internal Brick CreateRepeat(int id,string displayName)
        {
            var repeat=repeatTemplate ?? throw new InvalidOperationException("The receiver repeat was not prepared.");
            var brick=new Brick(id,displayName,repeat.source.nameMesh,repeat.resizeMode,repeat.recipe)
            {
                isRepeatBrick=true
            };
            ownedRepeat=repeat.Complete(brick);
            return ownedRepeat;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SupportsAttachmentResize(ResizeMode mode,bool receiver) => receiver
            ? mode.canResize && mode.vertical && mode.maxScale.x==0 && mode.maxScale.z==0
            : !mode.canResize;

        internal Brick Create(int id,string displayName,FunctionalBinding binding,CodexTab tab) => kind==ContentKind.AttachmentReceiver
            ? CreateReceiver(id,displayName,binding,tab)
            : CreateCommand<KeelFunctionalCommand>(id,displayName,binding,tab);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsSupportedAttachmentSource(Brick source)
        {
            const BindingFlags fields=BindingFlags.DeclaredOnly|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            var type=source.GetType();
            return type==typeof(AddonBrick) && type.GetField("stackable",fields)?.GetValue(source) is false
                && Equals(type.GetField("addonType",fields)?.GetValue(source),AddonCommand.Type.weaponAddon) && source.forceLocalGrid;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Brick CreateReceiver(int id,string displayName,FunctionalBinding binding,CodexTab tab)
            => CreateCommand<WeaponAttachmentReceiverCommand>(id,displayName,binding,tab);

        private Brick CreateCommand<T>(int id,string displayName,FunctionalBinding binding,CodexTab tab) where T : Command,new()
        {
            var brick=new CommandBrick<T>(id,displayName,source.nameMesh,resizeMode,recipe);
            brick.SetCommandArgs(new object[]{binding});
            brick.Tab(tab,false);
            return Complete(brick);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal Brick CreateAttachment(int id,string displayName,string key,CodexTab tab)
        {
            var brick=new ManagedWeaponAttachmentBrick(id,displayName,source.nameMesh,resizeMode,recipe,key);
            brick.Tab(tab,false);
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
            if(repeatTemplate!=null && ownedRepeat==null)
                throw new InvalidOperationException("The receiver's repeat was not registered first.");
            brick.repeatBrickId=checked((ushort)(ownedRepeat?.brickID ?? stockRepeat?.brickID ?? 0));
            brick.repeatBrickStart=source.repeatBrickStart;
            brick.repeatBrickEndOffset=source.repeatBrickEndOffset;
        }

        internal static bool IsSupported(Brick brick,ContentKind kind)
        {
            return Bindings.TryGetValue(brick,out var template) && template.kind==kind
                && brick.IsCommand==(kind is ContentKind.Functional or ContentKind.AttachmentReceiver)
                && StockTemplateResources.IsSupported(brick,template.source,repeatUse:template.RepeatUse,ownedRepeat:template.ownedRepeat);
        }

        internal static bool TryLoad(Brick brick)
        {
            if(!Bindings.TryGetValue(brick,out var template)) return false;
            if(template.visual!=null)
                AttachmentVisualResources.Load(brick,template.source,template.visual,template.RepeatUse,template.ownedRepeat);
            else StockTemplateResources.Load(brick,template.source,repeatUse:template.RepeatUse,ownedRepeat:template.ownedRepeat);
            return true;
        }

        internal void PrepareVisual(string key,Func<WeaponAttachmentVisual?> selectVisual,Bounds? renderEnvelope=null)
            => visual=Plugin.PrepareAttachmentVisual(key,source,selectVisual,
                kind==ContentKind.ReceiverRepeat || kind==ContentKind.AttachmentReceiver && source.hasRepeatBrick,renderEnvelope);

        internal static void VerifyRepeatBinding(Brick brick)
        {
            if(Bindings.TryGetValue(brick,out var template) && template.kind==ContentKind.AttachmentReceiver
                && (!ReferenceEquals(StockTemplateResources.GetStockRepeat(template.source),template.stockRepeat)
                    || !StockTemplateResources.MatchesRepeat(brick,template.source,template.ownedRepeat)))
                throw new InvalidOperationException("The managed receiver's repeat binding changed: "+brick.brickID);
        }

        internal static bool IsReceiverRepeat(Brick host,int repeatId) =>
            Bindings.TryGetValue(host,out var template) && MatchesReceiverRepeat(host,repeatId,template);

        internal static bool ShouldUseReceiverRepeatRenderDefinition(Brick host,int repeatId) =>
            Bindings.TryGetValue(host,out var template) && (template.visual!=null || template.ownedRepeat!=null)
            && MatchesReceiverRepeat(host,repeatId,template);

        internal static bool TryGetModularReceiver(Brick host,out Brick repeat,out Bounds bay)
        {
            if(Bindings.TryGetValue(host,out var template) && template.kind==ContentKind.AttachmentReceiver
                && template.ownedRepeat is { } owned && template.modularBay is { } envelope
                && MatchesReceiverRepeat(host,owned.brickID,template))
            {
                repeat=owned;
                bay=envelope;
                return true;
            }
            repeat=null!;
            bay=default;
            return false;
        }

        internal static bool TryGetModularBay(Brick definition,out Bounds bay)
        {
            if(Bindings.TryGetValue(definition,out var template) && template.kind==ContentKind.ReceiverRepeat
                && template.modularBay is { } envelope)
            {
                bay=envelope;
                return true;
            }
            bay=default;
            return false;
        }

        internal static Brick? GetModularLegacyRepeat(BrickInstanceData data,BrickIds ids)
        {
            if(!data.IsValid || data.Brick?.isRepeatBrick!=true || ids.commandId<=0
                || CommandManager.GetCommand(ids.commandId) is not WeaponAttachmentReceiverCommand receiver)
                return null;
            var parent=receiver.brickData;
            return parent.Brick is { } host && TryGetModularReceiver(host,out var repeat,out _)
                && data.brickId!=repeat.brickID && ManagedWeaponAttachmentBrick.MatchesRepeatPlacement(parent,data) ? repeat : null;
        }

        private static bool MatchesReceiverRepeat(Brick host,int repeatId,FunctionalStockTemplate template) =>
            template.kind==ContentKind.AttachmentReceiver && repeatId>0 && repeatId<Brick.BRICK_INDEX.Length
            && host.repeatBrickId==(template.ownedRepeat?.brickID ?? template.stockRepeat?.brickID ?? 0)
            && (template.stockRepeat?.brickID==repeatId && ReferenceEquals(Brick.BRICK_INDEX[repeatId],template.stockRepeat)
                || template.ownedRepeat?.brickID==repeatId && ReferenceEquals(Brick.BRICK_INDEX[repeatId],template.ownedRepeat));
    }
}
