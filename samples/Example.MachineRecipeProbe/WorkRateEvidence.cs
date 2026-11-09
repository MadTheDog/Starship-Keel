using System;
using System.Collections;
using System.Diagnostics;
using BepInEx;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using VoxelEngine.Crafting;
using World;

namespace Sevo.Example.MachineRecipeProbe
{
    public sealed partial class Plugin
    {
        private const float WorkRateCraftSeconds = 4f;
        private static bool WorkRateRequested => Environment.GetEnvironmentVariable("SEVO_KEEL_MACHINE_PROBE_WORK_RATE") == "1";

        private IEnumerator ExerciseWorkRate(Stopwatch timer, int unit, int furnaceId)
        {
            string? state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            Require(state != null && Application.companyName == "SEVO Keel Conn Lab 010"
                && Application.productName == "SEVO CT Test", "The work-rate fixture requires the dedicated connector lab.");
            ProfileProtocol.Open(state!, Paths.GameRootPath, session);

            Step("work-rate-placing-control-and-module");
            var controlData = BrickAt(TabStorage.FURNACE, 6 * unit, unit, 0);
            var moduleData = BrickAt(TabHull.HULL_A, 6 * unit, unit, 2 * unit);
            Place(controlData);
            Place(moduleData);
            FurnaceCommandData control = default;
            BrickCoord module = default;
            while (!Safe() || !TryCommand(controlData, out control) || !TryWorkRatePlacement(moduleData, out module))
            {
                Deadline(timer);
                yield return null;
            }

            var boosted = FurnaceCommandData.datas[furnaceId];
            Require(control.CommandId != furnaceId && !control.isCrucible && !Power().IsStalled,
                "The paired powered Arc Furnace fixture is unavailable.");
            Require(Count(boosted.StorageId, dust) == 0f && Count(control.StorageId, dust) == 0f,
                "The comparison furnaces must start with empty custom output storage.");
            var selection = new[] { new BlockAssemblyPart("rate-module", module) };
            Require(BlockAssemblyProvider.TryBind(grid!, boosted.brickCoord, selection, out var binding)
                && binding != null && binding.TryGetParts(out var resolved) && resolved.Count == 1
                && resolved[0].Placement.brickData.instanceId == module.brickData.instanceId,
                "The live assembly did not resolve the selected module placement.");
            Require(MachineWorkRateModifier.TryAttach(binding!, 2f, out var handle) && handle != null,
                "The native work-rate modifier refused the complete furnace assembly.");

            using (handle)
            {
                int boostedInput = AddWorkRateOre(furnaceId);
                int controlInput = AddWorkRateOre(control.CommandId);
                Step("work-rate-measuring-paired-progress");
                float boostedBefore = 0f;
                float controlBefore = 0f;
                do
                {
                    Deadline(timer);
                    yield return null;
                    if (!Safe()) continue;
                    boostedBefore = FurnaceCommandData.datas[furnaceId].completion;
                    controlBefore = FurnaceCommandData.datas[control.CommandId].completion;
                } while (!Safe() || controlBefore < 0.12f || boostedBefore <= 0f);

                float rate = boostedBefore / controlBefore;
                Require(controlBefore < 0.32f && rate is >= 1.90f and <= 2.10f,
                    $"Paired native progress did not show the 2x work rate (boosted={boostedBefore:R}, control={controlBefore:R}).");
                Logger.LogInfo(FormattableString.Invariant($"KEEL_MACHINE_PROBE WORK_RATE BOOST boosted={boostedBefore:R} control={controlBefore:R} ratio={rate:R} session={session}"));

                var delete = new VoxelActionData { entityID = grid!.ID, brickData = module.brickData, type = VoxelActionType.delete };
                Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(delete, false, 0);
                Step("work-rate-removing-native-module");
                while (!Safe() || binding!.IsComplete())
                {
                    Deadline(timer);
                    yield return null;
                }
                Require(!binding!.TryGetParts(out _) && !TryWorkRatePlacement(moduleData, out _),
                    "Assembly inspection or native placement still sees the deleted module.");

                float boostedAfterRemoval = FurnaceCommandData.datas[furnaceId].completion;
                float controlAfterRemoval = FurnaceCommandData.datas[control.CommandId].completion;
                Require(boostedAfterRemoval >= boostedBefore && controlAfterRemoval >= controlBefore,
                    "Module removal reset earned furnace progress.");

                float boostedAfter = boostedAfterRemoval;
                float controlAfter = controlAfterRemoval;
                do
                {
                    Deadline(timer);
                    yield return null;
                    if (!Safe()) continue;
                    boostedAfter = FurnaceCommandData.datas[furnaceId].completion;
                    controlAfter = FurnaceCommandData.datas[control.CommandId].completion;
                } while (!Safe() || controlAfter - controlAfterRemoval < 0.12f);

                float revertedRate = (boostedAfter - boostedAfterRemoval) / (controlAfter - controlAfterRemoval);
                Require(revertedRate is >= 0.95f and <= 1.05f && boostedAfter > controlAfter,
                    $"Removing the live module did not return to 1x while retaining the progress lead (ratio={revertedRate:R}).");
                Logger.LogInfo(FormattableString.Invariant($"KEEL_MACHINE_PROBE WORK_RATE REVERT ratio={revertedRate:R} retainedLead={boostedAfter - controlAfter:R} session={session}"));

                Step("work-rate-awaiting-exact-output");
                while (!Safe() || Count(boosted.StorageId, dust) < dust.quantity || Count(control.StorageId, dust) < dust.quantity)
                {
                    Deadline(timer);
                    yield return null;
                }
                var stockIngot = TabSchematics.Schematic_IronIngots.output;
                Require(Count(boosted.StorageId, dust) == dust.quantity && Count(control.StorageId, dust) == dust.quantity
                    && Count(boosted.StorageId, stockIngot) == 0f && Count(control.StorageId, stockIngot) == 0f
                    && FurnaceCommandData.datas[furnaceId].inputItemId != boostedInput
                    && FurnaceCommandData.datas[control.CommandId].inputItemId != controlInput,
                    "Paired furnaces did not commit one exact custom batch each.");

                // Keep subsequent conveyor and save/load probe inputs unchanged.
                var extra = CraftingCommon.PopupCraftingElement(boosted.StorageId, dust, StorageCommon.CONTEXT);
                Require(extra.id != 0 && extra.quantity == dust.quantity && Count(boosted.StorageId, dust) == 0f,
                    "Could not remove the comparison batch from the primary furnace.");
                ItemInstanceResources.ReleaseItemId(extra.id);
                Logger.LogInfo($"KEEL_MACHINE_PROBE WORK_RATE PASS moduleDeleted=true outputEach={dust.quantity} session={session}");
            }
        }

        private bool TryWorkRatePlacement(BrickInstanceData expected, out BrickCoord placement)
        {
            var live = grid!.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(expected));
            placement = new BrickCoord(grid.ID, live);
            return live.IsValid && live.healthScore > 0 && live.brickId == expected.brickId
                && live.gridPosition == expected.gridPosition && live.gridLevel == expected.gridLevel;
        }

        private int AddWorkRateOre(int commandId)
        {
            var furnace = FurnaceCommandData.datas[commandId];
            Require(furnace.inputItemId == 0 && furnace.completion == 0f,
                "The comparison furnace was not idle before seeding contextual ore.");
            var input = ore.ToItemData;
            input.prefabId = (int)WorldPropCommon.WorldPropId.rockSmallB;
            input.RegisterAndPushItself(StorageCommon.CONTEXT);
            furnace.inputItemId = input.id;
            furnace.PushItself();
            return input.id;
        }
    }
}
