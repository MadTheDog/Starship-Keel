using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the optional append-only material and atlas path independently of stock-backed visuals.</summary>
        public static CapabilityResult EvaluateAttachmentMaterials(ModuleDefinition module)
        {
            const string capability = "attachmentMaterials";
            const string loader = "ResourcesLoader", manager = "Manager.GameResourcesManager", brick = "VoxelEngine.Brick";
            const string material = "VoxelEngine.VoxelMaterial", materials = material + "[]";
            const string resources = "VoxelEngine.VoxelMaterialResources", asset = "VoxelEngine.AssetVoxelMaterials";
            const string textureArray = "UnityEngine.Texture2DArray", shader = "UnityEngine.Shader";
            const string buffer = "UnityEngine.ComputeBuffer", materialBuffer = "materialParamsBuffer";
            const string v = "System.Void", i = "System.Int32", f = "System.Single", b = "System.Boolean", s = "System.String";
            const string allocateAttribute = "Manager.AllocateResourcesCallback";
            const string reloadAttribute = "Manager.OnGameReloadCallback", releaseAttribute = "Manager.ReleaseResourcesCallback";

            try
            {
                MethodDefinition Need(string owner, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, owner, name, isStatic, result, parameters)
                    ?? throw new InvalidOperationException("Missing attachment material signature: " + owner + "." + name);

                Instruction[] Body(MethodDefinition method) => method.HasBody
                    ? method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray()
                    : throw new InvalidOperationException("Missing attachment material method body: " + method.FullName);

                bool FieldUse(Instruction instruction, Code code, string owner, string name, string type) =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == owner && field.Name == name && field.FieldType.FullName == type;

                bool Calls(Instruction instruction, string owner, string name, string result, params string[] parameters) =>
                    (instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt)
                    && instruction.Operand is MethodReference method && method.DeclaringType.FullName == owner
                    && method.Name == name && method.ReturnType.FullName == result
                    && method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters);

                int Site(Instruction[] body, Func<Instruction, bool> match, string description)
                {
                    var sites = body.Select((instruction, index) => (instruction, index))
                        .Where(entry => match(entry.instruction)).Select(entry => entry.index).ToArray();
                    return sites.Length == 1 ? sites[0]
                        : throw new InvalidOperationException("Missing or ambiguous attachment material site: " + description);
                }

                bool Literal(Instruction instruction, string value) =>
                    instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, value);

                void Ordered(params int[] sites)
                {
                    for (int index = 1; index < sites.Length; index++)
                        if (sites[index - 1] >= sites[index])
                            throw new InvalidOperationException("Native attachment material resource order changed.");
                }

                bool Attribute(MethodDefinition method, string name) =>
                    method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == name);

                var row = module.GetType(material);
                var fields = new (string Name, string Type)[]
                {
                    ("Id", i), ("AlphaMode", "VoxelEngine.AlphaMode"), ("dynDataVert", "VoxelEngine.DynData_Vert"),
                    ("TextureID", i), ("BorderId", i), ("Tiling", i), ("sizeUnit", f),
                    ("U_threshold_low", f), ("U_threshold_up", f), ("U_threshold_width", f),
                    ("V_threshold_low", f), ("V_threshold_up", f), ("V_threshold_width", f),
                    ("thresholdGeoHigh", f), ("thresholdGeoLow", f), ("BorderMode", "VoxelEngine.BorderMode"),
                    ("normalID", i), ("specularID", i), ("borderNormalID", i), ("borderSpecularID", i),
                    ("uvProj", "VoxelEngine.UV_Proj"), ("paintMode", "VoxelEngine.PaintMode"), ("blendMode", "VoxelEngine.BlendMode"),
                    ("recomputeNormal", "VoxelEngine.RecomputeNormal"), ("UVScale", "UnityEngine.Vector2"),
                    ("buildingOffset", "VoxelEngine.BuildingOffset"), ("dynDataPixel", "VoxelEngine.DynData_Pixel"),
                    ("scalingDeformLinkMode", "VoxelEngine.ResizeMode/LinkMode"), ("translationPath", "UnityEngine.Vector3"),
                    ("fuelOverlay", "VoxelEngine.FuelOverlay")
                };
                if (row?.IsSequentialLayout != true || !row.IsValueType || row.PackingSize > 0 || row.ClassSize > 0
                    || !row.Fields.Where(field => !field.IsStatic).Select(field => (field.Name, field.FieldType.FullName)).SequenceEqual(fields)
                    || row.Fields.Any(field => !field.IsStatic && !field.IsPublic)
                    || !fields.Where(field => field.Type.StartsWith("VoxelEngine.", StringComparison.Ordinal)).All(field =>
                        module.GetType(field.Type) is { IsEnum: true } type
                        && type.Fields.Any(member => member.Name == "value__" && member.FieldType.FullName == i)))
                    throw new InvalidOperationException("Native VoxelMaterial row layout changed.");

                // These constants are baked into the row writer at compile time and consumed by the native shader.
                var paletteModes = new (string Type, string Member, int Value)[]
                {
                    ("AlphaMode", "noAlpha", 0), ("AlphaMode", "glowDiffuse", 3), ("BorderMode", "noBorder", 0),
                    ("PaintMode", "noPaint", 1), ("DynData_Vert", "none", 0), ("DynData_Pixel", "none", 0),
                    ("UV_Proj", "off", 0), ("RecomputeNormal", "off", 0), ("BuildingOffset", "off", 0), ("FuelOverlay", "off", 0)
                };
                foreach (var mode in paletteModes)
                {
                    var value = module.GetType("VoxelEngine." + mode.Type)?.Fields
                        .SingleOrDefault(field => field.Name == mode.Member && field.IsLiteral);
                    if (value?.Constant is not int constant || constant != mode.Value)
                        throw new InvalidOperationException("Native palette mode changed: " + mode.Type + "." + mode.Member);
                }

                var getRows = Need(resources, "get_materialParamDatas", true, materials);
                var setRows = Need(resources, "set_materialParamDatas", true, v, materials);
                var getCursor = Need(resources, "get_MaterialCount", true, i);
                var setCursor = Need(resources, "set_MaterialCount", true, v, i);
                void SharedField(MethodDefinition getter, MethodDefinition setter, string type)
                {
                    var read = Body(getter);
                    var write = Body(setter);
                    if (read.Length != 2 || write.Length != 3 || read[0].OpCode.Code != Code.Ldsfld
                        || write[0].OpCode.Code != Code.Ldarg_0 || write[1].OpCode.Code != Code.Stsfld
                        || read[1].OpCode.Code != Code.Ret || write[2].OpCode.Code != Code.Ret
                        || read[0].Operand is not FieldReference source || write[1].Operand is not FieldReference destination
                        || source.FullName != destination.FullName || source.DeclaringType.FullName != resources
                        || source.FieldType.FullName != type)
                        throw new InvalidOperationException("Native material table or cursor no longer has paired storage accessors.");
                }
                if (!getRows.IsPublic || !getCursor.IsPublic || !setCursor.IsPublic)
                    throw new InvalidOperationException("Native material table and cursor access changed.");
                SharedField(getRows, setRows, materials);
                SharedField(getCursor, setCursor, i);

                var seed = Need(resources, "SetMaterialParamDatas", true, v, materials);
                var seedBody = Body(seed);
                if (!seed.IsPublic || seedBody.Length != 5 || seedBody[0].OpCode.Code != Code.Ldc_I4_0
                    || !Calls(seedBody[1], resources, "set_MaterialCount", v, i) || seedBody[2].OpCode.Code != Code.Ldarg_0
                    || !Calls(seedBody[3], resources, "set_materialParamDatas", v, materials) || seedBody[4].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native material table seeding or cursor reset changed.");

                var loaderMethod = Need(loader, "Awake", false, v);
                var awake = Body(loaderMethod);
                int Atlas(string property, string field)
                {
                    int site = Site(awake, instruction => Literal(instruction, property), property + " global");
                    var source = module.GetType(loader)?.Fields.SingleOrDefault(value => value.Name == field);
                    if (source == null || source.IsStatic || source.FieldType.FullName != textureArray
                        || site + 3 >= awake.Length || awake[site + 1].OpCode.Code != Code.Ldarg_0
                        || !FieldUse(awake[site + 2], Code.Ldfld, loader, field, textureArray)
                        || !Calls(awake[site + 3], shader, "SetGlobalTexture", v, s, "UnityEngine.Texture"))
                        throw new InvalidOperationException("Native " + property + " no longer binds its stock texture array.");
                    return site;
                }
                int diffuse = Atlas("MAIN_TEX", "ta_voxelDiffuse");
                int specular = Atlas("SPECULAR_TEX", "ta_voxelSpecular");
                int diffuseAlias = Site(awake, instruction => FieldUse(instruction, Code.Stsfld, loader, "VOXEL_DIFFUSE", textureArray),
                    "CPU diffuse atlas");
                int materialSeed = Site(awake, instruction => Calls(instruction, resources, "SetMaterialParamDatas", v, materials),
                    "baked material seed");
                int stockBricks = Site(awake, instruction => Calls(instruction, brick, "LoadAllResourcesRuntime", v), "stock brick load");
                int allocations = Site(awake, instruction => Calls(instruction, manager, "AllocateResources", v), "GPU resource allocation");
                var diffuseField = module.GetType(loader)?.Fields.SingleOrDefault(field => field.Name == "VOXEL_DIFFUSE");
                if (diffuseField == null || !diffuseField.IsPublic || !diffuseField.IsStatic || diffuseField.IsInitOnly
                    || diffuseField.FieldType.FullName != textureArray
                    || diffuseAlias < 2 || awake[diffuseAlias - 2].OpCode.Code != Code.Ldarg_0
                    || !FieldUse(awake[diffuseAlias - 1], Code.Ldfld, loader, "ta_voxelDiffuse", textureArray)
                    || materialSeed < 3 || awake[materialSeed - 3].OpCode.Code != Code.Ldarg_0
                    || !FieldUse(awake[materialSeed - 2], Code.Ldfld, loader, "AssetVoxelMaterials", asset)
                    || !FieldUse(awake[materialSeed - 1], Code.Ldfld, asset, "materialParamDatas", materials))
                    throw new InvalidOperationException("Native stock atlas or baked material source changed.");
                Ordered(Math.Max(Math.Max(diffuseAlias, diffuse), Math.Max(specular, materialSeed)), stockBricks, allocations);
                if (loaderMethod.Body.HasExceptionHandlers)
                    throw new InvalidOperationException("Native material startup now has an exception-controlled path.");
                int firstSetup = Math.Min(Math.Min(diffuseAlias, diffuse), Math.Min(specular, materialSeed));
                for (int index = 0; index < allocations; index++)
                {
                    var instruction = awake[index];
                    if ((index >= firstSetup && instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch
                            or FlowControl.Return or FlowControl.Throw)
                        || (instruction.Operand is Instruction target && Array.IndexOf(awake, target) > firstSetup)
                        || (instruction.Operand is Instruction[] targets && targets.Any(target => Array.IndexOf(awake, target) > firstSetup)))
                        throw new InvalidOperationException("Native material startup can bypass stock preparation or allocation.");
                }

                var stockFaceLookup = Body(Need(material, "CreateFaceIDArrayRuntime", true, materials, "System.Object[]"));
                int rowRead = Site(stockFaceLookup, instruction => Calls(instruction, resources, "get_materialParamDatas", materials),
                    "stock face material lookup");
                int cursorWrite = Site(stockFaceLookup, instruction => Calls(instruction, resources, "set_MaterialCount", v, i),
                    "stock face cursor advance");
                if (rowRead + 2 >= stockFaceLookup.Length || !Calls(stockFaceLookup[rowRead + 1], resources, "get_MaterialCount", i)
                    || stockFaceLookup[rowRead + 2].OpCode.Code != Code.Ldelem_Any
                    || stockFaceLookup[rowRead + 2].Operand is not TypeReference element || element.FullName != material
                    || cursorWrite < 3 || !Calls(stockFaceLookup[cursorWrite - 3], resources, "get_MaterialCount", i)
                    || stockFaceLookup[cursorWrite - 2].OpCode.Code != Code.Ldc_I4_1
                    || stockFaceLookup[cursorWrite - 1].OpCode.Code != Code.Add)
                    throw new InvalidOperationException("Native stock material consumption changed.");
                Ordered(rowRead, cursorWrite);
                var brickResources = Body(Need(brick, "LoadResourcesRuntime", false, v));
                int decalAtlas = Site(brickResources, instruction => FieldUse(instruction, Code.Ldsfld, loader, "VOXEL_DIFFUSE", textureArray),
                    "CPU decal diffuse atlas");
                Ordered(decalAtlas, Site(brickResources, instruction => Calls(instruction, textureArray, "GetPixels32", "UnityEngine.Color32[]", i),
                    "CPU decal diffuse pixels"));

                var upload = Need(resources, "LoadResourcesRuntime", true, v);
                var uploadBody = Body(upload);
                if (!upload.IsPublic || !Attribute(upload, allocateAttribute))
                    throw new InvalidOperationException("Native material upload no longer runs during resource allocation.");
                Ordered(Site(uploadBody, instruction => Calls(instruction, resources, "LoadBuffers", v), "native material upload callback"),
                    Site(uploadBody, instruction => Calls(instruction, resources, "LoadMaterials", v), "native scene material construction"));
                void CallbackDispatch(string methodName, string attribute, bool signalsReadiness = false)
                {
                    var body = Body(Need(manager, methodName, true, v));
                    int check = Site(body, instruction => Calls(instruction, "System.Reflection.MemberInfo", "GetCustomAttributes",
                        "System.Object[]", "System.Type", b), methodName + " callback attribute check");
                    int invoke = Site(body, instruction => Calls(instruction, "System.Reflection.MethodBase", "Invoke", "System.Object",
                        "System.Object", "System.Object[]"), methodName + " callback invocation");
                    if (check < 3 || check + 2 >= body.Length || invoke < 2
                        || body[check - 3].OpCode.Code != Code.Ldtoken || body[check - 3].Operand is not TypeReference type
                        || type.FullName != attribute || body[check - 1].OpCode.Code != Code.Ldc_I4_0
                        || !Calls(body[check - 2], "System.Type", "GetTypeFromHandle", "System.Type", "System.RuntimeTypeHandle")
                        || body[check + 1].OpCode.Code != Code.Ldlen
                        || body[check + 2].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                        || body[check + 2].Operand is not Instruction skip || Array.IndexOf(body, skip) <= invoke
                        || body[invoke - 1].OpCode.Code != Code.Ldnull || body[invoke - 2].OpCode.Code != Code.Ldnull)
                        throw new InvalidOperationException("Native resource dispatch changed: " + methodName);
                    Ordered(check, invoke);
                    if (signalsReadiness)
                        Ordered(invoke, Site(body, instruction => Calls(instruction, manager, "OnResourceLoaded", v), "native resource readiness"));
                }
                CallbackDispatch("AllocateResources", allocateAttribute, signalsReadiness: true);

                var gpu = Body(Need(resources, "LoadBuffers", true, v));
                int size = Site(gpu, instruction => instruction.OpCode.Code == Code.Call && instruction.Operand is GenericInstanceMethod method
                    && method.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal" && method.Name == "SizeOf"
                    && method.GenericArguments.Count == 1 && method.GenericArguments[0].FullName == material, "material row stride");
                int bind = Site(gpu, instruction => Literal(instruction, "MaterialParams"), "material GPU global");
                if (size < 8 || bind < 3 || bind + 2 >= gpu.Length || size + 2 >= gpu.Length
                    || !FieldUse(gpu[size - 8], Code.Ldsfld, resources, materialBuffer, buffer)
                    || gpu[size - 7].OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || !ReferenceEquals(gpu[size - 7].Operand, gpu[bind - 3])
                    || !Calls(gpu[size - 6], resources, "get_materialParamDatas", materials)
                    || gpu[size - 5].OpCode.Code != Code.Ldlen || gpu[size - 4].OpCode.Code != Code.Conv_I4
                    || !Calls(gpu[size - 3], resources, "get_materialParamDatas", materials)
                    || gpu[size - 2].OpCode.Code != Code.Ldc_I4_0 || gpu[size - 1].OpCode.Code != Code.Ldelem_Any
                    || gpu[size - 1].Operand is not TypeReference gpuElement || gpuElement.FullName != material
                    || gpu[size + 1].OpCode.Code != Code.Newobj || gpu[size + 1].Operand is not MethodReference createBuffer
                    || createBuffer.DeclaringType.FullName != buffer || createBuffer.Name != ".ctor"
                    || !createBuffer.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(new[] { i, i })
                    || !FieldUse(gpu[size + 2], Code.Stsfld, resources, materialBuffer, buffer)
                    || !FieldUse(gpu[bind - 3], Code.Ldsfld, resources, materialBuffer, buffer)
                    || !Calls(gpu[bind - 2], resources, "get_materialParamDatas", materials)
                    || !Calls(gpu[bind - 1], buffer, "SetData", v, "System.Array")
                    || !FieldUse(gpu[bind + 1], Code.Ldsfld, resources, materialBuffer, buffer)
                    || !Calls(gpu[bind + 2], shader, "SetGlobalBuffer", v, s, buffer))
                    throw new InvalidOperationException("Native material buffer sizing, upload, or shader binding changed.");
                int texelSize = Site(gpu, instruction => Literal(instruction, "TEXTURE_RES"), "native atlas resolution");
                if (texelSize + 2 >= gpu.Length || Constant(gpu[texelSize + 1]) != 256
                    || !Calls(gpu[texelSize + 2], shader, "SetGlobalInt", v, s, i))
                    throw new InvalidOperationException("Native shader texture resolution contract changed.");

                int reloadBranch = Site(awake, instruction => FieldUse(instruction, Code.Ldsfld, loader, "codexLoaded", b), "game reload branch");
                int reload = Site(awake, instruction => Calls(instruction, manager, "OnGameReload", v), "native game reload");
                int reloadReady = Site(awake, instruction => Calls(instruction, manager, "OnResourceLoaded", v), "reload readiness");
                if (reloadBranch + 1 >= awake.Length || reloadReady + 2 >= awake.Length
                    || awake[reloadBranch + 1].OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || !ReferenceEquals(awake[reloadBranch + 1].Operand, awake[reloadReady + 2])
                    || awake[reloadReady + 1].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native reload no longer preserves initial material resources.");
                Ordered(reloadBranch, reload, reloadReady, diffuseAlias);
                var reloadMaterials = Need(resources, "OnGameReload", true, v);
                var reloadBody = Body(reloadMaterials);
                if (!Attribute(reloadMaterials, reloadAttribute) || reloadBody.Length != 3
                    || !Calls(reloadBody[0], "Manager.SkyTransformData", "OnGameReload", v)
                    || !Calls(reloadBody[1], "Manager.CullingResources", "OnGameReload", v)
                    || reloadBody[2].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native reload no longer retains material buffer and atlas bindings.");
                CallbackDispatch("OnGameReload", reloadAttribute);

                var release = Need(resources, "UnloadResources", true, v);
                var releaseBody = Body(release);
                int clear = Site(releaseBody, instruction => FieldUse(instruction, Code.Stsfld, resources, materialBuffer, buffer),
                    "native material buffer clear");
                if (!Attribute(release, releaseAttribute) || clear < 5 || releaseBody[clear - 1].OpCode.Code != Code.Ldnull
                    || !FieldUse(releaseBody[clear - 5], Code.Ldsfld, resources, materialBuffer, buffer)
                    || !Calls(releaseBody[clear - 4], buffer, "Release", v)
                    || !FieldUse(releaseBody[clear - 3], Code.Ldsfld, resources, materialBuffer, buffer)
                    || !Calls(releaseBody[clear - 2], buffer, "Dispose", v))
                    throw new InvalidOperationException("Native material buffer teardown no longer has the verified owner.");
                CallbackDispatch("ReleaseResources", releaseAttribute);

                return new CapabilityResult(capability, true, "Native material row, cursor, atlas bindings, GPU upload and reload contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
