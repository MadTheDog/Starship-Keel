using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Sevo.Framework.Sdk;

namespace Sevo.Framework.Runtime
{
    public static partial class ContractAudit
    {
        /// <summary>Checks the optional native paint values on new placements and item previews.</summary>
        public static CapabilityResult EvaluateAttachmentDefaultPaint(ModuleDefinition module)
        {
            const string capability = "attachmentDefaultPaint";
            const string brick = "VoxelEngine.Brick", instance = "VoxelEngine.BrickInstanceData";
            const string color = "UnityEngine.Color32", noResult = "System.Void";
            const string defaultPaint = "defaultPaint", previewPaint = "PreviewPaint";

            try
            {
                foreach (var name in new[] { defaultPaint, previewPaint })
                {
                    if (module.GetType(brick)?.Fields.SingleOrDefault(field => field.Name == name && field.IsPublic
                        && !field.IsStatic && !field.IsInitOnly && field.FieldType.FullName == color) == null)
                        throw new InvalidOperationException("Native brick paint field is unavailable: " + name);
                }

                Instruction[] Body(string owner, string name, string result, params string[] parameters) =>
                    Method(module, owner, name, false, result, parameters) is { HasBody: true } method
                        ? method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray()
                        : throw new InvalidOperationException("Missing native paint consumer: " + owner + "." + name);

                bool PaintField(Instruction instruction, Code code, string owner, string name) =>
                    instruction.OpCode.Code == code && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName == owner && field.Name == name && field.FieldType.FullName == color;

                bool WriteInstanceColor(Instruction instruction) => PaintField(instruction, Code.Stfld, instance, "color");

                void CheckDirectReads(Instruction[] body, string field, int count)
                {
                    int reads = 0;
                    for (int index = 0; index < body.Length; index++)
                        if (PaintField(body[index], Code.Ldfld, brick, field))
                        {
                            if (index + 1 >= body.Length || !WriteInstanceColor(body[index + 1]))
                                throw new InvalidOperationException("Native color no longer consumes brick " + field + ".");
                            reads++;
                        }
                    if (reads != count) throw new InvalidOperationException("Native paint consumer changed for brick " + field + ".");
                }

                var placement = Body("VoxelEngine.ActionBuild", "OnIsSelected", noResult, "Game.PlayerCommandManager");
                int defaultRead = Array.FindIndex(placement, instruction => PaintField(instruction, Code.Ldfld, brick, defaultPaint));
                int colorWrite = Array.FindIndex(placement, WriteInstanceColor);
                int Local(Instruction instruction, bool store) => (instruction.OpCode.Code, store) switch
                {
                    (Code.Stloc_0, true) or (Code.Ldloc_0, false) => 0,
                    (Code.Stloc_1, true) or (Code.Ldloc_1, false) => 1,
                    (Code.Stloc_2, true) or (Code.Ldloc_2, false) => 2,
                    (Code.Stloc_3, true) or (Code.Ldloc_3, false) => 3,
                    (Code.Stloc or Code.Stloc_S, true) or (Code.Ldloc or Code.Ldloc_S, false) =>
                        instruction.Operand is VariableDefinition local ? local.Index : -1,
                    _ => -1
                };
                if (defaultRead < 0 || colorWrite <= defaultRead + 1)
                    throw new InvalidOperationException("Native placement no longer copies the definition's default paint.");
                int paintLocal = Local(placement[defaultRead + 1], store: true);
                if (paintLocal < 0 || Local(placement[colorWrite - 1], store: false) != paintLocal)
                    throw new InvalidOperationException("Native placement no longer copies the definition's default paint.");

                CheckDirectReads(Body("VoxelEngine.ActionPaint", "ColorBuildItem", noResult, "System.Boolean"), defaultPaint, 1);
                CheckDirectReads(Body(brick, "get_GetPreviewData", instance), previewPaint, 1);
                CheckDirectReads(Body(brick, "get_PreviewBrickData", instance), previewPaint, 1);
                CheckDirectReads(Body("VoxelEngine.ItemPreview", "ComputeGeoItem", noResult), previewPaint, 2);

                return new CapabilityResult(capability, true, "Native placement, paint eraser and item preview paint fields validated.");
            }
            catch (Exception exception)
            {
                return new CapabilityResult(capability, false, exception.Message);
            }
        }
    }
}
