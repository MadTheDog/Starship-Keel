using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VoxelEngine;

namespace Sevo.Framework.Runtime
{
    internal static class FunctionalEventGate
    {
        private const BindingFlags NativeMember = BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly;
        private static FieldInfo? stateType;
        private static FieldInfo? stateDataId;
        private static FieldInfo? saveData;
        private static Func<CommandEventGate,bool>? isPulse;
        private static Func<CommandEventGate,bool>? isFireGroup;
        private static readonly ConditionalWeakTable<CommandEventGate,PendingLevel> Pending =
            new ConditionalWeakTable<CommandEventGate,PendingLevel>();

        internal static bool Enabled { get; set; }

        internal static void Install(Harmony harmony)
        {
            var gate=typeof(CommandEventGate);
            stateType=gate.GetField("stateType",NativeMember);
            stateDataId=gate.GetField("stateDataId",NativeMember);
            saveData=gate.GetField("saveData",NativeMember);
            isPulse=ReadPredicate("get_isPulse");
            isFireGroup=ReadPredicate("get_isFireGroup");
            if(stateType?.FieldType!=typeof(CommandStateData.StateType) || stateDataId?.FieldType!=typeof(int)
                || saveData?.FieldType!=typeof(CommandEventGate.EventGateDataSave))
                throw new InvalidOperationException("Native Event Gate state fields changed.");

            Patch(typeof(Command),"Propagate",nameof(AfterPropagate),false,NativeMember,Type.EmptyTypes);
            Patch(gate,"SetStateData",nameof(AfterStateBindingChanged),false,NativeMember,Type.EmptyTypes);
            Patch(gate,"OnStateChanged",nameof(BeforeStateChanged),true,
                BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly,
                new[]{typeof(CommandEventGateResources.EventGateJobData)});

            Func<CommandEventGate,bool> ReadPredicate(string name)
            {
                var method=gate.GetMethod(name,NativeMember,null,Type.EmptyTypes,null)
                    ?? throw new InvalidOperationException("Missing Event Gate method: "+name);
                return (Func<CommandEventGate,bool>)method.CreateDelegate(typeof(Func<CommandEventGate,bool>));
            }

            void Patch(Type type,string name,string hook,bool prefix,BindingFlags flags,Type[] parameters)
            {
                var method=type.GetMethod(name,flags,null,parameters,null)
                    ?? throw new InvalidOperationException("Missing Event Gate hook: "+name);
                var patch=new HarmonyMethod(typeof(FunctionalEventGate).GetMethod(hook,BindingFlags.Static|BindingFlags.NonPublic));
                harmony.Patch(method,prefix:prefix ? patch : null,postfix:prefix ? null : patch);
            }
        }

        internal static bool IsSource(ICommand source) => Enabled && source.GetType()==typeof(CommandEventGate);

        private static bool IsHeldSource(CommandEventGate source,int dataId)
        {
            var inputs=source.predecessors;
            return dataId>0 && !source.IsRemoved && inputs.Count==1 && inputs.Command is ICommandEventStateProvider
                && (int)stateDataId!.GetValue(source)==dataId
                && (CommandStateData.StateType)stateType!.GetValue(source)==CommandStateData.StateType.signal
                && !isPulse!(source) && !isFireGroup!(source);
        }

        private static void BeforeStateChanged(CommandEventGate __instance,CommandEventGateResources.EventGateJobData __0)
        {
            if(!IsSource(__instance) || __instance.targets.Count==0) return;
            Pending.Remove(__instance);
            if(__0.stateType!=CommandStateData.StateType.signal || __0.signalType!=CommandEventGate.SignalType.toggle
                || (__0.State!=0f && __0.State!=1f) || !IsHeldSource(__instance,__0.stateDataId)) return;
            // A player gate can reach Propagate through its native local RPC. Retain only the latest
            // typed held level; a delayed pulse or generic Activate cannot create a pending level.
            var config=(CommandEventGate.EventGateDataSave)saveData!.GetValue(__instance);
            float level=config.isInverted ? 1f-__0.State : __0.State;
            Pending.Add(__instance,new PendingLevel(__0.stateDataId,level));
        }

        private static void AfterPropagate(Command __instance)
        {
            if(!Enabled || __instance.GetType()!=typeof(CommandEventGate)) return;
            var source=(CommandEventGate)__instance;
            if(!Pending.TryGetValue(source,out var pending) || source.State!=pending.Level) return;
            Pending.Remove(source);
            if(IsHeldSource(source,pending.StateDataId)) KeelFunctionalCommand.DeliverInput(source,pending.Level==1f,true);
        }

        private static void AfterStateBindingChanged(CommandEventGate __instance)
        {
            // Native unlink and configuration can reset the input without sending a low signal.
            if(!IsSource(__instance)) return;
            Pending.Remove(__instance);
            KeelFunctionalCommand.DeliverInput(__instance,false);
        }

        private sealed class PendingLevel
        {
            internal int StateDataId { get; }
            internal float Level { get; }
            internal PendingLevel(int stateDataId,float level) { StateDataId=stateDataId; Level=level; }
        }
    }
}
