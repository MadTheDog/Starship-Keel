using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Audits the optional render-only substitution against the native mesh pipeline.</summary>
        public static CapabilityResult EvaluateAttachmentVisuals(ModuleDefinition module)
        {
            const string capability = "attachmentVisuals";
            const string brick = "VoxelEngine.Brick", voxelMesh = "VoxelEngine.VoxelMesh", unityMesh = "UnityEngine.Mesh";
            const string collider = "VoxelEngine.VoxelItemCollider", brickCollider = "VoxelEngine.BrickCollider";
            const string properties = "VoxelEngine.BrickProperties", buffers = "VoxelEngine.BrickMeshResources";
            const string shape = buffers + "/ShapeMeshData", pass = voxelMesh + "/PassIndexingData";
            const string meshBuffer = "Common.MeshBuffer", offset = "VoxelEngine.Offset", material = "VoxelEngine.VoxelMaterial";
            const string bounds = "UnityEngine.Bounds", vector2 = "UnityEngine.Vector2", vector3 = "UnityEngine.Vector3";
            const string vector4 = "UnityEngine.Vector4", gpuBuffer = "UnityEngine.ComputeBuffer";
            const string v = "System.Void", i = "System.Int32", f = "System.Single", s = "System.String", b = "System.Boolean";
            string ListOf(string type) => "System.Collections.Generic.List`1<" + type + ">";

            try
            {
                MethodDefinition Need(string owner, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, owner, name, isStatic, result, parameters)
                    ?? throw new InvalidOperationException("Missing attachment visual signature: " + owner + "." + name);

                Instruction[] Body(MethodDefinition method) => method.HasBody
                    ? method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray()
                    : throw new InvalidOperationException("Missing attachment visual method body: " + method.FullName);

                bool FieldUse(Instruction instruction, Code code, string owner, string name) =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == owner && field.Name == name;

                bool Calls(Instruction instruction, string owner, string name) =>
                    (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                    && instruction.Operand is MethodReference called && called.DeclaringType.FullName == owner && called.Name == name;

                bool GenericCall(Instruction instruction, string owner, string name, string argument) =>
                    Calls(instruction, owner, name) && instruction.Operand is GenericInstanceMethod called
                    && called.GenericArguments.Count == 1 && called.GenericArguments[0].FullName == argument;

                bool Codes(Instruction[] body, int start, params Code[] codes) =>
                    start >= 0 && start + codes.Length <= body.Length
                    && codes.Select((code, index) => body[start + index].OpCode.Code == code).All(match => match);

                int[] Indices(Instruction[] body, Func<Instruction, bool> match) =>
                    body.Select((instruction, index) => (instruction, index)).Where(entry => match(entry.instruction))
                        .Select(entry => entry.index).ToArray();

                int One(Instruction[] body, Func<Instruction, bool> match, string description)
                {
                    var matches = Indices(body, match);
                    if (matches.Length != 1)
                        throw new InvalidOperationException("Missing or ambiguous attachment visual site: " + description);
                    return matches[0];
                }

                bool Literal(Instruction instruction, string text) =>
                    instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, text);

                bool New(Instruction instruction, MethodDefinition constructor) =>
                    instruction.OpCode.Code == Code.Newobj && instruction.Operand is MethodReference method
                    && method.FullName == constructor.FullName;

                void CheckEnum(string owner, string name, int expected)
                {
                    var type = module.GetType(owner);
                    if (type?.IsEnum != true || type.Fields.SingleOrDefault(field => field.Name == name && field.IsLiteral)?.Constant
                        is not int value || value != expected)
                        throw new InvalidOperationException("Native attachment visual enum changed: " + owner + "." + name);
                }

                void NeedOrder(params int[] indices)
                {
                    for (int index = 1; index < indices.Length; index++)
                        if (indices[index - 1] >= indices[index])
                            throw new InvalidOperationException("Native attachment visual initialization order changed.");
                }

                bool LoadMesh(Instruction instruction) =>
                    GenericCall(instruction, "UnityEngine.Resources", "Load", unityMesh)
                    && instruction.Operand is MethodReference method
                    && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == s;

                var constructor = Need(voxelMesh, ".ctor", false, v, s, brick, f, "VoxelEngine.ResizeMode", b, b);
                var ctor = Body(constructor);
                int ownerStore = One(ctor, instruction => FieldUse(instruction, Code.Stfld, voxelMesh, "brick"), "mesh owner");
                int main = One(ctor, instruction => FieldUse(instruction, Code.Stfld, voxelMesh, "Mesh"), "main mesh store");
                int gizmo = One(ctor, instruction => FieldUse(instruction, Code.Stfld, voxelMesh, "MeshGizmo"), "gizmo mesh store");
                var previewStores = Indices(ctor, instruction => FieldUse(instruction, Code.Stfld, voxelMesh, "MeshPreview"));
                if (!constructor.IsPublic || !Codes(ctor, ownerStore - 2, Code.Ldarg_0, Code.Ldarg_2) || previewStores.Length != 2)
                    throw new InvalidOperationException("Native mesh owner or preview store contract changed.");
                int preview = previewStores[0], clone = previewStores[1];
                void StockLoad(int store, string? suffix)
                {
                    int path = store - (suffix == null ? 3 : 4);
                    if (path < 2 || !Codes(ctor, path - 2, Code.Ldarg_0, Code.Ldstr, Code.Ldarg_1)
                        || !Literal(ctor[path - 1], "Shapes/") || suffix != null && !Literal(ctor[path + 1], suffix)
                        || !Calls(ctor[store - 2], "System.String", "Concat") || !LoadMesh(ctor[store - 1])
                        || ctor[store].Previous != ctor[store - 1])
                        throw new InvalidOperationException("Native stock mesh load no longer feeds the expected store directly.");
                }
                StockLoad(main, null);
                StockLoad(preview, "_preview");
                StockLoad(gizmo, "_gizmo");
                foreach (var site in new[] { ctor[main], ctor[preview] })
                    if (ctor.Any(instruction => ReferenceEquals(instruction.Operand, site)
                        || instruction.Operand is Instruction[] targets && targets.Contains(site))
                        || constructor.Body.ExceptionHandlers.Any(handler => ReferenceEquals(handler.TryStart, site)
                            || ReferenceEquals(handler.TryEnd, site) || ReferenceEquals(handler.HandlerStart, site)
                            || ReferenceEquals(handler.HandlerEnd, site) || ReferenceEquals(handler.FilterStart, site)))
                        throw new InvalidOperationException("Native control flow enters a mesh substitution store directly.");
                if (preview + 10 != clone || clone + 2 >= ctor.Length
                    || !Codes(ctor, preview + 1, Code.Ldarg_0, Code.Ldfld, Code.Ldnull, Code.Call)
                    || !FieldUse(ctor[preview + 2], Code.Ldfld, voxelMesh, "MeshPreview")
                    || !Calls(ctor[preview + 4], "UnityEngine.Object", "op_Equality")
                    || (ctor[preview + 5].OpCode.Code != Code.Brfalse && ctor[preview + 5].OpCode.Code != Code.Brfalse_S)
                    || !ReferenceEquals(ctor[preview + 5].Operand, ctor[clone + 1])
                    || !Codes(ctor, clone - 4, Code.Ldarg_0, Code.Ldarg_0, Code.Ldfld, Code.Call, Code.Stfld)
                    || !FieldUse(ctor[clone - 2], Code.Ldfld, voxelMesh, "Mesh")
                    || !GenericCall(ctor[clone - 1], "UnityEngine.Object", "Instantiate", unityMesh)
                    || !Calls(ctor[clone + 2], voxelMesh, "OffsetMeshPreviewUV"))
                    throw new InvalidOperationException("Native preview no longer clones the main mesh before editing its UVs.");

                var meshBufferConstructor = Need(meshBuffer, ".ctor", false, v, unityMesh);
                int collect = One(ctor, instruction => New(instruction, meshBufferConstructor), "mesh buffer construction");
                int faceIds = One(ctor, instruction => Calls(instruction, offset, "GetFaceOffset"), "face offset calculation");
                int meshBounds = One(ctor, instruction => FieldUse(instruction, Code.Stfld, voxelMesh, "bounds"), "render bounds");
                var passes = Indices(ctor, instruction => Calls(instruction, voxelMesh, "PushMeshData"));
                if (collect == 0 || !FieldUse(ctor[collect - 1], Code.Ldfld, voxelMesh, "Mesh")
                    || meshBounds < 2 || !FieldUse(ctor[meshBounds - 2], Code.Ldfld, voxelMesh, "Mesh")
                    || !Calls(ctor[meshBounds - 1], unityMesh, "get_bounds")
                    || faceIds + 1 >= ctor.Length || !FieldUse(ctor[faceIds + 1], Code.Stfld, voxelMesh, "faceIDList")
                    || passes.Length == 0)
                    throw new InvalidOperationException("Native render buffer no longer consumes the substituted mesh.");
                NeedOrder(ownerStore, main, preview, clone, gizmo, collect, faceIds, meshBounds, passes[0]);

                const string alphaMode = "VoxelEngine.AlphaMode", renderPass = "VoxelEngine.RenderablePass";
                const string dynamicVertex = "VoxelEngine.DynData_Vert", dynamicPixel = "VoxelEngine.DynData_Pixel";
                CheckEnum(alphaMode, "noAlpha", 0);
                CheckEnum(alphaMode, "glowPaint", 4);
                CheckEnum(alphaMode, "glowDiffuse", 3);
                CheckEnum(dynamicVertex, "none", 0);
                CheckEnum(dynamicPixel, "none", 0);
                CheckEnum(renderPass, "opaque", 0);
                if (!Field(module, material, "AlphaMode", alphaMode, false)
                    || !Field(module, material, "dynDataVert", dynamicVertex, false)
                    || !Field(module, material, "dynDataPixel", dynamicPixel, false)
                    || passes[0] < 1 || passes[0] + 1 >= ctor.Length || Constant(ctor[passes[0] - 1]) != 0
                    || !FieldUse(ctor[passes[0] + 1], Code.Stfld, voxelMesh, "opaqueData"))
                    throw new InvalidOperationException("Native static opaque material fields or pass construction changed.");

                var passFilter = Body(Need(voxelMesh, "AddToShapeData", false, b, renderPass, material));
                var opaqueGuard = Indices(passFilter, instruction => instruction.OpCode.Code is Code.Brtrue or Code.Brtrue_S)
                    .Where(index => index > 0 && Codes(passFilter, index - 1, Code.Ldarg_1)).ToArray();
                if (opaqueGuard.Length != 1)
                    throw new InvalidOperationException("Native opaque pass dispatch changed.");
                int opaque = opaqueGuard[0];
                int dispatch = 0;
                while (dispatch < opaque - 1)
                {
                    if (dispatch + 2 >= passFilter.Length || !Codes(passFilter, dispatch, Code.Ldarg_1)
                        || Constant(passFilter[dispatch + 1]) == 0
                        || passFilter[dispatch + 2].OpCode.Code is not (Code.Bne_Un or Code.Bne_Un_S))
                        throw new InvalidOperationException("Native material pass dispatch can bypass the opaque filter.");
                    int next = Array.IndexOf(passFilter, passFilter[dispatch + 2].Operand as Instruction);
                    if (next <= dispatch || next > opaque - 1)
                        throw new InvalidOperationException("Native material pass dispatch can bypass the opaque filter.");
                    dispatch = next;
                }
                int comparison = opaque + 1;
                var acceptedModes = new System.Collections.Generic.List<int>();
                while (Codes(passFilter, comparison, Code.Ldarg_2, Code.Ldfld)
                    && FieldUse(passFilter[comparison + 1], Code.Ldfld, material, "AlphaMode")
                    && Codes(passFilter, comparison + 3, Code.Ceq))
                {
                    acceptedModes.Add(Constant(passFilter[comparison + 2]));
                    comparison += 4;
                    if (acceptedModes.Count == 1)
                        continue;
                    if (!Codes(passFilter, comparison, Code.Or))
                        throw new InvalidOperationException("Native opaque material comparisons no longer use an OR chain.");
                    comparison++;
                }
                if (!Codes(passFilter, comparison, Code.Ret) || comparison + 1 >= passFilter.Length
                    || !ReferenceEquals(passFilter[opaque].Operand, passFilter[comparison + 1])
                    || !new[] { 0, 4, 3 }.All(mode => acceptedModes.Contains(mode)))
                    throw new InvalidOperationException("Native opaque pass no longer accepts the supported static alpha modes.");

                var channelReads = Body(meshBufferConstructor);
                foreach (string channel in new[] { "vertices", "normals", "tangents", "triangles", "uv" })
                    One(channelReads, instruction => Calls(instruction, unityMesh, "get_" + channel), "mesh channel " + channel);

                var faceOffset = Need(offset, "GetFaceOffset", true, i, i, meshBuffer);
                var allFaces = Body(Need(offset, "GetFaceOffset", true, ListOf(i), meshBuffer));
                One(allFaces, instruction => Calls(instruction, offset, "GetFaceOffset"), "one offset per triangle");
                if (!Enumerable.Range(0, allFaces.Length).Any(index => Codes(allFaces, index, Code.Ldc_I4_3, Code.Add, Code.Stloc_1))
                    || !allFaces.Any(instruction => FieldUse(instruction, Code.Ldfld, meshBuffer, "triangles")))
                    throw new InvalidOperationException("Native material face traversal changed.");
                var triangle = Body(faceOffset);
                var expectedTriangle = new[]
                {
                    Code.Ldarg_1, Code.Ldfld, Code.Ldarg_0, Code.Callvirt, Code.Stloc_0,
                    Code.Ldarg_1, Code.Ldfld, Code.Ldarg_0, Code.Ldc_I4_1, Code.Add, Code.Callvirt, Code.Stloc_1,
                    Code.Ldarg_1, Code.Ldfld, Code.Ldarg_0, Code.Ldc_I4_2, Code.Add, Code.Callvirt, Code.Stloc_2,
                    Code.Ldc_R4, Code.Ldarg_1, Code.Ldfld, Code.Ldloc_0, Code.Callvirt, Code.Ldfld,
                    Code.Ldarg_1, Code.Ldfld, Code.Ldloc_1, Code.Callvirt, Code.Ldfld, Code.Add,
                    Code.Ldarg_1, Code.Ldfld, Code.Ldloc_2, Code.Callvirt, Code.Ldfld, Code.Add, Code.Mul, Code.Call, Code.Ret
                };
                if (triangle.Length != expectedTriangle.Length || !Codes(triangle, 0, expectedTriangle)
                    || !Equals(triangle[19].Operand, 1f / 3f)
                    || !new[] { 1, 6, 13 }.All(index => FieldUse(triangle[index], Code.Ldfld, meshBuffer, "triangles"))
                    || !new[] { 21, 26, 32 }.All(index => FieldUse(triangle[index], Code.Ldfld, meshBuffer, "uv"))
                    || !new[] { 24, 29, 35 }.All(index => FieldUse(triangle[index], Code.Ldfld, vector2, "x"))
                    || !new[] { 3, 10, 17 }.All(index => Calls(triangle[index], ListOf(i), "get_Item"))
                    || !new[] { 23, 28, 34 }.All(index => Calls(triangle[index], ListOf(vector2), "get_Item"))
                    || !Calls(triangle[38], "UnityEngine.Mathf", "FloorToInt"))
                    throw new InvalidOperationException("Native material slot is no longer the floor of the mean triangle UV0.x.");

                var push = Body(Need(voxelMesh, "PushMeshData", false, pass, renderPass));
                var faceReads = Indices(push, instruction => FieldUse(instruction, Code.Ldfld, voxelMesh, "faceIDList"));
                var materialReads = Indices(push, instruction => instruction.OpCode.Code == Code.Ldelem_Any
                    && instruction.Operand is TypeReference type && type.FullName == material);
                var materialFilters = Indices(push, instruction => Calls(instruction, voxelMesh, "AddToShapeData"));
                int materialId = One(push, instruction => FieldUse(instruction, Code.Ldfld, material, "Id"), "baked material ID");
                int expand = One(push, instruction => Calls(instruction, shape, ".ctor"), "shape vertex expansion");
                if (faceReads.Length != 5 || !faceReads.All(index => Codes(push, index + 2, Code.Ldc_I4_3, Code.Div, Code.Callvirt))
                    || materialReads.Length != 2 || !materialReads.All(index => index >= 2
                        && Calls(push[index - 2], brick, "get_VoxelMaterials"))
                    || materialFilters.Length != 2 || !materialFilters.All(index => index + 1 < push.Length
                        && push[index + 1].OpCode.Code is Code.Brfalse or Code.Brfalse_S)
                    || !Codes(push, faceReads[4] + 5, Code.Conv_R4, Code.Sub, Code.Stind_R4)
                    || !push.Any(instruction => FieldUse(instruction, Code.Ldfld, voxelMesh, "scaleUV")))
                    throw new InvalidOperationException("Native vertex material indexing or UV remapping changed.");
                NeedOrder(materialReads[0], materialFilters[0], materialReads[1], materialFilters[1], materialId, expand, faceReads[4]);

                int cluster = One(push, instruction => Calls(instruction, "UnityEngine.Mathf", "CeilToInt"), "shape cluster size");
                if (cluster < 4 || cluster + 5 >= push.Length
                    || !FieldUse(push[cluster - 4], Code.Ldfld, pass, "count")
                    || !Codes(push, cluster - 3, Code.Conv_R4, Code.Ldc_R4, Code.Div)
                    || !Equals(push[cluster - 2].Operand, 36f)
                    || !Codes(push, cluster + 2, Code.Ldloc_3, Code.Ldc_I4_3, Code.Mul, Code.Ldc_I4_S, Code.Mul)
                    || Constant(push[cluster + 5]) != 12
                    || Indices(push, instruction => Calls(instruction, ListOf(shape), "Add")).Length != 2
                    || !push.Any(instruction => instruction.OpCode.Code == Code.Initobj
                        && instruction.Operand is TypeReference type && type.FullName == shape))
                    throw new InvalidOperationException("Native 12-triangle cluster expansion or padding changed.");
                One(push, instruction => FieldUse(instruction, Code.Stfld, "VoxelEngine.RenderData", "clusterStart"), "render cluster start");
                One(push, instruction => FieldUse(instruction, Code.Stfld, "VoxelEngine.RenderData", "clusterCount"), "render cluster count");

                var layout = module.GetType(shape);
                var shapeFields = new (string Name, string Type)[]
                {
                    ("position", vector3), ("normal", vector3), ("tangent", vector4), ("binormal", vector3), ("uv", vector2),
                    ("materialID", "System.UInt32"), ("boundsCenter", vector3), ("boundsSize", vector3),
                    ("passBoundsMin", vector3), ("passBoundsMax", vector3), ("vertOffset", vector3)
                };
                if (layout?.IsSequentialLayout != true || !layout.IsValueType || layout.PackingSize > 0 || layout.ClassSize > 0
                    || !layout.Fields.Where(field => !field.IsStatic).Select(field => (field.Name, field.FieldType.FullName))
                        .SequenceEqual(shapeFields))
                    throw new InvalidOperationException("Native expanded shape vertex layout changed.");
                var expandVertex = Body(Need(shape, ".ctor", false, v, meshBuffer, i, i, bounds, bounds, vector3));
                foreach (var field in shapeFields)
                    One(expandVertex, instruction => FieldUse(instruction, Code.Stfld, shape, field.Name), "shape field " + field.Name);

                const string resources = "Manager.GameResourcesManager";
                var allocate = Need(resources, "AllocateResources", true, v);
                var upload = Need(buffers, "AllocateResources", true, v);
                if (!upload.IsPublic || !upload.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Manager.AllocateResourcesCallback"))
                    throw new InvalidOperationException("Shape mesh upload is no longer dispatched by native allocation.");
                var awake = Need("ResourcesLoader", "Awake", false, v);
                NeedOrder(FunctionalCallIndex(awake, Need(brickCollider, "AllocateResources", true, v)),
                    FunctionalCallIndex(awake, Need(brick, "LoadAllResourcesRuntime", true, v)), FunctionalCallIndex(awake, allocate));
                var allocateBody = Body(allocate);
                NeedOrder(One(allocateBody, instruction => Calls(instruction, "System.Reflection.MethodBase", "Invoke"), "native allocation dispatch"),
                    One(allocateBody, instruction => Calls(instruction, resources, "OnResourceLoaded"), "native resource readiness"));

                var uploadBody = Body(upload);
                int stride = One(uploadBody, instruction => GenericCall(instruction, "System.Runtime.InteropServices.Marshal", "SizeOf", shape), "GPU stride");
                int setData = One(uploadBody, instruction => GenericCall(instruction, gpuBuffer, "SetData", shape), "GPU vertex upload");
                int binding = One(uploadBody, instruction => Calls(instruction, "UnityEngine.Shader", "SetGlobalBuffer"), "GPU shape binding");
                if (stride + 2 >= uploadBody.Length || uploadBody[stride + 1].OpCode.Code != Code.Newobj
                    || uploadBody[stride + 1].Operand is not MethodReference createBuffer
                    || createBuffer.DeclaringType.FullName != gpuBuffer || createBuffer.Name != ".ctor"
                    || !createBuffer.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(new[] { i, i })
                    || !FieldUse(uploadBody[stride + 2], Code.Stsfld, buffers, "shapeMeshBuffer")
                    || binding < 2 || !Literal(uploadBody[binding - 2], "ShapeMeshData")
                    || !FieldUse(uploadBody[binding - 1], Code.Ldsfld, buffers, "shapeMeshBuffer")
                    || setData < 2 || !FieldUse(uploadBody[setData - 1], Code.Ldsfld, buffers, "shapeMeshDatas")
                    || !FieldUse(uploadBody[setData - 2], Code.Ldsfld, buffers, "shapeMeshBuffer"))
                    throw new InvalidOperationException("Native GPU shape data source or shader binding changed.");
                NeedOrder(stride, setData, binding);

                var colliderCtor = Need(collider, ".ctor", false, v, voxelMesh, "Manager.ColliderShapeCommon/Shape", bounds, "VoxelEngine.SubShape[]");
                var propertyCtor = Need(properties, ".ctor", false, v, brick);
                var brickLoad = Body(Need(brick, "LoadResourcesRuntime", false, v));
                NeedOrder(One(brickLoad, instruction => Calls(instruction, brick, "set_VoxelMaterials"), "brick baked materials"),
                    One(brickLoad, instruction => New(instruction, constructor), "brick mesh construction"),
                    One(brickLoad, instruction => New(instruction, colliderCtor), "brick collider construction"),
                    One(brickLoad, instruction => Calls(instruction, brickCollider, "SetBrick"), "brick collider publication"),
                    One(brickLoad, instruction => New(instruction, propertyCtor), "brick properties publication"));
                if (!brickLoad.Any(instruction => FieldUse(instruction, Code.Ldfld, brick, "boundsOverride"))
                    || !brickLoad.Any(instruction => FieldUse(instruction, Code.Ldsflda, brick, "BrickProperties")))
                    throw new InvalidOperationException("Native brick bounds override or properties path changed.");
                var colliderBody = Body(colliderCtor);
                int colliderMesh = One(colliderBody, instruction => FieldUse(instruction, Code.Stfld, collider, "Mesh"), "stock collider mesh");
                int colliderBounds = One(colliderBody, instruction => FieldUse(instruction, Code.Stfld, collider, "bounds"), "collider bounds");
                if (colliderMesh < 5 || !LoadMesh(colliderBody[colliderMesh - 1])
                    || !Calls(colliderBody[colliderMesh - 2], "System.String", "Concat")
                    || !FieldUse(colliderBody[colliderMesh - 3], Code.Ldfld, collider, "path")
                    || !Literal(colliderBody[colliderMesh - 5], "Shapes/")
                    || !colliderBody.Any(instruction => FieldUse(instruction, Code.Ldfld, voxelMesh, "path"))
                    || !Codes(colliderBody, colliderBounds - 5, Code.Ldarg_1, Code.Ldfld, Code.Callvirt, Code.Br_S, Code.Ldarg_3)
                    || !FieldUse(colliderBody[colliderBounds - 4], Code.Ldfld, voxelMesh, "Mesh")
                    || !Calls(colliderBody[colliderBounds - 3], unityMesh, "get_bounds")
                    || !ReferenceEquals(colliderBody[colliderBounds - 2].Operand, colliderBody[colliderBounds]))
                    throw new InvalidOperationException("Native collider no longer loads the stock path or follows the verified bounds fallback.");
                NeedOrder(colliderMesh, colliderBounds);
                var publishCollider = Body(Need(brickCollider, "SetBrick", true, v, brick));
                var addCollider = Body(Need(brickCollider, "AddColliderData", true, v, i, f, "VoxelEngine.SubShape", i, b, b, b, brick));
                const string colliderData = brickCollider + "/Data";
                if (!publishCollider.Any(instruction => FieldUse(instruction, Code.Ldfld, collider, "subShapes"))
                    || !publishCollider.Any(instruction => Calls(instruction, brickCollider, "AddColliderData"))
                    || !addCollider.Any(instruction => FieldUse(instruction, Code.Ldfld, "VoxelEngine.SubShape", "bounds"))
                    || !addCollider.Any(instruction => FieldUse(instruction, Code.Stfld, colliderData, "bounds"))
                    || !addCollider.Any(instruction => FieldUse(instruction, Code.Ldsflda, brickCollider, "Datas")))
                    throw new InvalidOperationException("Native collider bounds no longer flow into the stock collider table.");
                var brickBounds = Body(Need(brick, "get_Bounds", false, bounds));
                var propertyBody = Body(propertyCtor);
                if (!brickBounds.Any(instruction => FieldUse(instruction, Code.Ldfld, voxelMesh, "bounds"))
                    || !new[] { "renderBounds", "volumeBounds" }.All(name =>
                    {
                        int store = One(propertyBody, instruction => FieldUse(instruction, Code.Stfld, properties, name), name);
                        return store >= 2 && Calls(propertyBody[store - 2], "Item", "get_Bounds")
                            && Calls(propertyBody[store - 1], "Common.BoundsExtensions", "ToAabb");
                    }))
                    throw new InvalidOperationException("Native collider and brick properties no longer share the verified bounds contract.");

                return new CapabilityResult(capability, true, "Native render mesh substitution, material indexing, GPU layout and stock collision contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
