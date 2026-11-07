using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks native repeat persistence and the optional managed receiver render correction.</summary>
        public static CapabilityResult EvaluateReceiverRepeats(ModuleDefinition module)
        {
            const string capability = "receiverRepeats";
            const string brick = "VoxelEngine.Brick", data = "VoxelEngine.BrickInstanceData", ids = "VoxelEngine.BrickIds";
            const string entity = "VoxelEngine.VoxelEntity", entities = "VoxelEngine.EntityBricks";
            const string coord = "VoxelEngine.BrickCoord", renderer = "VoxelEngine.EvoRenderer";
            const string extra = "System.Collections.Generic.Dictionary`2<System.Int32,System.Byte[]>";
            const string i = "System.Int32", u = "System.UInt16", b = "System.Boolean", v = "System.Void";

            try
            {
                MethodDefinition Need(string owner, string name, string result, params string[] parameters) =>
                    Method(module, owner, name, false, result, parameters) is { HasBody: true } method ? method
                    : throw new InvalidOperationException("Missing native repeat method: " + owner + "." + name);

                bool Uses(Instruction instruction, Code code, string owner, string name, string type) =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == owner && field.Name == name && field.FieldType.FullName == type;

                bool RepeatFlag(Instruction instruction) => Uses(instruction, Code.Ldfld, brick, "isRepeatBrick", b);

                foreach (var field in new (string Name, string Type)[]
                {
                    ("isRepeatBrick", b), ("repeatBrickId", u), ("repeatEvenBrickId", u), ("repeatOddBrickId", u),
                    ("repeatBrickStart", i), ("repeatBrickEndOffset", i)
                })
                {
                    if (module.GetType(brick)?.Fields.SingleOrDefault(member => member.Name == field.Name
                        && member.FieldType.FullName == field.Type && member.IsPublic && !member.IsStatic && !member.IsInitOnly) == null)
                        throw new InvalidOperationException("Native repeat definition field changed: " + field.Name);
                }

                var bvh = Need(brick, "get_AddToBVH", b).Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                if (bvh.Length != 5 || bvh[0].OpCode.Code != Code.Ldarg_0 || !RepeatFlag(bvh[1])
                    || bvh[2].OpCode.Code != Code.Ldc_I4_0 || bvh[3].OpCode.Code != Code.Ceq || bvh[4].OpCode.Code != Code.Ret)
                    throw new InvalidOperationException("Native repeat bricks no longer bypass independent collision registration.");

                var recycled = Need(brick, "get_UseRecycledId", b).Body.Instructions;
                var reuseTest = recycled.SingleOrDefault(RepeatFlag)?.Next;
                if (reuseTest?.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || reuseTest.Operand is not Instruction { OpCode.Code: Code.Ldc_I4_0, Next.OpCode.Code: Code.Ret })
                    throw new InvalidOperationException("Native repeat bricks no longer allocate adjacent instance IDs.");

                var onAdd = Need(brick, "OnBrickAddedToEntity", v, coord, b, extra);
                var addRepeat = Need(brick, "AddRepeatBrick", v, i, i, i, coord, u);
                var addGate = onAdd.Body.Instructions[1];
                if (onAdd.Body.Instructions[0].OpCode.Code != Code.Ldarg_2
                    || addGate.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                    || addGate.Operand is not Instruction { OpCode.Code: Code.Ret }
                    || !onAdd.Body.Instructions.Any(instruction => Uses(instruction, Code.Ldfld, brick, "repeatBrickId", u))
                    || !addRepeat.Body.Instructions.Any(instruction => Uses(instruction, Code.Stfld, data, "brickId", u)))
                    throw new InvalidOperationException("Native repeat generation no longer preserves saved child IDs.");
                FunctionalCallIndex(onAdd, addRepeat, requireUnique: false);
                FunctionalCallIndex(addRepeat, Need(entities, "Add", i, data));

                var load = Need(entities, "RegisterBrickOnLoad", b, "VoxelEngine.BrickDatasSave", "System.Int32&");
                var loadBody = load.Body.Instructions;
                var loadFlags = loadBody.Where(RepeatFlag).ToArray();
                int hasRepeat = FunctionalCallIndex(load, Need(brick, "get_hasRepeatBrick", b));
                int addOnLoad = FunctionalCallIndex(load, Need(entities, "AddBrickAt", v, data, b, extra));
                if (loadFlags.Length != 2 || loadFlags[0].Next.OpCode.Code is not (Code.Brfalse or Code.Brfalse_S)
                    || loadBody.IndexOf(loadFlags[0]) >= hasRepeat || hasRepeat >= loadBody.IndexOf(loadFlags[1])
                    || loadBody.IndexOf(loadFlags[1]) >= addOnLoad
                    || loadBody.Any(instruction => Uses(instruction, Code.Ldfld, brick, "repeatBrickId", u)
                        || Uses(instruction, Code.Ldfld, brick, "repeatEvenBrickId", u) || Uses(instruction, Code.Ldfld, brick, "repeatOddBrickId", u)))
                    throw new InvalidOperationException("Native loading no longer recognizes stock and owned repeats by adjacency.");

                var update = Need(brick, "UpdateRenderData", v, data, renderer, i, ids, entity, b, b);
                var body = update.Body.Instructions;
                int call = FunctionalCallIndex(update, update);
                int receiver = call - 8;
                bool Arg(Instruction instruction, int index) => instruction.OpCode.Code switch
                {
                    Code.Ldarg_0 => index == 0, Code.Ldarg_1 => index == 1, Code.Ldarg_2 => index == 2, Code.Ldarg_3 => index == 3,
                    Code.Ldarg or Code.Ldarg_S => instruction.Operand is ParameterDefinition parameter && parameter.Index + 1 == index,
                    _ => false
                };
                bool LocalType(Instruction instruction, string type)
                {
                    int index = instruction.OpCode.Code switch
                    {
                        Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3,
                        Code.Ldloc or Code.Ldloc_S when instruction.Operand is VariableDefinition local => local.Index,
                        _ => -1
                    };
                    var locals = update.Body.Variables;
                    return (uint)index < locals.Count && locals[index].VariableType.FullName == type;
                }

                if (receiver < 0 || body[call].OpCode.Code != Code.Call || !Arg(body[receiver], 0)
                    || !LocalType(body[receiver + 1], data) || !Arg(body[receiver + 2], 2) || !Arg(body[receiver + 3], 3)
                    || !LocalType(body[receiver + 4], ids) || !Arg(body[receiver + 5], 5) || !Arg(body[receiver + 6], 6)
                    || body[receiver + 7].OpCode.Code != Code.Ldc_I4_1)
                    throw new InvalidOperationException("Native repeat render recursion operands changed.");

                var operands = body.Skip(receiver + 1).Take(8).ToHashSet();
                if (body.Any(instruction => (instruction.Operand is Instruction target && operands.Contains(target))
                        || (instruction.Operand is Instruction[] targets && targets.Any(operands.Contains)))
                    || update.Body.ExceptionHandlers.Any(handler => operands.Contains(handler.TryStart) || operands.Contains(handler.TryEnd)
                        || operands.Contains(handler.HandlerStart) || operands.Contains(handler.HandlerEnd) || operands.Contains(handler.FilterStart)))
                    throw new InvalidOperationException("Native control flow enters the repeat render operands directly.");

                return new CapabilityResult(capability, true, "Native repeat generation, saved adjacency and render recursion contracts validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
