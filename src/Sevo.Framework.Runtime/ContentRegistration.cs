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
        internal bool IsFunctional => Kind is ContentKind.Functional or ContentKind.AttachmentReceiver;
        internal bool RequiresWeaponAttachments => Kind is ContentKind.AttachmentReceiver or ContentKind.Attachment;
        internal ContentDefinition(string key,int revision,Func<int,object> factory,Func<string?>? prepareTemplate,ContentKind kind)
        { Key=key; Revision=revision; Factory=factory; PrepareTemplate=prepareTemplate; Kind=kind; }
    }

    internal enum ContentKind { Ordinary, Functional, AttachmentReceiver, Attachment }

    public sealed class RegistrationCatalog
    {
        private readonly object gate = new object();
        private readonly Dictionary<string,ContentDefinition> definitions = new Dictionary<string,ContentDefinition>(StringComparer.Ordinal);
        private bool sealedCatalog;
        public void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory)
            => Register(ownerId,localName,revision,brickFactory,null,ContentKind.Ordinary);

        internal void Register(string ownerId,string localName,int revision,Func<int,object> brickFactory,
            Func<string?>? prepareTemplate,ContentKind kind)
        {
            var key=ownerId+":"+localName;
            ContentAllocator.ValidateKey(key);
            if(revision<1 || brickFactory==null) throw new InvalidOperationException("A positive revision and factory are required.");
            lock(gate)
            {
                if(sealedCatalog) throw new InvalidOperationException("Registration collection is closed; restart required.");
                if(definitions.ContainsKey(key)) throw new InvalidOperationException("Duplicate content key: "+key);
                definitions.Add(key,new ContentDefinition(key,revision,brickFactory,prepareTemplate,kind));
            }
        }
        public IReadOnlyList<ContentDefinition> Seal()
        {
            lock(gate)
            {
                sealedCatalog=true;
                return Array.AsReadOnly(definitions.Values.OrderBy(value=>value.Key,StringComparer.Ordinal).ToArray());
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

        /// <summary>Declares a functional receiver using a stock side-slot layout and an explicit set of compatible content keys.</summary>
        public static void RegisterWeaponAttachmentReceiver(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,IEnumerable<string> compatibleAttachmentKeys,
            Func<FunctionalBlockContext,FunctionalBlockBehavior> behaviorFactory)
        {
            if (compatibleAttachmentKeys==null) throw new ArgumentNullException(nameof(compatibleAttachmentKeys));
            var keys=compatibleAttachmentKeys.Distinct(StringComparer.Ordinal).ToArray();
            if(keys.Length==0) throw new ArgumentException("At least one compatible attachment key is required.",nameof(compatibleAttachmentKeys));
            foreach(var key in keys) ContentAllocator.ValidateKey(key);
            RegisterPrepared(ownerId,localName,revision,displayName,selectTemplate,behaviorFactory,
                Array.AsReadOnly(keys),ContentKind.AttachmentReceiver);
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

        private static void RegisterPrepared(string ownerId,string localName,int revision,string displayName,
            Func<Brick?> selectTemplate,Func<FunctionalBlockContext,FunctionalBlockBehavior>? behaviorFactory,
            IReadOnlyCollection<string>? compatibleAttachmentKeys,ContentKind kind,Func<WeaponAttachmentVisual?>? selectVisual=null)
        {
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.",nameof(displayName));
            if (selectTemplate==null) throw new ArgumentNullException(nameof(selectTemplate));
            if (kind!=ContentKind.Attachment && behaviorFactory==null) throw new ArgumentNullException(nameof(behaviorFactory));
            var key=ownerId+":"+localName;
            FunctionalStockTemplate? template=null;
            Catalog.Register(ownerId,localName,revision,id=>
            {
                var prepared=template ?? throw new InvalidOperationException("Native template was not prepared.");
                if(kind==ContentKind.Attachment) return prepared.CreateAttachment(id,displayName,key);
                var binding=new FunctionalBinding(key,id,behaviorFactory!,compatibleAttachmentKeys);
                return prepared.Create(id,displayName,binding);
            },()=>
            {
                if(!FunctionalStockTemplate.TryPrepare(selectTemplate(),kind,out template,out var reason)) return reason;
                if(selectVisual!=null) template!.PrepareVisual(key,selectVisual);
                return null;
            },kind);
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
