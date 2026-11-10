using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Common;
using HarmonyLib;
using Manager;
using Unity.Collections;
using World;
using World.Terrain;

namespace Sevo.Framework.Runtime
{
    // The game draws these original ore records separately from the cloned widget-job array.
    internal static class TerrainDepositNativeHighlights
    {
        private const int TrackedRowLimit = 262144;
        private static readonly HashSet<int> CustomRows = new HashSet<int>();
        private static AccessTools.FieldRef<NativeRecyclableContainer<OreScannerData>>? readData;
        private static Action<string>? warning;
        private static int mainThread;

        internal static bool IsAvailable => warning != null;

        internal static void Install(Harmony patches, Action<string> reportWarning)
        {
            var scanner = typeof(OreScannerResources);
            var data = AccessTools.DeclaredField(scanner, "datas")
                ?? throw new MissingFieldException(scanner.FullName, "datas");
            readData = AccessTools.StaticFieldRefAccess<NativeRecyclableContainer<OreScannerData>>(data);

            void Patch(Type owner, string method, string? prefix = null, string? transpiler = null)
            {
                var target = AccessTools.DeclaredMethod(owner, method)
                    ?? throw new MissingMethodException(owner.FullName, method);
                patches.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(TerrainDepositNativeHighlights), prefix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(TerrainDepositNativeHighlights), transpiler));
            }

            Patch(typeof(OctreeNodeJobs), "OnComputeGeoJobFinished", transpiler: nameof(BridgeRegistration));
            Patch(scanner, "ScheduleFromGraph", transpiler: nameof(BridgeWidgetCopy));
            Patch(scanner, nameof(OreScannerResources.UpdateData), prefix: nameof(BeforeUpdate));
            Patch(scanner, nameof(OreScannerResources.ReleaseData), prefix: nameof(BeforeRelease));
            Patch(scanner, nameof(OreScannerResources.OnGameReload), prefix: nameof(Clear));
            Patch(scanner, nameof(OreScannerResources.ReleaseResources), prefix: nameof(Clear));

            mainThread = Environment.CurrentManagedThreadId;
            warning = reportWarning;
        }

        internal static void Disable()
        {
            warning = null;
            readData = null;
            Clear();
        }

        private static bool ActiveOnMainThread()
            => IsAvailable && Environment.CurrentManagedThreadId == mainThread;

        private static void Clear() => CustomRows.Clear();

        private static void Fail(Exception error)
        {
            var report = warning;
            Disable();
            report?.Invoke("Native terrain deposit highlight separation disabled: " + error.Message);
        }

        private static IEnumerable<CodeInstruction> BridgeRegistration(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var native = AccessTools.DeclaredMethod(typeof(OreScannerResources), nameof(OreScannerResources.RegisterData));
            var bridge = AccessTools.DeclaredMethod(typeof(TerrainDepositNativeHighlights), nameof(RegisterRows));
            var job = typeof(OctreeNodeJobs.Job_GeoCompute);
            var source = AccessTools.DeclaredField(job, nameof(OctreeNodeJobs.Job_GeoCompute.oreScannerDatas));
            var voxels = AccessTools.DeclaredField(job, nameof(OctreeNodeJobs.Job_GeoCompute.voxelDatas));
            var node = AccessTools.DeclaredField(job, nameof(OctreeNodeJobs.Job_GeoCompute.node));
            int index = code.FindIndex(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, native));
            if (native == null || bridge == null || source == null || voxels == null || node == null
                || index < 2 || code[index].blocks.Count != 0
                || code[index - 2].opcode != OpCodes.Ldarg_0 || code[index - 1].opcode != OpCodes.Ldfld
                || !Equals(code[index - 1].operand, source)
                || code.Count(instruction => instruction.opcode == OpCodes.Call && Equals(instruction.operand, native)) != 1)
                throw new InvalidOperationException("Live native ore record registration boundary changed.");

            code[index].operand = bridge;
            code.InsertRange(index, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, voxels),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, node)
            });
            return code;
        }

        private static IEnumerable<CodeInstruction> BridgeWidgetCopy(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var scanner = typeof(OreScannerResources);
            var data = AccessTools.DeclaredField(scanner, "datas");
            var job = AccessTools.Inner(scanner, "JobComputeOreWidgets");
            var copyField = job == null ? null : AccessTools.DeclaredField(job, "oreDatas");
            var bridge = AccessTools.DeclaredMethod(typeof(TerrainDepositNativeHighlights), nameof(FilterWidgetCopy));
            int store = code.FindIndex(instruction => instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, copyField));
            if (data == null || copyField == null || bridge == null || store < 4
                || code[store].blocks.Count != 0
                || code[store - 1].opcode != OpCodes.Newobj
                || code[store - 1].operand is not ConstructorInfo constructor
                || constructor.DeclaringType != typeof(NativeArray<OreScannerData>)
                || !constructor.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(NativeArray<OreScannerData>), typeof(Allocator) })
                || code[store - 4].opcode != OpCodes.Ldsfld || !Equals(code[store - 4].operand, data)
                || code.Count(instruction => instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, copyField)) != 1)
                throw new InvalidOperationException("Live native widget job no longer receives a private ore array.");

            code.Insert(store, new CodeInstruction(OpCodes.Call, bridge));
            return code;
        }

        private static int RegisterRows(NativeList<OreScannerData> records, NativeArray<VoxelNodeData> voxels, OctreeNodeData node)
        {
            int head = OreScannerResources.RegisterData(records);
            if (!ActiveOnMainThread() || head == 0) return head;
            try
            {
                if (!records.IsCreated || records.Length > OctreeNodeData.DENSITY_COUNT)
                    throw new InvalidOperationException("Native ore record count exceeded the node bounds.");
                var data = readData!();
                int id = head;
                for (int i = records.Length - 1; i >= 0; i--)
                {
                    var live = ReadRow(data, id);
                    var input = records[i];
                    if (live.coordinate != input.coordinate || live.transformId != input.transformId || live.texId != input.texId)
                        throw new InvalidOperationException("Native ore row does not match its source record.");
                    CustomRows.Remove(id);
                    if (IsCustomRecord(input, voxels, node))
                    {
                        if (CustomRows.Count == TrackedRowLimit)
                            throw new InvalidOperationException("Native custom ore row budget exceeded.");
                        CustomRows.Add(id);
                    }
                    id = live.nextElementId;
                }
                if (id != 0) throw new InvalidOperationException("Native ore registration returned a longer chain than expected.");
            }
            catch (Exception error) { Fail(error); }
            return head;
        }

        private static bool IsCustomRecord(OreScannerData record, NativeArray<VoxelNodeData> voxels, OctreeNodeData node)
        {
            var coord = record.coordinate;
            if (!TerrainDepositScanner.IsAvailable || record.transformId == 0 || record.transformId != node.transformId
                || !voxels.IsCreated || voxels.Length != OctreeNodeData.DENSITY_COUNT
                || (uint)coord.x >= OctreeNodeData.DENSITY_WIDTH || (uint)coord.y >= OctreeNodeData.DENSITY_WIDTH
                || (uint)coord.z >= OctreeNodeData.DENSITY_WIDTH)
                return false;

            var voxel = voxels[VoxelNodeData.ToDensityId(coord.x, coord.y, coord.z)];
            int material = voxel.MainTexId;
            if (voxel.density >= 0 || !TerrainDepositScanner.HasDeclaredHighlight(material)) return false;
            var table = WorldTerrainResources.TerrainMaterialDatas;
            return table.IsCreated && (uint)material < table.Length
                && MaterialGradientGenerator.GetMetalTextTexId(table[material].gradientId) == record.texId;
        }

        private static OreScannerData ReadRow(NativeRecyclableContainer<OreScannerData> data, int id)
        {
            if (!data.IsCreated || id <= 0 || id >= data.Length)
                throw new InvalidOperationException("Native ore row link is invalid.");
            return data[id];
        }

        private static void BeforeUpdate(ref int __0, Vector3i __1)
        {
            if (ActiveOnMainThread() && CustomRows.Count != 0) ForgetReleasedRows(__0, __1);
        }

        private static void BeforeRelease(int __0)
        {
            if (ActiveOnMainThread() && CustomRows.Count != 0) ForgetReleasedRows(__0, null);
        }

        private static void ForgetReleasedRows(int id, Vector3i? coordinate)
        {
            try
            {
                var data = readData!();
                for (int visited = 0; id != 0; visited++)
                {
                    if (visited >= OctreeNodeData.DENSITY_COUNT)
                        throw new InvalidOperationException("Native ore row chain exceeded the node bounds.");
                    var row = ReadRow(data, id);
                    if (!coordinate.HasValue || row.coordinate == coordinate.Value)
                    {
                        CustomRows.Remove(id);
                        if (coordinate.HasValue) return;
                    }
                    id = row.nextElementId;
                }
            }
            catch (Exception error) { Fail(error); }
        }

        internal static void TraceNearby(Vector3l origin, Action<string> report)
        {
            if (!ActiveOnMainThread()) return;
            try
            {
                var data = readData!();
                if (!data.IsCreated || !SkyTransformData.datas.IsCreated) return;
                var pulseOrigin = PositionManager.ToWorldPosition(origin);
                int ironGlyph = MaterialGradientGenerator.GetMetalTextTexId(MaterialGradientType.rawIron);
                int custom = 0, ironPath = 0;
                float nearestCustom = float.PositiveInfinity, nearestIronPath = float.PositiveInfinity;
                int limit = Math.Min(data.Length, 65537);
                for (int i = 1; i < limit; i++)
                {
                    var row = data[i];
                    if (row.transformId <= 0 || row.transformId >= SkyTransformData.datas.Length) continue;
                    bool isCustom = CustomRows.Contains(i);
                    if (!isCustom && row.texId != ironGlyph) continue;
                    var position = SkyTransformData.datas[row.transformId].LocalToSystem(row.localPosition);
                    float distance = UnityEngine.Vector3.Distance(PositionManager.ToWorldPosition(position), pulseOrigin);
                    if (!(distance <= OreScannerResources.PLAYER_SCANNER_RANGE)) continue;
                    if (isCustom)
                    {
                        custom++;
                        nearestCustom = Math.Min(nearestCustom, distance);
                    }
                    else
                    {
                        ironPath++;
                        nearestIronPath = Math.Min(nearestIronPath, distance);
                    }
                }
                report($"KEEL_DEPOSIT_HIGHLIGHTS rowsInspected={limit - 1}/{data.Length - 1} custom={custom} nearestCustomM={nearestCustom:F1} ironGlyphPath={ironPath} nearestIronGlyphM={nearestIronPath:F1}");
            }
            catch (Exception error) { report("Terrain deposit highlight trace unavailable: " + error.Message); }
        }

        private static NativeArray<OreScannerData> FilterWidgetCopy(NativeArray<OreScannerData> copy)
        {
            if (!ActiveOnMainThread() || CustomRows.Count == 0 || !copy.IsCreated) return copy;
            try
            {
                foreach (int id in CustomRows)
                {
                    if ((uint)id >= copy.Length) continue;
                    var row = copy[id];
                    // The widget loop skips zero transforms before grouping or allocating tracking IDs.
                    row.transformId = 0;
                    copy[id] = row;
                }
            }
            catch (Exception error) { Fail(error); }
            return copy;
        }
    }
}
