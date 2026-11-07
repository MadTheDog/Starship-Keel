using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateWeaponAttachments(ModuleDefinition module)
        {
            const string capability = "weaponAttachments";
            const string brick = "VoxelEngine.Brick", addon = "AddonBrick", slot = "VoxelEngine.AddonCommand";
            const string coord = "VoxelEngine.BrickCoord", ids = "VoxelEngine.BrickIds", hit = "VoxelEngine.VoxelRaycastHit";
            const string data = "VoxelEngine.BrickInstanceData", entityId = "VoxelEngine.VoxelEntityID";
            const string entity = "VoxelEngine.VoxelEntity", entities = "VoxelEngine.EntityBricks";
            const string permission = "VoxelEngine.BuildPermission", parent = "VoxelEngine.ParentEntity";
            const string command = "VoxelEngine.Command", system = "VoxelEngine.CommandSystem", icommand = "VoxelEngine.ICommand";
            const string receiver = "VoxelEngine.IAddonReceiverSystem", manager = "VoxelEngine.CommandManager";
            const string action = "VoxelEngine.ActionBuild", actionData = "VoxelEngine.VoxelActionData";
            const string actionType = "VoxelEngine.VoxelActionType", quaternion = "UnityEngine.Quaternion";
            const string unspawn = "VoxelEngine.BrickUnspawnMode", handle = "VoxelEngine.DynamicDataHandle", vector = "Vector3i";
            const string extra = "System.Collections.Generic.Dictionary`2<System.Int32,System.Byte[]>";
            const string query = "Manager.SkyPhysics/QueryOverlapSphereOnEntity", context = "Manager.SkyPhysics/QueryContext";
            const string elements = "Unity.Collections.NativeList`1<VoxelEngine.BrickColliderBVH/Element>";
            const string v = "System.Void", b = "System.Boolean", i = "System.Int32", by = "System.Byte", s = "System.String";
            const string u = "System.UInt16", f = "System.Single", args = "System.Object[]";

            try
            {
                MethodDefinition Need(string owner, string name, bool isStatic, string result, params string[] parameters) =>
                    Method(module, owner, name, isStatic, result, parameters)
                    ?? throw new InvalidOperationException("Missing native attachment signature: " + owner + "." + name);
                MethodDefinition Public(string owner, string name, bool isStatic, string result, params string[] parameters)
                {
                    var method = Need(owner, name, isStatic, result, parameters);
                    if (!method.IsPublic)
                        throw new InvalidOperationException("Native attachment API is no longer public: " + method.FullName);
                    return method;
                }
                FieldDefinition PrivateField(string owner, string name, string type, bool isStatic) =>
                    module.GetType(owner)?.Fields.SingleOrDefault(field => field.IsPrivate && field.Name == name
                        && field.FieldType.FullName == type && field.IsStatic == isStatic)
                    ?? throw new InvalidOperationException("Missing attachment hook field: " + owner + "." + name);
                void Before(MethodDefinition caller, MethodDefinition first, MethodDefinition second)
                {
                    if (FunctionalCallIndex(caller, first) >= FunctionalCallIndex(caller, second))
                        throw new InvalidOperationException("Native attachment lifecycle order changed: " + caller.FullName);
                }
                bool Uses(MethodDefinition method, string owner, string name, Code code) =>
                    method.HasBody && method.Body.Instructions.Any(instruction => instruction.OpCode.Code == code
                        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == owner && field.Name == name);
                void EnumValue(TypeDefinition? type, string name, int expected)
                {
                    if (type?.IsEnum != true || type.Fields.SingleOrDefault(field => field.Name == name && field.IsLiteral)?.Constant
                        is not int value || value != expected)
                        throw new InvalidOperationException("Native attachment enum member changed: " + name);
                }

                if (module.GetType(addon)?.BaseType?.FullName != brick || module.GetType(slot)?.BaseType?.FullName != command
                    || module.GetType(system)?.BaseType?.FullName != command || module.GetType(receiver)?.IsInterface != true)
                    throw new InvalidOperationException("Native attachment class hierarchy changed.");
                EnumValue(module.GetType(slot)?.NestedTypes.SingleOrDefault(type => type.Name == "Type"), "weaponAddon", 2);
                EnumValue(module.GetType(actionType), "build", 0);
                EnumValue(module.GetType(permission), "cannotBeBuilt", 1);
                EnumValue(module.GetType(permission), "onlyDecals", 3);

                const string resources = "Manager.GameResourcesManager";
                var allocateResources = Public(resources, "AllocateResources", true, v);
                var allocateItems = Public("Game.ItemInstanceResources", "AllocateResources", true, v);
                int readyIndex = FunctionalCallIndex(allocateResources, Public(resources, "OnResourceLoaded", true, v));
                if (!allocateItems.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Manager.AllocateResourcesCallback")
                    || !allocateResources.Body.Instructions.Take(readyIndex).Any(instruction =>
                        instruction.OpCode.Code == Code.Callvirt && instruction.Operand is MethodReference call
                        && call.DeclaringType.FullName == "System.Reflection.MethodBase" && call.Name == "Invoke"))
                    throw new InvalidOperationException("Native item allocation no longer precedes resource readiness.");
                Before(Need("ResourcesLoader", "Awake", false, v), allocateResources, Public("VoxelEngine.TabSchematics", "WakeUp", true, v));

                var constructor = Public(addon, ".ctor", false, v, i, s, s, "VoxelEngine.ResizeMode", b, slot + "/Type", args);
                FunctionalCallIndex(constructor, Public(brick, ".ctor", false, v, i, s, s, "VoxelEngine.ResizeMode", args));
                if (!Uses(constructor, addon, "addonType", Code.Stfld) || !Uses(constructor, addon, "stackable", Code.Stfld))
                    throw new InvalidOperationException("Native attachment constructor no longer declares slot type and stacking.");

                var find = Need(addon, "GetCommand", false, slot, coord, vector);
                var nativeCommand = Public(coord, "get_Command", false, icommand);
                var canBuild = Public(addon, "CanBuild", false, b, hit, vector, i, quaternion + "&");
                var construct = Public(addon, "OnConstruct", false, ids, coord, b, extra);
                var link = Public(slot, "Link", false, v, addon, coord, b);
                var unlink = Public(slot, "UnLink", false, v);
                var update = Public(receiver, "OnAddonBlockUpdate", false, v, b);
                if (!find.IsPrivate || !construct.IsVirtual || construct.IsFinal || !link.IsVirtual)
                    throw new InvalidOperationException("Native attachment lookup, construction, or link hook is incompatible.");
                Before(canBuild, Public(brick, "CanBuild", false, b, hit, vector, i, quaternion + "&"), find);
                if (!Uses(canBuild, slot, "addonType", Code.Ldfld) || !Uses(canBuild, data, "gridLevel", Code.Ldfld)
                    || !Uses(find, system, "addonCommands", Code.Ldfld) || !Uses(find, data, "gridPosition", Code.Ldfld))
                    throw new InvalidOperationException("Native attachment lookup no longer compares position, slot type and grid.");
                Before(construct, Public(brick, "OnConstruct", false, ids, coord, b, extra), nativeCommand);
                Before(construct, nativeCommand, find);
                Before(construct, find, link);
                Before(link, Public(addon, "OnLinkedToCommand", false, v, coord, slot, b), update);
                FunctionalCallIndex(unlink, update);
                FunctionalCallIndex(Public(addon, "OnRemove", false, v, coord, ids, unspawn), unlink, requireUnique: false);

                var overlap = Public(query, "Execute", false, elements);
                var overlapConstructor = Public(query, ".ctor", false, v, i, "UnityEngine.Vector3", f, "Unity.Collections.Allocator", context);
                FunctionalCallIndex(construct, overlap);
                if (!Uses(overlapConstructor, query, "brickElements", Code.Stfld)
                    || !Uses(overlap, query, "brickElements", Code.Ldfld)
                    || !overlapConstructor.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Newobj
                        && instruction.Operand is MethodReference allocation && allocation.DeclaringType.FullName == elements))
                    throw new InvalidOperationException("Native attachment query no longer owns its temporary results.");
                if (!construct.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Ldc_R4
                    && instruction.Operand is float radius && radius == 0.1f))
                    throw new InvalidOperationException("Native attachment lookup radius changed.");
                Public("Manager.SkyPhysics", "get_CONTEXT", true, context);
                Public("VoxelEngine.VoxelPhysics", "IsOverlapping", true, b, coord, b);
                Public(brick, "CanBuildOnEntity", false, b, entity);

                var onSelect = Public(action, "OnIsSelected", false, v, "Game.PlayerCommandManager");
                var actionCheck = Need(action, "CanBuild", false, b);
                var collisionCheck = Need(action, "IsColliding", false, b);
                var buildClick = Public(action, "LeftClickUp", false, v);
                var networkAction = Need("NetworkedBuilder", "DoAction", false, v, actionData + "&", b, i);
                var allocate = Public(entities, "Add", false, i, data);
                if (!actionCheck.IsPrivate || !networkAction.IsPrivate)
                    throw new InvalidOperationException("Native attachment action hook visibility changed.");
                Before(onSelect, Public("QuaternionExtensions", "ToByte", true, by, quaternion), actionCheck);
                Before(actionCheck, collisionCheck, Public(brick, "CanBuild", false, b, hit, vector, i, quaternion + "&"));
                if (!Uses(onSelect, data, "rotation", Code.Stfld) || !Uses(buildClick, action, "brickData", Code.Ldsfld)
                    || !Uses(allocate, data, "instanceId", Code.Stfld))
                    throw new InvalidOperationException("Native attachment preview or allocation no longer uses packed brick data.");
                FunctionalCallIndex(networkAction, allocate);
                FunctionalCallIndex(allocate, Public(entities, "AddBrickAt", false, v, data, b, extra));
                PrivateField(action, "brick", brick, false);
                PrivateField(action, "hitBuild", hit, false);
                PrivateField(action, "gridPositionInitial", vector, false);
                PrivateField(action, "q", quaternion, false);
                PrivateField(action, "brickData", data, true);

                Public(actionData, "get_type", false, actionType);
                Public(actionData, "get_brickData", false, data);
                Public(actionData, "get_brickCoord", false, coord);
                Public(hit, "get_isHitBrick", false, b);
                Public(hit, "get_brickCoord", false, coord);
                Public(coord, "get_entity", false, entity);
                Public(coord, "get_BrickIds", false, ids);
                Public(coord, "get_Position", false, "UnityEngine.Vector3");
                Public(coord, "get_quaternion", false, quaternion);
                Public(coord, ".ctor", false, v, entityId, data);
                Public(entity, "get_ID", false, entityId);
                Public(entity, "get_Bricks", false, entities);
                Public(entity, "get_Parent", false, parent);
                Public(entity, "get_buildPermission", false, permission);
                Public(entity, "get_WillBeUnspawned", false, b);
                Public(parent, "get_isDestroyed", false, b);
                Public(entities, "GetBrickData", false, data, i);
                Public(entities, "GetBrickIds", false, ids, i);
                Public(data, "get_Brick", false, brick);
                Public(data, "get_IsValid", false, b);
                Public(data, "get_Scale", false, vector);
                Public(data, "get_UnitGrid", false, i);
                Public(data, "get_QuaternionInv", false, quaternion);
                Public(slot, "get_receiver", false, receiver);
                Public(slot, "get_addon_bd", false, data);
                Public(command, "get_IsRemoved", false, b);
                Public(command, "get_brickCoord", false, coord);
                Public(command, "get_brickData", false, data);
                Public(manager, "GetCommand", true, command, i);

                var spawn = Need(system, "SpawnNewAddonCommand", false, slot, vector, by, handle, handle, receiver, slot + "/Type", b);
                if (!spawn.IsFamily || !Uses(spawn, system, "addonCommands", Code.Ldfld))
                    throw new InvalidOperationException("Native receiver no longer owns the spawned attachment slots.");
                var sideLayout=Need("VoxelEngine.SystemWeapon","SpawnSideAddon",false,v,b);
                FunctionalCallIndex(sideLayout,spawn);
                ValidateSideSlotLayout(sideLayout);
                Before(spawn, Public(manager, "GetId", true, i, command), Public(command, "Initialize", false, v, coord, ids, b, args));
                var checkReload = Public(slot, "CheckForBrickForLink", false, v);
                FunctionalCallIndex(Public(system, "OnEntityInitialized", false, v, b), checkReload);
                var asyncType = checkReload.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    ?.ConstructorArguments.Single().Value as TypeReference;
                var reload = asyncType == null ? null : module.GetType(slot)?.NestedTypes
                    .SingleOrDefault(type => type.FullName == asyncType.FullName)?.Methods.SingleOrDefault(method => method.Name == "MoveNext");
                if (reload == null)
                    throw new InvalidOperationException("Native delayed attachment reload is unavailable.");
                Before(reload, overlap, link);
                FunctionalCallIndex(Public(system, "OnDestroy", false, v, unspawn), Public(command, "OnDestroy", false, v, unspawn),
                    requireUnique: false);
                FunctionalCallIndex(Public(slot, "OnDestroy", false, v, unspawn), Public(manager, "ReleaseId", true, v, i));

                var repeat = Public(brick, "OnConstruct", false, ids, coord, b, extra);
                var addRepeat=Need(brick,"AddRepeatBrick",false,v,i,i,i,coord,u);
                Before(addRepeat,Public(data,"get_GridSize",false,f),Public("VoxelEngine.ResizeMode","get_unitStud",false,i));
                FunctionalCallIndex(addRepeat,allocate);
                if (!Uses(repeat, brick, "isRepeatBrick", Code.Ldfld) || !Uses(repeat, entities, "brickIds", Code.Ldflda)
                    || !Uses(repeat, ids, "commandId", Code.Ldfld) || !Uses(repeat, ids, "commandId", Code.Stfld)
                    || !repeat.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Sub))
                    throw new InvalidOperationException("Native repeat bricks no longer inherit the host command ID.");
                FunctionalCallIndex(Need(brick, "AddRepeatBrick", false, v, i, i, i, coord, u), allocate);
                var fields = new (string Owner, string Name, string Type, bool IsStatic)[]
                {
                    (brick,"BRICK_INDEX",brick+"[]",true), (brick,"forceLocalGrid",b,false), (brick,"NoMirroring",b,false),
                    (brick,"isRepeatBrick",b,false), (brick,"repeatBrickId",u,false), (brick,"repeatBrickStart",i,false),
                    (brick,"repeatBrickEndOffset",i,false), (coord,"entityID",entityId,false), (coord,"brickData",data,false),
                    (data,"instanceId",i,false), (data,"brickId",u,false), (data,"gridPosition",vector,false),
                    (data,"scale",u,false), (data,"gridLevel",by,false), (data,"rotation",by,false),
                    (ids,"commandId",i,false), (slot,"addonType",slot+"/Type",false),
                    (system,"addonCommands","System.Collections.Generic.List`1<"+slot+">",false),
                    (entity,"brickTree","Common.BVHSet`1/Handle<VoxelEngine.BrickColliderBVH/Element>",false),
                    (query,"brickElements",elements,false)
                };
                foreach (var field in fields)
                    if (!Field(module, field.Owner, field.Name, field.Type, field.IsStatic))
                        throw new InvalidOperationException("Native attachment field changed: " + field.Owner + "." + field.Name);

                return new CapabilityResult(capability, true, "Native attachment preview, allocation, linking and repeat ownership contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static void ValidateSideSlotLayout(MethodDefinition layout)
        {
            // Compare the geometry operations, ignoring local variable numbering and short instruction encodings.
            // The receiver duplicates this native calculation without instantiating a stock weapon command.
            var body=layout.Body.Instructions.Where(instruction=>instruction.OpCode.Code!=Code.Nop).ToArray();
            var operations=body.Select(instruction=>instruction.Operand switch
            {
                MethodReference call => call.DeclaringType.Name+"."+call.Name,
                FieldReference field => field.DeclaringType.Name+"."+field.Name,
                _ => instruction.OpCode.Code switch
                {
                    Code.Ldc_I4_0 => "0", Code.Ldc_I4_1 => "1", Code.Ldc_I4_2 => "2",
                    Code.Ldc_I4 or Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand).ToString(),
                    Code.Div => "div", Code.Mul => "mul", Code.Add => "add",
                    Code.Br or Code.Br_S => "branch", Code.Ble or Code.Ble_S => "loop",
                    _ => null
                }
            }).Where(operation=>operation!=null);
            const string expected=
                "Command.get_brickCoord BrickCoord.get_quaternion Vector3.get_up Vector3.op_UnaryNegation "
                + "Vector3.get_forward Quaternion.LookRotation Quaternion.op_Multiply QuaternionExtensions.ToByte "
                + "Command.get_brickData BrickInstanceData.get_UnitGrid 0 2 div 2 div Vector3i..ctor 0 branch "
                + "Command.get_brickData BrickInstanceData.gridPosition mul Command.get_brickCoord BrickCoord.get_Normal "
                + "Vector3i.op_Multiply Vector3i.op_Addition Command.get_brickData BrickInstanceData.get_Quaternion "
                + "Vector3i.op_Multiply Vector3i.op_Addition Command.get_vertexDataPointer Command.get_pixelDataPointer "
                + "2 CommandSystem.SpawnNewAddonCommand 1 add Command.get_brickData BrickInstanceData.get_Scale Vector3i.y loop";
            if(!operations.SequenceEqual(expected.Split(' ')))
                throw new InvalidOperationException("Native side-slot position, rotation, or stretch calculation changed.");
        }
    }
}
