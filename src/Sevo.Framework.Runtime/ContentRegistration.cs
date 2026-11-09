using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Sevo.Framework.Sdk;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    public sealed class ContentDefinition
    {
        public string Key { get; }
        public int Revision { get; }
        internal Func<int,object> Factory { get; }
        internal Func<string?>? PrepareTemplate { get; }
        internal ContentKind Kind { get; }
        internal ContentDefinition? OwnedRepeat { get; }
        internal bool IsFunctional => Kind is ContentKind.Functional or ContentKind.AttachmentReceiver;
        internal bool RequiresWeaponAttachments => Kind is ContentKind.AttachmentReceiver or ContentKind.Attachment;
        internal ContentDefinition(string key,int revision,Func<int,object> factory,Func<string?>? prepareTemplate,
            ContentKind kind,ContentDefinition? ownedRepeat=null)
        { Key=key; Revision=revision; Factory=factory; PrepareTemplate=prepareTemplate; Kind=kind; OwnedRepeat=ownedRepeat; }
    }

    internal enum ContentKind { Ordinary, Functional, AttachmentReceiver, Attachment, ReceiverRepeat, StorageConnector }

    public sealed class RegistrationCatalog
    {
        private readonly object gate = new object();
        private readonly Dictionary<string,ContentDefinition> definitions = new Dictionary<string,ContentDefinition>(StringComparer.Ordinal);
        private bool sealedCatalog;
        public void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory)
            => Register(ownerId,localName,revision,brickFactory,null,ContentKind.Ordinary);

        internal void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory,
            Func<string?>? prepareTemplate,ContentKind kind,ContentDefinition? ownedRepeat=null)
        {
            var key=ownerId+":"+localName;
            ContentAllocator.ValidateKey(key);
            if(revision<1 || brickFactory==null) throw new InvalidOperationException("A positive revision and factory are required.");
            if(ownedRepeat!=null) ContentAllocator.ValidateKey(ownedRepeat.Key);
            lock(gate)
            {
                if(sealedCatalog) throw new InvalidOperationException("Registration collection is closed; restart required.");
                if(definitions.ContainsKey(key)) throw new InvalidOperationException("Duplicate content key: "+key);
                if(ownedRepeat!=null && (ownedRepeat.Key==key || definitions.ContainsKey(ownedRepeat.Key)))
                    throw new InvalidOperationException("Duplicate content key: "+ownedRepeat.Key);
                definitions.Add(key,new ContentDefinition(key,revision,brickFactory,prepareTemplate,kind,ownedRepeat));
                if(ownedRepeat!=null) definitions.Add(ownedRepeat.Key,ownedRepeat);
            }
        }
        public IReadOnlyList<ContentDefinition> Seal()
        {
            lock(gate)
            {
                sealedCatalog=true;
                return Array.AsReadOnly(definitions.Values.Where(value=>value.Kind!=ContentKind.ReceiverRepeat)
                    .OrderBy(value=>value.Key,StringComparer.Ordinal).ToArray());
            }
        }
    }

    /// <summary>Call from a dependent BepInEx plugin's Awake. Factories run only in explicit managed experimental mode.</summary>
    public static class ContentRegistration
    {
        internal static RegistrationCatalog Catalog { get; } = new RegistrationCatalog();
        public static void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory)
            => Catalog.Register(ownerId,localName,revision,brickFactory);

        /// <summary>Declares a functional block using geometry from a mod-selected native template.</summary>
        /// <param name="selectTemplate">Read-only selector invoked after native brick resources are loaded.</param>
        public static void RegisterFunctionalBlock(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory)
            => RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,behaviorFactory,null,ContentKind.Functional);

        /// <summary>Declares a functional block in a named native Codex category.</summary>
        public static void RegisterFunctionalBlock(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,string codexCategory)
            => RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,behaviorFactory,null,ContentKind.Functional,
                codexCategory:RequireCodexCategory(codexCategory));

        /// <summary>Declares a functional receiver using a stock side-slot layout and an explicit set of compatible content keys.</summary>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory)
            => RegisterReceiver(ownerId,localName,revision,displayName,selectTemplate,compatibleAttachmentKeys,behaviorFactory,null);

        /// <summary>Declares a receiver with a custom host visual and the template's native repeat segments.</summary>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,Func<WeaponAttachmentVisual?> selectVisual)
            => RegisterReceiver(ownerId,localName,revision,displayName,selectTemplate,compatibleAttachmentKeys,behaviorFactory,
                selectVisual ?? throw new ArgumentNullException(nameof(selectVisual)));

        /// <summary>Declares a receiver and a hidden, separately identified repeat using the template's stock ring footprint.</summary>
        /// <param name="repeatLocalName">Permanent local content name for the repeat definition in the receiver's namespace.</param>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,Func<WeaponAttachmentVisual?>? selectVisual,
            string repeatLocalName,Func<WeaponAttachmentVisual?> selectRepeatVisual)
            => RegisterWeaponAttachmentReceiver(ownerId,localName,revision,displayName,selectTemplate,compatibleAttachmentKeys,
                behaviorFactory,selectVisual,repeatLocalName,selectRepeatVisual,modularSections:false);

        /// <summary>Optionally renders one full, unscaled visual section at each native repeat position.</summary>
        /// <param name="modularSections">Requires a repeat at every native bay and a valid full-section repeat visual.</param>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,Func<WeaponAttachmentVisual?>? selectVisual,
            string repeatLocalName,Func<WeaponAttachmentVisual?> selectRepeatVisual,bool modularSections)
        {
            if(string.IsNullOrWhiteSpace(repeatLocalName)) throw new ArgumentException("A repeat content name is required.",nameof(repeatLocalName));
            if(selectRepeatVisual==null) throw new ArgumentNullException(nameof(selectRepeatVisual));
            RegisterReceiver(ownerId,localName,revision,displayName,selectTemplate,compatibleAttachmentKeys,behaviorFactory,
                selectVisual,repeatLocalName,selectRepeatVisual,modularSections);
        }

        /// <summary>Declares a receiver in a named native Codex category, with optional host and repeat visuals.</summary>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,string codexCategory,
            Func<WeaponAttachmentVisual?>? selectVisual=null,string? repeatLocalName=null,
            Func<WeaponAttachmentVisual?>? selectRepeatVisual=null,bool modularSections=false)
        {
            if((repeatLocalName==null)!=(selectRepeatVisual==null) || (modularSections && repeatLocalName==null))
                throw new ArgumentException("An owned repeat requires both a name and a visual selector.",nameof(repeatLocalName));
            if(repeatLocalName!=null && string.IsNullOrWhiteSpace(repeatLocalName))
                throw new ArgumentException("A repeat content name is required.",nameof(repeatLocalName));
            RegisterReceiver(ownerId,localName,revision,displayName,selectTemplate,compatibleAttachmentKeys,behaviorFactory,
                selectVisual,repeatLocalName,selectRepeatVisual,modularSections,RequireCodexCategory(codexCategory));
        }

        private static void RegisterReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory,Func<WeaponAttachmentVisual?>? selectVisual,
            string? repeatLocalName=null,Func<WeaponAttachmentVisual?>? selectRepeatVisual=null,bool modularSections=false,
            string? codexCategory=null)
        {
            if (compatibleAttachmentKeys==null) throw new ArgumentNullException(nameof(compatibleAttachmentKeys));
            var keys=compatibleAttachmentKeys.Distinct(StringComparer.Ordinal).ToArray();
            if(keys.Length==0) throw new ArgumentException("At least one compatible attachment key is required.",nameof(compatibleAttachmentKeys));
            foreach(var key in keys) ContentAllocator.ValidateKey(key);
            RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,behaviorFactory,
                Array.AsReadOnly(keys),ContentKind.AttachmentReceiver,selectVisual,repeatLocalName,selectRepeatVisual,
                modularSections,codexCategory);
        }

        /// <summary>Declares a separately identified native side attachment using mod-selected stock geometry.</summary>
        public static void RegisterWeaponAttachment(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate)
            => RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,null,null,ContentKind.Attachment);

        /// <summary>Declares an attachment with optional caller-owned geometry and a stock placement and visual fallback.</summary>
        public static void RegisterWeaponAttachment(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,Func<WeaponAttachmentVisual?> selectVisual)
            => RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,null,null,ContentKind.Attachment,
                selectVisual ?? throw new ArgumentNullException(nameof(selectVisual)));

        /// <summary>Declares an attachment in a named native Codex category with optional custom geometry.</summary>
        public static void RegisterWeaponAttachment(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,string codexCategory,Func<WeaponAttachmentVisual?>? selectVisual=null)
            => RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,null,null,ContentKind.Attachment,
                selectVisual,codexCategory:RequireCodexCategory(codexCategory));

        internal static string RequireCodexCategory(string name) =>
            !string.IsNullOrWhiteSpace(name) ? name : throw new ArgumentException("A native Codex category name is required.","codexCategory");

        private static void RegisterPrepared(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,Func<FunctionalBlockContext,FunctionalBlockBehavior>? behaviorFactory,
            IReadOnlyCollection<string>? compatibleAttachmentKeys,ContentKind kind,Func<WeaponAttachmentVisual?>? selectVisual=null,
            string? repeatLocalName=null,Func<WeaponAttachmentVisual?>? selectRepeatVisual=null,bool modularSections=false,
            string? codexCategory=null)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.",nameof(displayName));
            if (selectTemplate==null) throw new ArgumentNullException(nameof(selectTemplate));
            if (kind!=ContentKind.Attachment && behaviorFactory==null) throw new ArgumentNullException(nameof(behaviorFactory));
            var key=ownerId+":"+localName;
            FunctionalStockTemplate? template=null;
            var repeatKey=repeatLocalName==null ? null : ownerId+":"+repeatLocalName;
            var ownedRepeat=repeatKey==null ? null : new ContentDefinition(repeatKey,revision,
                id=>(template ?? throw new InvalidOperationException("Native receiver was not prepared.")).CreateRepeat(id,displayName),
                null,ContentKind.ReceiverRepeat);
            Catalog.Register(ownerId,localName,revision,id=>
            {
                var prepared=template ?? throw new InvalidOperationException("Native template was not prepared.");
                var tab=Plugin.ResolveCodexCategory(codexCategory,UI.Codex.CodexTab.COMMANDS,key);
                if(kind==ContentKind.Attachment) return prepared.CreateAttachment(id,displayName,key,tab);
                var binding=new FunctionalBinding(key,id,behaviorFactory!,compatibleAttachmentKeys);
                return prepared.Create(id,displayName,binding,tab);
            },()=>
            {
                if(!FunctionalStockTemplate.TryPrepare(selectTemplate(),kind,out template,out var reason)) return reason;
                if(ownedRepeat!=null && !template!.TryPrepareRepeat(ownedRepeat.Key,selectRepeatVisual!,modularSections,out reason)) return reason;
                if(selectVisual!=null && (!modularSections || template!.UsesModularSections)) template!.PrepareVisual(key,selectVisual);
                return null;
            },kind,ownedRepeat);
        }
    }

    public interface INativeRegistry
    {
        int Capacity { get; }
        object? Read(int id);
        int IdOf(object definition);
        bool IsSupported(object definition);
    }

    internal interface IFunctionalNativeRegistry
    {
        bool IsSupportedPrepared(object definition,ContentKind kind);
    }

    /// <summary>Checks cooperating factories. It cannot sandbox arbitrary plugin code or reverse native side effects.</summary>
    public sealed class NativeRegistrationSession
    {
        private readonly INativeRegistry registry;
        private readonly Dictionary<int,object> owned = new Dictionary<int,object>();
        private bool attempted;
        public bool Failed { get; private set; }
        public IReadOnlyDictionary<int,object> Owned => new ReadOnlyDictionary<int,object>(owned);
        public NativeRegistrationSession(INativeRegistry registry) { this.registry=registry ?? throw new ArgumentNullException(nameof(registry)); }

        public void Commit(IReadOnlyList<ContentDefinition> definitions,ContentAllocator allocator,Action<string> persistRegistry)
        {
            if(attempted || Failed) throw new InvalidOperationException("Content registration cannot be retried in this process.");
            attempted=true;
            try
            {
                var ids=allocator.Apply(definitions.Select(definition=>new ContentRequest(definition.Key,ResourceKind.Brick,definition.Revision)));
                foreach(var id in ids.Values)
                    if(id<0 || id>=registry.Capacity || registry.Read(id)!=null) throw new InvalidOperationException("Reserved native slot became occupied: "+id);
                // Persist before any constructor can publish an ID into the native table.
                persistRegistry(allocator.ToJson());
                foreach(var definition in definitions)
                {
                    int id=ids[definition.Key];
                    var before=Enumerable.Range(0,registry.Capacity).Select(registry.Read).ToArray();
                    if(before[id]!=null) throw new InvalidOperationException("Native slot changed before factory: "+definition.Key);
                    var value=definition.Factory(id);
                    bool supported=value!=null && (definition.Kind==ContentKind.Ordinary
                        ? registry.IsSupported(value)
                        : registry is IFunctionalNativeRegistry templates && templates.IsSupportedPrepared(value,definition.Kind));
                    if(!supported || registry.IdOf(value!)!=id || !ReferenceEquals(registry.Read(id),value))
                        throw new InvalidOperationException("Factory did not register the expected stock brick: "+definition.Key);
                    if(registry.Capacity!=before.Length) throw new InvalidOperationException("Factory replaced the native registry capacity.");
                    for(int index=0;index<before.Length;index++)
                        if(index!=id && !ReferenceEquals(before[index],registry.Read(index)))
                            throw new InvalidOperationException("Factory changed an unreserved native slot: "+index);
                    owned.Add(id,value!);
                }
                VerifyOwnership();
            }
            catch(Exception exception)
            {
                Failed=true;
                throw new InvalidOperationException("Content registration failed; restart required. Native mutations were not rolled back.",exception);
            }
        }
        public void VerifyOwnership()
        {
            if(Failed) throw new InvalidOperationException("Content session already failed; restart required.");
            try
            {
                foreach(var pair in owned)
                    if(pair.Key>=registry.Capacity || !ReferenceEquals(registry.Read(pair.Key),pair.Value))
                        throw new InvalidOperationException("Managed native ID was overwritten: "+pair.Key);
            }
            catch { Failed=true; throw; }
        }
    }
}
