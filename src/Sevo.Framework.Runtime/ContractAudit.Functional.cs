using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the optional native command capability independently of ordinary bricks.</summary>
        public static CapabilityResult EvaluateFunctionalCommands(ModuleDefinition module)
        {
            const string capability = "functionalCommands";
            const string brick = "VoxelEngine.Brick";
            const string commandBrick = "CommandBrick`1";
            const string command = "VoxelEngine.Command";
            const string commandManager = "VoxelEngine.CommandManager";
            const string stockSwitch = "VoxelEngine.CommandSwitch";
            const string cmd = "VoxelEngine.ICommand";
            const string source = "VoxelEngine.ICommandLinkSource";
            const string links = "VoxelEngine.CommandLinkData";
            const string coord = "VoxelEngine.BrickCoord";
            const string ids = "VoxelEngine.BrickIds";
            const string unspawn = "VoxelEngine.BrickUnspawnMode";
            const string instance = "VoxelEngine.BrickInstanceData";
            const string entity = "VoxelEngine.VoxelEntity";
            const string parent = "VoxelEngine.ParentEntity";
            const string initialized = "VoxelEngine.ICommandOnEntityInitializedCallback";
            const string predecessorCallback = "VoxelEngine.ICommandOnPredecessorCallback";
            const string player = "Game.PlayerCommandManager";
            const string extra = "System.Collections.Generic.Dictionary`2<System.Int32,System.Byte[]>";
            const string v = "System.Void", b = "System.Boolean", f = "System.Single", i = "System.Int32", args = "System.Object[]";

            try
            {
                MethodDefinition Need(string type, string name, bool isStatic, string result, params string[] parameters)
                    => Method(module,type,name,isStatic,result,parameters)
                        ?? throw new InvalidOperationException("Missing functional command contract: "+type+"."+name);
                MethodDefinition Public(string type, string name, bool isStatic, string result, params string[] parameters)
                {
                    var method=Need(type,name,isStatic,result,parameters);
                    if (!method.IsPublic) throw new InvalidOperationException("Functional command API is no longer public: "+method.FullName);
                    return method;
                }
                void Override(string name, string result, params string[] parameters)
                {
                    var method=Need(command,name,false,result,parameters);
                    if (!method.IsVirtual || method.IsFinal || (!method.IsPublic && !method.IsFamily))
                        throw new InvalidOperationException("Functional command callback is no longer overridable: "+method.FullName);
                }
                void Before(MethodDefinition caller, MethodDefinition first, MethodDefinition second)
                {
                    if (FunctionalCallIndex(caller,first) >= FunctionalCallIndex(caller,second))
                        throw new InvalidOperationException("Functional command lifecycle order changed: "+caller.FullName);
                }

                var generic = module.GetType(commandBrick);
                var type = generic?.GenericParameters.SingleOrDefault();
                if (generic?.IsPublic != true || generic.BaseType?.FullName != brick || type?.HasDefaultConstructorConstraint != true
                    || !type.Constraints.Any(constraint=>constraint.FullName==command)
                    || module.GetType(stockSwitch)?.BaseType?.FullName != command)
                    throw new InvalidOperationException("Native command brick or stock switch inheritance changed.");

                var ctor=Public(commandBrick,".ctor",false,v,i,"System.String","System.String","VoxelEngine.ResizeMode",args);
                Before(ctor,Public(brick,".ctor",false,v,i,"System.String","System.String","VoxelEngine.ResizeMode",args),
                    Need(brick,"set_IsCommand",false,v,b));
                Public(commandBrick,"SetCommandArgs",false,commandBrick+"<T>",args);
                var construct=Public(commandBrick,"OnConstruct",false,ids,coord,b,extra);
                var initialize=Public(command,"Initialize",false,v,coord,ids,b,args);
                Before(construct,Public(brick,"OnConstruct",false,ids,coord,b,extra),Public(commandManager,"GetId",true,i,command));
                Before(construct,Public(commandManager,"GetId",true,i,command),initialize);
                Override("OnInitialize",v,ids,b,args);
                FunctionalCallIndex(initialize,Need(command,"OnInitialize",false,v,ids,b,args));
                FunctionalCallIndex(Public(commandBrick,"OnEntityInitialized",false,v,instance,entity,b),
                    Public(initialized,"OnEntityInitialized",false,v,b));

                Override("OnDestroy",v,unspawn);
                Override("ReleaseResources",v);
                Before(Public(commandBrick,"OnRemove",false,v,coord,ids,unspawn),
                    Public(command,"OnDestroy",false,v,unspawn),Public(commandManager,"ReleaseId",true,v,i));
                foreach (var operation in new[] { "OnGameReload", "ReleaseResources" })
                    FunctionalCallIndex(Public(commandManager,operation,true,v),Public(command,"ReleaseResources",false,v));

                Override("get_CanBeLinkSource",b);
                Override("get_CanBeLinkTarget",b);
                Override("get_AllowMultipleInputs",b);
                Override("CanActivateManually",b,player);
                Override("CanBeLinkTargetFromCommand",b,cmd);
                Override("Activate",v,b,player,f);
                Override("OnPredecessorAdded",v,cmd,b,b);
                Override("OnPredecessorRemoved",v,cmd);
                FunctionalCallIndex(Public("VoxelEngine.CommandCommon","LinkToCommand",true,source,source,cmd,b,b),
                    Public(predecessorCallback,"OnPredecessorAdded",false,v,cmd,b,b));
                FunctionalCallIndex(Public("VoxelEngine.CommandCommon","RemoveTarget",true,source,source,cmd,b),
                    Public(predecessorCallback,"OnPredecessorRemoved",false,v,cmd));
                var activate=Public(stockSwitch,"Activate",false,v,b,player,f);
                FunctionalCallIndex(activate,Public(command,"Activate",false,v,b,player,f));
                FunctionalCallIndex(Need(stockSwitch,"ComputeNewState",false,f,f,b),Public(command,"get_State",false,f), requireUnique:false);
                Public(command,"get_predecessors",false,links);
                Public(command,"get_targets",false,links);
                Public(links,"get_Count",false,i);
                Public(links,"get_Command",false,cmd);
                Public(links,"get_Items",false,links+"/StructEnumerable");
                Public(command,"get_IsRemoved",false,b);
                Public(command,"get_parentEntity",false,parent);
                Public(command,"get_brickCoord",false,coord);
                Public(entity,"get_IsInitialized",false,b);
                Public(entity,"get_WillBeUnspawned",false,b);
                if (!Field(module,coord,"brickData",instance,false) || !Field(module,instance,"brickId","System.UInt16",false)
                    || !Field(module,"UI.Codex.CodexTab","COMMANDS","UI.Codex.CodexTab",true))
                    throw new InvalidOperationException("Functional placement or command catalog fields changed.");
                Public(brick,"get_IsFacingForward",false,b);
                Public(brick,"set_IsFacingForward",false,v,b);
                var templateFields=new[]
                {
                    (brick,"nameMesh","System.String"),
                    (brick,"colliderShape","Manager.ColliderShapeCommon/Shape"),
                    (brick,"boundsOverride","UnityEngine.Bounds"), (brick,"boundsCulling","UnityEngine.Bounds"),
                    (brick,"CollisionWidth",f), (brick,"CollisionScaleFactor",f), (brick,"scaleUV",f), (brick,"massForUnitShape",f),
                    (brick,"gridLevel",i), (brick,"minGridSize",i), (brick,"maxGridSize",i), (brick,"gridLockOffset","Vector3i"),
                    (brick,"isDynamicGridSize",b), (brick,"NoMirroring",b), (brick,"useMaterial",b),
                    (brick,"canPaint",b), (brick,"hasGlowPaint",b), (brick,"isConveyorBelt",b),
                    (brick,"isPipeSeeThrough",b), (brick,"isFuelSeeThrough",b),
                    (brick,"PreviewPaint","UnityEngine.Color32"), (brick,"defaultPaint","UnityEngine.Color32"),
                    (commandBrick,"isDynDataCollider",b), ("VoxelEngine.ResizeMode","unit",f),
                    ("VoxelEngine.VoxelMaterial","dynDataVert","VoxelEngine.DynData_Vert"),
                    ("VoxelEngine.VoxelMaterial","dynDataPixel","VoxelEngine.DynData_Pixel")
                };
                foreach(var (owner,name,fieldType) in templateFields)
                    if(!Field(module,owner,name,fieldType,false))
                        throw new InvalidOperationException("Missing functional template field: "+owner+"."+name);
                Public("PlayerState","get_sessionMode",true,"PlayerState/SessionMode");
                var session = module.GetType("PlayerState")?.NestedTypes.SingleOrDefault(value=>value.Name=="SessionMode");
                if (session?.Fields.SingleOrDefault(field=>field.Name=="solo" && field.IsLiteral)?.Constant is not int solo || solo != 1)
                    throw new InvalidOperationException("Native solo session mode is unavailable.");
                return new CapabilityResult(capability,true,"Native stock switch, placement and command lifecycle contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability,false,exception.Message);
            }
        }

        public static CapabilityResult EvaluateFunctionalEventGate(ModuleDefinition module)
        {
            const string capability="functionalEventGate", gate="VoxelEngine.CommandEventGate", command="VoxelEngine.Command";
            const string v="System.Void", b="System.Boolean", f="System.Single", i="System.Int32";
            const string player="Game.PlayerCommandManager", cmd="VoxelEngine.ICommand";
            const string stateType="VoxelEngine.CommandStateData/StateType";
            const string resources="VoxelEngine.CommandEventGateResources", jobType=resources+"/EventGateJobData";
            try
            {
                MethodDefinition Need(string type,string name,string result,params string[] parameters) =>
                    Method(module,type,name,false,result,parameters)
                    ?? throw new InvalidOperationException("Missing native Event Gate contract: "+type+"."+name);
                var type=module.GetType(gate);
                var dataType=module.GetType("VoxelEngine.CommandStateData")?.NestedTypes.SingleOrDefault(nested=>nested.Name=="StateType");
                var signalType=type?.NestedTypes.SingleOrDefault(nested=>nested.Name=="SignalType");
                var saveType=type?.NestedTypes.SingleOrDefault(nested=>nested.Name=="EventGateDataSave");
                var job=module.GetType(resources)?.NestedTypes.SingleOrDefault(nested=>nested.Name=="EventGateJobData");
                bool PrivateField(string name,string fieldType) => type?.Fields.Any(field=>field.Name==name
                    && field.FieldType.FullName==fieldType && !field.IsStatic && field.IsPrivate)==true;
                bool JobField(string name,string fieldType) => job?.Fields.Any(field=>field.Name==name
                    && field.FieldType.FullName==fieldType && !field.IsStatic && field.IsPublic)==true;
                if(type?.BaseType?.FullName!=command || type.Methods.Any(method=>method.Name is "Propagate" or "get_CanPropagate")
                    || dataType?.Fields.SingleOrDefault(field=>field.Name=="signal")?.Constant is not int signal || signal!=0
                    || signalType?.Fields.SingleOrDefault(field=>field.Name=="toggle")?.Constant is not int toggle || toggle!=0
                    || !PrivateField("stateType",stateType) || !PrivateField("stateDataId",i)
                    || !PrivateField("saveData",gate+"/EventGateDataSave")
                    || saveType?.Methods.Any(method=>method.Name=="get_isInverted" && method.IsPublic && !method.IsStatic
                        && method.ReturnType.FullName==b && !method.HasParameters)!=true
                    || !JobField("stateType",stateType) || !JobField("signalType",gate+"/SignalType")
                    || !JobField("stateDataId",i) || !JobField("State",f)
                    || module.GetType("VoxelEngine.ICommandEventStateProvider")?.IsInterface!=true)
                    throw new InvalidOperationException("The native Event Gate held-signal shape changed.");
                var propagate=Need(command,"Propagate",v);
                var canPropagate=Need(command,"get_CanPropagate",b);
                var canCodes=canPropagate.Body?.Instructions.Select(instruction=>instruction.OpCode.Code)
                    .Where(code=>code!=Code.Nop).ToArray();
                if(!propagate.IsFamily || !propagate.IsVirtual || canCodes==null
                    || !canCodes.SequenceEqual(new[]{Code.Ldc_I4_1,Code.Ret}))
                    throw new InvalidOperationException("The native Event Gate no longer uses unconditional base propagation.");
                FunctionalCallIndex(propagate,canPropagate);
                FunctionalCallIndex(propagate,Need(cmd,"Activate",v,b,player,f));
                FunctionalCallIndex(Need(gate,"PropagateValue",v,f),propagate);
                FunctionalCallIndex(Need(gate,"Activate",v,b,player,f),propagate);
                var bind=Need(gate,"SetStateData",v);
                if(!bind.IsPrivate || !Need(gate,"get_isPulse",b).IsPrivate || !Need(gate,"get_isFireGroup",b).IsPrivate)
                    throw new InvalidOperationException("The native Event Gate mode or binding API changed.");
                FunctionalCallIndex(Need(gate,"OnPredecessorRemoved",v,cmd),bind);
                FunctionalCallIndex(Need(gate,"OnPredecessorAdded",v,cmd,b,b),bind);
                FunctionalCallIndex(Need(gate,"OnGenericParamsUpdated",v,"System.Object",b),bind);
                var stateChanged=Need(gate,"OnStateChanged",v,jobType);
                var callback=Method(module,resources,"DisposeJobCallback",true,v)
                    ?? throw new InvalidOperationException("Missing native Event Gate job dispatch.");
                FunctionalCallIndex(callback,stateChanged);
                return new CapabilityResult(capability,true,"Native Event Gate held propagation and reset contracts validated.");
            }
            catch(Exception exception)
            {
                return new CapabilityResult(capability,false,exception.Message);
            }
        }

        private static int FunctionalCallIndex(MethodDefinition caller, MethodDefinition target, bool requireUnique=true)
        {
            if (!caller.HasBody) throw new InvalidOperationException("Missing native method body: "+caller.FullName);
            var found=caller.Body.Instructions.Select((instruction,index)=>(instruction,index)).Where(entry=>
                (entry.instruction.OpCode.Code==Code.Call || entry.instruction.OpCode.Code==Code.Callvirt)
                && entry.instruction.Operand is MethodReference reference && reference.FullName==target.FullName).ToArray();
            if (found.Length==0 || requireUnique && found.Length!=1)
                throw new InvalidOperationException("Native call changed: "+caller.FullName+" -> "+target.FullName);
            return found[0].index;
        }
    }
}
