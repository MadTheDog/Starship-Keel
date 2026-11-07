using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public sealed class NativeLimits
    {
        public int PackedCapacity { get; internal set; }
        public int BrickPropertiesCapacity { get; internal set; }
        public int ColliderPrimaryCapacity { get; internal set; }
        public int ColliderDataCapacity { get; internal set; }
        public int ShapeCapacity { get; internal set; }
        public int SafeBrickCapacity => Math.Min(PackedCapacity, Math.Min(BrickPropertiesCapacity, Math.Min(ColliderPrimaryCapacity, ColliderDataCapacity)));
    }

    /// <summary>Reads metadata only. Identity values are never consulted for compatibility.</summary>
    public static partial class ContractAudit
    {
        private static MethodDefinition? Method(ModuleDefinition module, string type, string name, bool isStatic, string result, params string[] parameters)
            => module.GetType(type)?.Methods.SingleOrDefault(method => method.Name == name && method.IsStatic == isStatic
                && method.ReturnType.FullName == result && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));
        private static bool Field(ModuleDefinition module, string type, string name, string fieldType, bool isStatic)
            => module.GetType(type)?.Fields.Any(field => field.Name == name && field.FieldType.FullName == fieldType && field.IsStatic == isStatic && field.IsPublic) == true;
        private static bool IsCall(Instruction instruction, string type, string name)
            => (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                && instruction.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name
                && method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Void";
        private static int UniqueCall(MethodDefinition method, string type, string name)
        {
            if (!method.HasBody) throw new InvalidOperationException("Missing body: " + method.FullName);
            var matches = method.Body.Instructions.Select((instruction,index) => new {instruction,index}).Where(item => IsCall(item.instruction,type,name)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Missing or ambiguous call: " + type + "." + name);
            return matches[0].index;
        }
        private static int Constant(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldc_I4: return (int)instruction.Operand;
                case Code.Ldc_I4_S: return Convert.ToInt32(instruction.Operand);
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                default: throw new InvalidOperationException("Expected validated constant capacity.");
            }
        }
        private static int PackedCapacity(ModuleDefinition module)
        {
            var field = module.GetType("VoxelEngine.BrickIndex")?.Fields.SingleOrDefault(value => value.Name == "BRICK_MASK" && value.IsLiteral && value.FieldType.FullName == "System.Int32");
            var method = Method(module,"VoxelEngine.BrickIndex","GetBrickID",true,"System.Int32","System.UInt64");
            if (!(field?.Constant is int mask) || mask < 1 || mask >= 1_000_000 || (mask & (mask + 1)) != 0 || method?.HasBody != true)
                throw new InvalidOperationException("Packed ID mask/signature unavailable.");
            var instructions = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
            if (instructions.Length != 6 || instructions[0].OpCode.Code != Code.Ldarg_0 || Constant(instructions[1]) != mask
                || instructions[2].OpCode.Code != Code.Conv_I8 || instructions[3].OpCode.Code != Code.And
                || instructions[4].OpCode.Code != Code.Conv_I4 || instructions[5].OpCode.Code != Code.Ret)
                throw new InvalidOperationException("Packed ID extraction no longer matches its mask.");
            return mask + 1;
        }
        private static int StoredNativeCapacity(MethodDefinition method, string fieldName, string elementType)
        {
            var instructions = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
            var stores = instructions.Select((instruction,index) => new {instruction,index}).Where(item => item.instruction.OpCode.Code == Code.Stsfld
                && item.instruction.Operand is FieldReference field && field.DeclaringType.FullName == method.DeclaringType.FullName && field.Name == fieldName).ToArray();
            if (stores.Length != 1 || stores[0].index < 4) throw new InvalidOperationException("Native allocation store changed: " + fieldName);
            int index = stores[0].index;
            if (instructions[index-1].OpCode.Code != Code.Newobj || !(instructions[index-1].Operand is MethodReference ctor)
                || ctor.DeclaringType.FullName != "Unity.Collections.NativeArray`1<" + elementType + ">"
                || !ctor.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[]{"System.Int32","Unity.Collections.Allocator","Unity.Collections.NativeArrayOptions"}))
                throw new InvalidOperationException("Native capacity constructor changed: " + fieldName);
            int capacity = Constant(instructions[index-4]);
            Constant(instructions[index-3]); Constant(instructions[index-2]);
            if (capacity < 1 || capacity > 1_000_000) throw new InvalidOperationException("Unsafe native capacity: " + fieldName);
            return capacity;
        }
        private static int ColliderPrimaryCapacity(MethodDefinition method)
        {
            var instructions = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
            var store = instructions.Select((instruction,index) => new {instruction,index}).Single(item => item.instruction.OpCode.Code == Code.Stsfld
                && item.instruction.Operand is FieldReference field && field.DeclaringType.FullName == "VoxelEngine.BrickCollider" && field.Name == "ShapeCount");
            var capacity = Constant(instructions[store.index-1]);
            if (capacity < 1 || capacity > 1_000_000) throw new InvalidOperationException("Invalid collider primary partition.");
            return capacity;
        }
        public static NativeLimits ReadLimits(ModuleDefinition module)
        {
            var brick = Method(module,"VoxelEngine.Brick","LoadAllResourcesRuntime",true,"System.Void") ?? throw new InvalidOperationException("Missing brick loader.");
            var collider = Method(module,"VoxelEngine.BrickCollider","AllocateResources",true,"System.Void") ?? throw new InvalidOperationException("Missing collider allocator.");
            var shapes = Method(module,"Manager.ColliderShapeResources","AllocateResources",true,"System.Void") ?? throw new InvalidOperationException("Missing geometry allocator.");
            return new NativeLimits
            {
                PackedCapacity = PackedCapacity(module),
                BrickPropertiesCapacity = StoredNativeCapacity(brick,"BrickProperties","VoxelEngine.BrickProperties"),
                ColliderPrimaryCapacity = ColliderPrimaryCapacity(collider),
                ColliderDataCapacity = StoredNativeCapacity(collider,"Datas","VoxelEngine.BrickCollider/Data"),
                ShapeCapacity = StoredNativeCapacity(shapes,"GeometryDatas","Manager.ColliderShapeCommon/GeoIndexData")
            };
        }

        public static IReadOnlyDictionary<string,CapabilityResult> Evaluate(ModuleDefinition module)
        {
            var results = new Dictionary<string,CapabilityResult>(StringComparer.Ordinal);
            void Check(string name, Action validate, string success)
            {
                try { validate(); results.Add(name,new CapabilityResult(name,true,success)); }
                catch (Exception exception) { results.Add(name,new CapabilityResult(name,false,exception.Message)); }
            }
            Check("packedIds",()=>{ PackedCapacity(module); },"Packed extraction matches its declared mask.");
            Check("brickRegistry",()=>
            {
                if (!Field(module,"VoxelEngine.Brick","BRICK_INDEX","VoxelEngine.Brick[]",true)
                    || !Field(module,"VoxelEngine.Brick","brickID","System.Int32",false)
                    || Method(module,"VoxelEngine.Brick",".ctor",false,"System.Void","System.Int32","System.String","System.String","VoxelEngine.ResizeMode","System.Object[]")?.IsPublic != true)
                    throw new InvalidOperationException("Required brick registry/constructor signatures changed.");
            },"Brick registry and constructor contracts present.");
            Check("lifecycle",()=>
            {
                var awake=Method(module,"ResourcesLoader","Awake",false,"System.Void") ?? throw new InvalidOperationException("Missing ResourcesLoader.Awake.");
                foreach(var target in new[]{("VoxelEngine.Brick","LoadAllResourcesRuntime"),("VoxelEngine.BrickCollider","AllocateResources"),("Manager.GameResourcesManager","AllocateResources"),("Manager.GameResourcesManager","OnResourceLoaded"),("Manager.GameResourcesManager","OnGameReload"),("Manager.GameResourcesManager","ReleaseResources")})
                    if(Method(module,target.Item1,target.Item2,true,"System.Void")==null) throw new InvalidOperationException("Missing lifecycle signature: "+target);
                int collider=UniqueCall(awake,"VoxelEngine.BrickCollider","AllocateResources"), bricks=UniqueCall(awake,"VoxelEngine.Brick","LoadAllResourcesRuntime"), allocate=UniqueCall(awake,"Manager.GameResourcesManager","AllocateResources");
                int reload=UniqueCall(awake,"Manager.GameResourcesManager","OnGameReload"), ready=UniqueCall(awake,"Manager.GameResourcesManager","OnResourceLoaded");
                if (!(reload < ready && ready < collider && collider < bricks && bricks < allocate)
                    || !awake.Body.Instructions.Skip(ready+1).Take(collider-ready-1).Any(instruction=>instruction.OpCode.Code==Code.Ret))
                    throw new InvalidOperationException("Initial/reload lifecycle ordering changed.");
                var allocator=Method(module,"Manager.GameResourcesManager","AllocateResources",true,"System.Void")!;
                UniqueCall(allocator,"Manager.GameResourcesManager","OnResourceLoaded");
                var body=awake.Body.Instructions;
                if(!body.Skip(allocate+1).Any(instruction=>instruction.OpCode.Code==Code.Stsfld && instruction.Operand is FieldReference field && field.DeclaringType.FullName=="ResourcesLoader" && field.Name=="codexLoaded"))
                    throw new InvalidOperationException("Resource completion publication changed.");
            },"Stock declaration, collider, brick, allocation and reload call relationships validated.");
            Check("nativeResources",()=>
            {
                if (!Field(module,"VoxelEngine.Brick","BrickProperties","Unity.Collections.NativeArray`1<VoxelEngine.BrickProperties>",true)
                    || !Field(module,"VoxelEngine.BrickProperties","isValid","System.Boolean",false)
                    || !Field(module,"VoxelEngine.BrickCollider","Datas","Unity.Collections.NativeArray`1<VoxelEngine.BrickCollider/Data>",true)
                    || !Field(module,"Manager.ColliderShapeResources","GeometryDatas","Unity.Collections.NativeArray`1<Manager.ColliderShapeCommon/GeoIndexData>",true)
                    || Method(module,"VoxelEngine.Brick","get_VoxelMesh",false,"VoxelEngine.VoxelMesh")?.IsPublic!=true
                    || Method(module,"VoxelEngine.Brick","get_VoxelCollider",false,"VoxelEngine.VoxelItemCollider")?.IsPublic!=true
                    || Method(module,"VoxelEngine.Brick","get_VoxelMaterials",false,"VoxelEngine.VoxelMaterial[]")?.IsPublic!=true)
                    throw new InvalidOperationException("Required native resource field/getter contract changed.");
                var brick=Method(module,"VoxelEngine.Brick","LoadAllResourcesRuntime",true,"System.Void")!;
                var collider=Method(module,"VoxelEngine.BrickCollider","AllocateResources",true,"System.Void")!;
                StoredNativeCapacity(brick,"BrickProperties","VoxelEngine.BrickProperties");
                StoredNativeCapacity(collider,"Datas","VoxelEngine.BrickCollider/Data"); ColliderPrimaryCapacity(collider);
            },"Native ID-indexed buffer allocation and readiness contracts validated.");
            Check("shapeAudit",()=>
            {
                if (!Field(module,"Manager.ColliderShapeResources","GeometryDatas","Unity.Collections.NativeArray`1<Manager.ColliderShapeCommon/GeoIndexData>",true)) throw new InvalidOperationException("Geometry table signature changed.");
                var geo=module.GetType("Manager.ColliderShapeCommon")?.NestedTypes.SingleOrDefault(t=>t.Name=="GeoIndexData");
                if(geo?.Fields.Any(f=>f.Name=="vertexCount" && f.FieldType.FullName=="System.Int32" && f.IsPublic)==false || geo==null) throw new InvalidOperationException("Geometry occupancy contract changed.");
                StoredNativeCapacity(Method(module,"Manager.ColliderShapeResources","AllocateResources",true,"System.Void")!,"GeometryDatas","Manager.ColliderShapeCommon/GeoIndexData");
            },"Collision-template occupancy contract validated; custom geometry is unsupported.");
            Check("stockHull",()=>
            {
                var requiredFields=new[]
                {
                    ("VoxelEngine.TabHull","HULL_A","VoxelEngine.Brick",true),
                    ("VoxelEngine.ResizeMode","BlockFull","VoxelEngine.ResizeMode",true),
                    ("UI.Codex.CodexTab","HULL_BLOCKS","UI.Codex.CodexTab",true),
                    ("VoxelEngine.Brick","nameMesh","System.String",false),
                    ("VoxelEngine.Brick","SubShapes","VoxelEngine.SubShape[]",false),
                    ("VoxelEngine.Brick","colliderShape","Manager.ColliderShapeCommon/Shape",false),
                    ("VoxelEngine.Brick","systemProfile","VoxelEngine.SystemProfile",false),
                    ("VoxelEngine.Brick","massForUnitShape","System.Single",false),
                    ("VoxelEngine.Brick","isDynamicGridSize","System.Boolean",false),
                    ("VoxelEngine.Brick","minGridSize","System.Int32",false),
                    ("VoxelEngine.Brick","gridLevel","System.Int32",false),
                    ("VoxelEngine.Brick","useMaterial","System.Boolean",false),
                    ("VoxelEngine.Brick","canPaint","System.Boolean",false),
                    ("VoxelEngine.Brick","defaultPaint","UnityEngine.Color32",false),
                    ("VoxelEngine.VoxelItemCollider","subShapes","VoxelEngine.SubShape[]",false)
                };
                foreach(var field in requiredFields)
                    if(!Field(module,field.Item1,field.Item2,field.Item3,field.Item4)) throw new InvalidOperationException("Stock-hull dependency changed: "+field.Item1+"."+field.Item2);
                if(Method(module,"VoxelEngine.Brick","Tab",false,"VoxelEngine.Brick","UI.Codex.CodexTab","System.Boolean")?.IsPublic!=true
                    || Method(module,"VoxelEngine.Brick","get_IsCommand",false,"System.Boolean")?.IsPublic!=true)
                    throw new InvalidOperationException("Stock-hull tab or command-identity signature changed.");
            },"Stock-hull example binding contracts available; assets are validated during native initialization.");
            var materials=StockTemplateResources.Evaluate(module);
            results.Add(materials.Name,materials);
            return results;
        }
    }
}
