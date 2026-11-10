using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        public static CapabilityResult EvaluateTerrainDepositNativeHighlights(ModuleDefinition module)
        {
            const string capability = "terrainDepositNativeHighlights";
            const string scanner = "World.Terrain.OreScannerResources";
            const string record = "World.Terrain.OreScannerData";
            const string container = "Common.NativeRecyclableContainer`1<" + record + ">";
            const string list = "Unity.Collections.NativeList`1<" + record + ">";
            const string array = "Unity.Collections.NativeArray`1<" + record + ">";
            const string widget = scanner + "/JobComputeOreWidgets";
            const string jobs = "World.Terrain.OctreeNodeJobs";
            const string geo = jobs + "/Job_GeoCompute";
            const string handle = "Unity.Jobs.JobHandle";
            const string integer = "System.Int32";

            try
            {
                var depositScanner = EvaluateTerrainDepositScanner(module);
                if (!depositScanner.Available) throw new InvalidOperationException(depositScanner.Reason);
                if (module.GetType(scanner)?.Fields.Any(field => field.Name == "datas" && field.IsStatic
                    && field.FieldType.FullName == container) != true
                    || !Field(module, record, "nextElementId", integer, false))
                    throw new InvalidOperationException("Native ore record storage changed.");

                var registration = Method(module, scanner, "RegisterData", true, integer, list);
                var register = registration?.Body?.Instructions;
                var link = register?.SingleOrDefault(instruction =>
                    TerrainProviderField(instruction, Code.Stfld, record, "nextElementId"));
                var add = register?.SingleOrDefault(instruction => TerrainPlacementCall(instruction, container, "GetIdAndSetFast"));
                if (registration?.IsPublic != true || register == null || link == null || add == null
                    || link.Previous?.OpCode.Code != Code.Ldloc_0
                    || add.Next?.OpCode.Code != Code.Stloc_0
                    || register[0].OpCode.Code != Code.Ldc_I4_0 || register[1].OpCode.Code != Code.Stloc_0
                    || register[register.Count - 2].OpCode.Code != Code.Ldloc_0
                    || register[register.Count - 1].OpCode.Code != Code.Ret
                    || register.IndexOf(link) >= register.IndexOf(add)
                    || !register.Any(instruction => TerrainPlacementCall(instruction, list, "get_Item")
                        && instruction.Previous?.OpCode.Code == Code.Ldloc_1)
                    || !register.Any(instruction => instruction.OpCode.Code == Code.Stloc_1
                        && instruction.Previous?.OpCode.Code == Code.Add
                        && instruction.Previous.Previous?.OpCode.Code == Code.Ldc_I4_1
                        && instruction.Previous.Previous.Previous?.OpCode.Code == Code.Ldloc_1))
                    throw new InvalidOperationException("Native ore records no longer register in a reverse-linked chain.");

                var publication = Method(module, jobs, "OnComputeGeoJobFinished", true, "System.Void", geo);
                var publish = publication?.Body?.Instructions;
                var registerCall = publish?.SingleOrDefault(instruction =>
                    TerrainPlacementCall(instruction, scanner, "RegisterData"));
                if (registerCall?.Previous == null || registerCall.Previous.Previous?.OpCode.Code != Code.Ldarg_0
                    || !TerrainProviderField(registerCall.Previous, Code.Ldfld, geo, "oreScannerDatas")
                    || publication == null || HasNativeHighlightControlFlowEntry(publication, registerCall)
                    || module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
                        .SelectMany(method => method.Body.Instructions)
                        .Count(instruction => instruction.Operand is MethodReference target
                            && target.FullName == registration.FullName) != 1)
                    throw new InvalidOperationException("Native ore registration no longer receives the completed geometry job records.");

                var update = Method(module, scanner, "UpdateData", true, "System.Void", integer + "&", "Vector3i");
                var release = Method(module, scanner, "ReleaseData", true, "System.Void", integer);
                foreach (var method in new[] { update, release })
                {
                    var instructions = method?.Body?.Instructions;
                    if (method?.IsPublic != true || instructions == null
                        || !instructions.Any(instruction => TerrainProviderField(instruction, Code.Ldfld, record, "nextElementId"))
                        || instructions.Count(instruction => TerrainPlacementCall(instruction, container, "ReleaseId")) != 1)
                        throw new InvalidOperationException("Native ore row release lifecycle changed.");
                }
                if (update?.Body.Instructions.Any(instruction => TerrainProviderField(instruction, Code.Ldfld, record, "coordinate")
                    && instruction.Next?.OpCode.Code == Code.Ldarg_1
                    && instruction.Next.Next != null
                    && TerrainPlacementCall(instruction.Next.Next, "Vector3i", "op_Equality")) != true)
                    throw new InvalidOperationException("Native ore edit no longer releases a coordinate-matched record.");
                foreach (var (name, reset) in new[] { ("OnGameReload", "OnGameReload"), ("ReleaseResources", "Dispose") })
                {
                    var instructions = Method(module, scanner, name, true, "System.Void")?.Body?.Instructions;
                    if (instructions?.Any(instruction => TerrainProviderField(instruction, Code.Ldsflda, scanner, "datas")
                        && instruction.Next != null && TerrainPlacementCall(instruction.Next, container, reset)) != true)
                        throw new InvalidOperationException("Native ore record reset lifecycle changed.");
                }

                var scheduler = Method(module, scanner, "ScheduleFromGraph", true, handle, handle);
                var schedule = scheduler?.Body?.Instructions;
                var store = schedule?.SingleOrDefault(instruction => TerrainProviderField(instruction, Code.Stfld, widget, "oreDatas"));
                var ctor = store?.Previous;
                var source = ctor?.Previous?.Previous;
                if (scheduler == null || schedule == null || store == null || HasNativeHighlightControlFlowEntry(scheduler, store)
                    || ctor?.OpCode.Code != Code.Newobj
                    || ctor.Operand is not MethodReference constructor || constructor.DeclaringType.FullName != array
                    || constructor.Name != ".ctor" || constructor.Parameters.Count != 2
                    || constructor.Parameters[0].ParameterType.FullName != "Unity.Collections.NativeArray`1<!0>"
                    || constructor.Parameters[1].ParameterType.FullName != "Unity.Collections.Allocator"
                    || ctor.Previous?.OpCode.Code != Code.Ldc_I4_3 || source == null
                    || !TerrainPlacementCall(source, container, "op_Implicit") || source.Previous == null
                    || !TerrainProviderField(source.Previous, Code.Ldsfld, scanner, "datas")
                    || !schedule.Skip(schedule.IndexOf(store) + 1).Any(instruction =>
                        instruction.Operand is GenericInstanceMethod call && call.Name == "Schedule"
                        && call.DeclaringType.FullName == "Unity.Jobs.IJobExtensions"
                        && call.GenericArguments.Count == 1 && call.GenericArguments[0].FullName == widget))
                    throw new InvalidOperationException("Native widget job no longer receives a private ore array copy.");

                var execute = Method(module, widget, "Execute", false, "System.Void")?.Body?.Instructions;
                var transform = execute?.FirstOrDefault(instruction =>
                    TerrainProviderField(instruction, Code.Ldfld, record, "transformId"));
                var branch = transform?.Next;
                if (execute == null || transform == null || branch?.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || branch.Operand is not Instruction skip || skip.OpCode.Code != Code.Ldloc_0
                    || skip.Next?.OpCode.Code != Code.Ldc_I4_1 || skip.Next.Next?.OpCode.Code != Code.Add
                    || skip.Next.Next.Next?.OpCode.Code != Code.Stloc_0
                    || !execute.Skip(execute.IndexOf(transform) + 2).Take(execute.IndexOf(skip) - execute.IndexOf(transform) - 2)
                        .Any(instruction => TerrainProviderField(instruction, Code.Stfld, "UI.TrackingData", "spriteType")))
                    throw new InvalidOperationException("Native widget job no longer skips zero transforms before tracking allocation.");

                var render = Method(module, scanner, "OnRenderAfterCompleteCallback", true, "System.Void", "Common.RenderPipelineBuffers")
                    ?.Body?.Instructions;
                var upload = render?.SingleOrDefault(instruction =>
                    TerrainPlacementCall(instruction, "Common.ComputeBufferHandler`1<" + record + ">", "PushData"));
                if (render == null || upload == null || !render.Take(render.IndexOf(upload))
                        .Any(instruction => TerrainProviderField(instruction, Code.Ldsfld, scanner, "datas"))
                    || !render.Skip(render.IndexOf(upload) + 1).Any(instruction =>
                        TerrainPlacementCall(instruction, "UnityEngine.Rendering.CommandBuffer", "DrawProcedural")))
                    throw new InvalidOperationException("Native per-sample highlight draw no longer consumes the original ore records.");

                return new CapabilityResult(capability, true,
                    "Native per-sample highlight upload, reverse row chain, and separate widget array validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }

        private static bool HasNativeHighlightControlFlowEntry(MethodDefinition method, Instruction target)
            => method.Body.Instructions.Any(instruction => ReferenceEquals(instruction.Operand, target)
                || instruction.Operand is Instruction[] branches && branches.Contains(target))
                || method.Body.ExceptionHandlers.Any(handler => ReferenceEquals(handler.TryStart, target)
                    || ReferenceEquals(handler.TryEnd, target) || ReferenceEquals(handler.HandlerStart, target)
                    || ReferenceEquals(handler.HandlerEnd, target) || ReferenceEquals(handler.FilterStart, target));
    }
}
