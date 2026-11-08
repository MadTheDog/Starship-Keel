using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using Game;
using HarmonyLib;
using Manager;
using MessagePack;
using Mirror;
using Sevo.Framework.Runtime;
using UnityEngine;
using VoxelEngine;
using VoxelEngine.Crafting;
using World;
using World.Terrain;

namespace Sevo.Example.MachineRecipeProbe
{
    [BepInPlugin(Owner, "Keel Machine Recipe Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.8")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string Owner = "example.keel-machines";
        private const string Dust = Owner + ":dust";
        private const string Pellet = Owner + ":pellet";
        private const float CraftSeconds = 1.5f;
        private string stage = "awaiting-solo";
        private CraftingElement ore;
        private CraftingElement dust;
        private CraftingElement pellet;
        private ParentEntity? grid;
        private ParentEntity? sourceGrid;
        private string session = "";
        private bool armed;
        private static Plugin? observingLoad;
        private LoadedCrusher? loadedCrusher;
        private BrickInstanceData? conveyorStorage;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_MACHINE_PROBE") != "1")
                return;
            session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION") ?? "";
            if (!Guid.TryParseExact(session, "N", out _))
            {
                Logger.LogWarning("KEEL_MACHINE_PROBE requires an isolated lab session.");
                return;
            }
            ItemRegistration.Register(Owner, "dust", 1, () =>
                new ItemDefinition(WorldPropCommon.WorldPropId.component_construction, "Machine Probe Dust", "A furnace output for the machine probe."));
            ItemRegistration.Register(Owner, "pellet", 1, () =>
                new ItemDefinition(WorldPropCommon.WorldPropId.component_mechanical, "Machine Probe Pellet", "A crusher output for the machine probe."));
            MachineRecipeRegistration.Register(Owner, "smelt_ore", 1, () =>
            {
                ore = TabSchematics.Schematic_IronIngots.element0;
                if (ore.itemType != WorldItemType.ore || ore.contextualDataId <= 0)
                    throw new InvalidOperationException("Stock iron smelting no longer exposes contextual ore.");
                // Terrain-generated ore uses the stock small-rock carriers.
                ore.id = (int)WorldPropCommon.WorldPropId.rockSmallA;
                ore.quantity = 1f;
                dust = Item(Dust, 3f);
                return new MachineRecipeDefinition(MachineKind.Furnace, CraftSeconds, ore, dust);
            });
            MachineRecipeRegistration.Register(Owner, "press_dust", 1, () =>
            {
                pellet = Item(Pellet, 2f);
                return new MachineRecipeDefinition(MachineKind.Crusher, CraftSeconds, Item(Dust, 3f), pellet);
            });
            armed = true;
        }

        private void Update()
        {
            // Unity runs a coroutine up to its first yield immediately. Defer the fixture iterator
            // until the title screen has loaded the stock brick and command resources.
            if (!armed || !MachineRecipeRegistration.IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo)
                return;
            armed = false;
            Step("awaiting-safe-solo");
            StartCoroutine(Report());
        }

        private IEnumerator Report()
        {
            var run = Run();
            try
            {
                while (true)
                {
                    object? wait;
                    try
                    {
                        if (!run.MoveNext()) yield break;
                        wait = run.Current;
                    }
                    catch (Exception exception)
                    {
                        Logger.LogError($"KEEL_MACHINE_PROBE FAIL stage={stage} session={session} {exception}");
                        yield break;
                    }
                    yield return wait;
                }
            }
            finally { (run as IDisposable)?.Dispose(); }
        }

        private IEnumerator Run()
        {
            var timer = Stopwatch.StartNew();
            var position = default(WorldTransform);
            while (!Safe() || PlayerState.SystemEntered || Dependency.PlayerManager.Health.IsRespawning
                || !MachineRecipeRegistration.IsAvailable || !TryPosition(out position))
            {
                Deadline(timer);
                yield return null;
            }

            if (ore.id == 0 || dust.id == 0 || pellet.id == 0 || dust.id == pellet.id)
                throw new InvalidOperationException("The contextual and custom recipe identities did not resolve.");
            Step($"resolved-recipes ore={ore.id}/{ore.contextualDataId} dust={dust.id} pellet={pellet.id}");

            var baseBlock = TabHull.HULL_A.PreviewBrickData;
            baseBlock.gridLevel = 3;
            baseBlock.scale = BrickInstanceData.PackScale(new Vector3i(7, 0, 2));
            int unit = baseBlock.GridSizeUnit;
            var furnaceData = BrickAt(TabStorage.FURNACE, 4 * unit, unit, 0);
            var crusherData = BrickAt(TabSystem.CRUSHER, unit, unit, 0);
            var reactorData = BrickAt(TabSystem.REACTOR, 0, unit, 0);
            reactorData.scale = BrickInstanceData.PackScale(new Vector3i(1, 1, 1));
            // Leave room for the two-cell-wide reactor and the furnace footprint.
            crusherData.gridPosition = new Vector3i(2 * unit, unit, 0);

            try
            {
                Step("creating-fixture");
                grid = Dependency.PlayerManager.NetworkSetup.ServerCreateEntity(baseBlock, position, EntityType.spaceship);
                Require(grid != null, "The stock builder refused the fixture hull.");
                while (!Safe() || !grid!.IsInitialized)
                {
                    Deadline(timer);
                    yield return null;
                }
                Place(furnaceData);
                Place(crusherData);
                FurnaceCommandData furnace = default;
                CrusherCommandData crusher = default;
                Step("awaiting-stock-command-ids");
                while (!Safe() || !TryCommand(furnaceData, out furnace) || !TryCommand(crusherData, out crusher))
                {
                    Deadline(timer);
                    yield return null;
                }
                int furnaceId = furnace.CommandId;
                int crusherId = crusher.CommandId;
                Require(furnaceId > 0 && crusherId > 0 && !furnace.isCrucible, "The native arc furnace and crusher did not register.");

                Step($"stock-slot-validation furnace={furnaceId} crusher={crusherId}");
                var oreData = ore.ToItemData;
                oreData.prefabId = (int)WorldPropCommon.WorldPropId.rockSmallB;
                Require(furnace.IsItemValidInput(oreData), "The native furnace input predicate rejected the stock ore.");
                Require(crusher.IsItemValidInput(dust.ToItemData), "The native crusher input predicate rejected registered dust.");
                Require(!crusher.IsItemValidInput(pellet.ToItemData), "The native crusher accepted the unrelated custom pellet.");
                var drill = new CraftingElement(WorldPropCommon.WorldPropId.drill_bit, 1f).ToItemData;
                Require(CrusherCommandData.IsItemValidDrillBit(drill), "The stock drill bit predicate rejected the stock bit.");

                while (!Safe() || !Power().IsStalled)
                {
                    Deadline(timer);
                    yield return null;
                }
                furnace = FurnaceCommandData.datas[furnaceId];
                Require(furnace.inputItemId == 0, "The fixture's furnace input slot is already occupied.");
                oreData.RegisterAndPushItself(StorageCommon.CONTEXT);
                furnace.inputItemId = oreData.id;
                furnace.PushItself();

                crusher = CrusherCommandData.datas[crusherId];
                Require(crusher.drillBitItemId == 0, "The fixture's drill-bit slot is already occupied.");
                drill.RegisterAndPushItself(StorageCommon.CONTEXT);
                crusher.drillBitItemId = drill.id;
                crusher.SpawnOrRefreshDrillBitVisual();
                crusher.PushItself();

                Step("checking-unpowered-furnace");
                float holdUntil = Time.realtimeSinceStartup + CraftSeconds + 0.25f;
                bool sawPendingPreview = false;
                do
                {
                    Deadline(timer);
                    yield return null;
                    if (!Safe()) continue;
                    var idleFurnace = FurnaceCommandData.datas[furnaceId];
                    var power = Power();
                    var waitContext = StorageCommon.CONTEXT;
                    var liveInput = waitContext.GetItemData(oreData.id);
                    Require(power.IsStalled && power.powerStored == 0f && idleFurnace.inputItemId == oreData.id
                        && liveInput.id == oreData.id && liveInput.quantity == oreData.quantity
                        && idleFurnace.completion == 0f && waitContext.GetStorageData(furnace.StorageId).rootItemId == 0,
                        "The unpowered furnace advanced the custom recipe.");
                    if (idleFurnace.outputItemId != 0)
                    {
                        var preview = waitContext.GetItemData(idleFurnace.outputItemId);
                        Require(preview.id == idleFurnace.outputItemId && preview.itemType == dust.itemType
                            && preview.prefabId == dust.id && preview.quantity == dust.quantity
                            && preview.nextElementId == 0 && !preview.HasLiveReleaseComponents(),
                            "The unpowered furnace staged an unexpected output.");
                        sawPendingPreview = true;
                    }
                } while (Time.realtimeSinceStartup < holdUntil || !Safe());
                Logger.LogInfo($"KEEL_MACHINE_PROBE POWER PASS stalled=true ore={oreData.id} pendingPreview={sawPendingPreview} session={session}");

                Step("placing-fueled-stock-reactor");
                Place(reactorData);
                SystemReactor reactor = null!;
                while (!Safe() || !TryCommand(reactorData, out reactor))
                {
                    Deadline(timer);
                    yield return null;
                }
                var context = StorageCommon.CONTEXT;
                var tank = context.GetStorageData(reactor.StorageId);
                if (FuelCommon.GetFuel(reactor.StorageId, FuelCommon.FuelType.hydrogen, context) < 1f)
                    FuelCommon.FillTank(reactor.StorageId, 60f, FuelCommon.FuelType.hydrogen, context);
                float hydrogen = FuelCommon.GetFuel(reactor.StorageId, FuelCommon.FuelType.hydrogen, context);
                Require(hydrogen >= 1f, $"The stock reactor has no hydrogen (storage={reactor.StorageId}, type={tank.type}, capacity={tank.maximumVolume}, used={tank.currentVolume}, gridLevel={reactorData.gridLevel}).");
                Logger.LogInfo($"KEEL_MACHINE_PROBE REACTOR storage={reactor.StorageId} capacity={tank.maximumVolume:F1} hydrogen={hydrogen:F1} output={Power().powerOutput:F1} session={session}");
                Step("awaiting-powered-furnace-output");
                float nextPowerReport = Time.realtimeSinceStartup;
                while (!Safe() || Power().IsStalled || Count(furnace.StorageId, dust) < dust.quantity)
                {
                    Deadline(timer);
                    if (Safe() && Time.realtimeSinceStartup >= nextPowerReport)
                    {
                        ReportFurnaceWait(furnaceId);
                        nextPowerReport = Time.realtimeSinceStartup + 5f;
                    }
                    yield return null;
                }
                Require(Count(furnace.StorageId, dust) == dust.quantity
                    && Count(furnace.StorageId, TabSchematics.Schematic_IronIngots.output) == 0f
                    && FurnaceCommandData.datas[furnaceId].inputItemId != oreData.id,
                    "The furnace failed exact input consumption or produced a stock ingot.");
                Logger.LogInfo($"KEEL_MACHINE_PROBE FURNACE PASS oreCarrier={oreData.prefabId} dust={dust.quantity} power={Power().powerStored:F1} session={session}");

                // Move only the furnace's output into the live crusher input slot via stock inventory removal.
                var transfer = CraftingCommon.PopupCraftingElement(furnace.StorageId, dust, StorageCommon.CONTEXT);
                Require(transfer.id != 0 && transfer.prefabId == dust.id && transfer.quantity == dust.quantity
                    && Count(furnace.StorageId, dust) == 0f, "Native storage removal did not transfer the furnace's exact output.");
                crusher = CrusherCommandData.datas[crusherId];
                if (crusher.inputItemId != 0)
                {
                    ItemInstanceResources.ReleaseItemId(transfer.id);
                    throw new InvalidOperationException("The fixture's crusher input slot is already occupied.");
                }
                crusher.inputItemId = transfer.id;
                crusher.PushItself();

                Step("awaiting-powered-crusher-output");
                while (!Safe() || Count(crusher.StorageId, pellet) < pellet.quantity)
                {
                    Deadline(timer);
                    yield return null;
                }
                Require(Count(crusher.StorageId, pellet) == pellet.quantity && Count(crusher.StorageId, dust) == 0f
                    && CrusherCommandData.datas[crusherId].inputItemId != transfer.id,
                    "The crusher did not consume the custom part and produce the exact pellet stack.");
                var wornBit = ItemInstanceResources.GetItemData(CrusherCommandData.datas[crusherId].drillBitItemId);
                Require(wornBit.id == drill.id && wornBit.damage > drill.damage, "Stock crushing did not wear the installed drill bit.");
                Logger.LogInfo($"KEEL_MACHINE_PROBE CHAIN PASS ore={ore.quantity} dust={dust.quantity} pellet={pellet.quantity} drillWear={wornBit.damage:F1} session={session}");

                if (Environment.GetEnvironmentVariable("SEVO_KEEL_MACHINE_PROBE_CONVEYOR") == "1")
                {
                    var conveyor = ExerciseConveyor(timer, furnaceData, furnace, crusherData, crusherId);
                    while (conveyor.MoveNext()) yield return conveyor.Current;
                }
                if (Environment.GetEnvironmentVariable("SEVO_KEEL_MACHINE_PROBE_ROUNDTRIP") == "1")
                {
                    var roundTrip = ExerciseRoundTrip(timer, position, furnaceData, furnaceId, crusherData, crusherId);
                    try { while (roundTrip.MoveNext()) yield return roundTrip.Current; }
                    finally { (roundTrip as IDisposable)?.Dispose(); }
                }
                Step("cleanup");
            }
            finally
            {
                if (Safe())
                {
                    if (grid != null && !grid.WillBeUnspawned) Dependency.PlayerNetwork.ServerDestroyEntity(grid);
                    if (sourceGrid != null && !sourceGrid.WillBeUnspawned) Dependency.PlayerNetwork.ServerDestroyEntity(sourceGrid);
                    grid = null;
                    sourceGrid = null;
                }
                else if (grid != null || sourceGrid != null)
                {
                    Logger.LogWarning($"KEEL_MACHINE_PROBE could not remove the grid at a safe boundary; discard the lab world session={session}.");
                }
            }
            Logger.LogInfo($"KEEL_MACHINE_PROBE PASS session={session}");
        }

        private IEnumerator ExerciseConveyor(Stopwatch timer, BrickInstanceData furnaceData, FurnaceCommandData furnace,
            BrickInstanceData crusherData, int crusherId)
        {
            Step("connecting-furnace-conveyor");
            var container = PlaceConveyorStorage(furnaceData);
            StorageCommandData cargo = default;
            var reachable = new List<int>();
            while (!Safe() || !TryConnectedCargo(furnace.StorageId, container, reachable, out cargo))
            {
                Deadline(timer);
                yield return null;
            }

            var wrong = ore;
            // Arc furnaces treat coal ore as fuel rather than an eligible smelting input.
            wrong.contextualDataId = (int)MaterialGradientType.coal;
            Require(FurnaceCommandData.IsItemValidFuel(wrong.ToItemData), "Stock coal ore no longer matches the control input.");
            Insert(cargo.StorageId, wrong.ToItemData);
            Insert(cargo.StorageId, ore.ToItemData);
            ConfigureFurnace(furnace.CommandId, push: false);
            Step("awaiting-conveyor-pull-and-smelt");
            while (!Safe() || Count(furnace.StorageId, dust) < dust.quantity)
            {
                Deadline(timer);
                yield return null;
            }
            Require(Count(cargo.StorageId, ore) == 0f && Count(cargo.StorageId, wrong) == wrong.quantity
                && Count(furnace.StorageId, dust) == dust.quantity,
                "The native conveyor pulled the wrong ore context or produced the wrong stack.");
            ConfigureFurnace(furnace.CommandId, push: true);
            Step("awaiting-conveyor-custom-part-push");
            while (!Safe() || Count(cargo.StorageId, dust) < dust.quantity)
            {
                Deadline(timer);
                yield return null;
            }
            Require(Count(cargo.StorageId, dust) == dust.quantity && Count(furnace.StorageId, dust) == 0f
                && Count(cargo.StorageId, wrong) == wrong.quantity, "The native output route moved the wrong stack.");
            conveyorStorage = container;
            Logger.LogInfo($"KEEL_MACHINE_PROBE FURNACE_CONVEYOR PASS source={cargo.StorageId} furnace={furnace.StorageId} wrongContext={wrong.contextualDataId} pushedDust={dust.quantity} session={session}");

            Step("connecting-crusher-conveyor");
            var crusherContainer = PlaceConveyorStorage(crusherData);
            var crusher = CrusherCommandData.datas[crusherId];
            StorageCommandData crusherCargo = default;
            while (!Safe() || !TryConnectedCargo(crusher.StorageId, crusherContainer, reachable, out crusherCargo))
            {
                Deadline(timer);
                yield return null;
            }
            Require(!reachable.Contains(cargo.StorageId), "The crusher's source network overlaps the furnace fixture.");
            float before = Count(crusher.StorageId, pellet);
            Insert(crusherCargo.StorageId, pellet.ToItemData);
            Insert(crusherCargo.StorageId, dust.ToItemData);
            var settings = crusher.GetSaveData();
            settings.autoCraftInventory = true;
            settings.pullComponents = true;
            crusher.OnGenericParamsUpdated(settings, syncUpdate: false, onClose: false);

            Step("awaiting-conveyor-custom-part-pull");
            while (!Safe() || Count(crusher.StorageId, pellet) < before + pellet.quantity)
            {
                Deadline(timer);
                yield return null;
            }
            Require(Count(crusherCargo.StorageId, dust) == 0f && Count(crusherCargo.StorageId, pellet) == pellet.quantity
                && Count(crusher.StorageId, pellet) == before + pellet.quantity,
                "The native crusher route pulled the wrong registered part or changed the expected stack count.");
            Logger.LogInfo($"KEEL_MACHINE_PROBE CONVEYOR PASS source={crusherCargo.StorageId} crusher={crusher.StorageId} pulledDust={dust.quantity} unrelatedPellets={pellet.quantity} session={session}");
        }

        private BrickInstanceData PlaceConveyorStorage(BrickInstanceData machine)
        {
            var bounds = machine.GetAabb(useGeoBounds: true);
            var tube = BrickAt(TabStorage.CONVEYOR_TUBE, machine.gridPosition.x,
                Mathf.RoundToInt(bounds.Max.y * 32f), machine.gridPosition.z);
            var container = BrickAt(TabStorage.STORAGE_UNIT, tube.gridPosition.x,
                tube.gridPosition.y + tube.GridSizeUnit, tube.gridPosition.z);
            Place(tube);
            Place(container);
            return container;
        }

        private bool TryConnectedCargo(int origin, BrickInstanceData brick, List<int> reachable, out StorageCommandData cargo)
        {
            if (!TryCommand(brick, out cargo)) return false;
            reachable.Clear();
            StorageCommon.GetConnectedStorageIds(origin, reachable,
                value => value.type == ItemStorageType.storageUnit, origin);
            return reachable.Contains(cargo.StorageId);
        }

        private static void ConfigureFurnace(int commandId, bool push)
        {
            var furnace = FurnaceCommandData.datas[commandId];
            furnace.OnGenericParamsUpdated(new FurnaceCommandData.FurnaceSaveData
            {
                autoCraftInventory = true,
                pullComponents = !push,
                pushBricks = push
            }, syncUpdate: false, onClose: false);
        }

        private static void Insert(int storageId, ItemInstanceData item)
        {
            item.RegisterAndPushItself(StorageCommon.CONTEXT);
            if (StorageCommon.InsertItem(storageId, item.id, StorageCommon.CONTEXT, 6123,
                StorageInsertCapacityPolicy.checkCapacity, StorageInsertMergePolicy.tryMerge))
                return;
            ItemInstanceResources.ReleaseItemId(item.id);
            throw new InvalidOperationException("The connected native storage refused a seed stack.");
        }

        private IEnumerator ExerciseRoundTrip(Stopwatch timer, WorldTransform position, BrickInstanceData furnaceData, int furnaceId,
            BrickInstanceData crusherData, int crusherId)
        {
            Step("preparing-inflight-stock-machine-save");
            var furnace = FurnaceCommandData.datas[furnaceId];
            var crusher = CrusherCommandData.datas[crusherId];
            Require(furnace.outputItemId == 0 && crusher.outputItemId == 0 && crusher.inputItemId == 0,
                "The save probe expects both recipes to be complete.");
            float expectedDust = Count(furnace.StorageId, dust);
            float expectedPellet = Count(crusher.StorageId, pellet);
            var drill = ItemInstanceResources.GetItemData(crusher.drillBitItemId);
            Require(CrusherCommandData.IsItemValidDrillBit(drill), "The live crusher lost its drill before saving.");

            var input = dust.ToItemData;
            input.RegisterAndPushItself(StorageCommon.CONTEXT);
            crusher.inputItemId = input.id;
            crusher.PushItself();
            while (true)
            {
                Deadline(timer);
                yield return null;
                if (!Safe()) continue;
                crusher = CrusherCommandData.datas[crusherId];
                if (crusher.inputItemId == input.id && crusher.outputItemId != 0 && crusher.completion > 0f)
                    break;
            }
            Step("snapshotting-inflight-stock-machine-grid");
            var commandSave = crusher.GetSaveData();
            Require(commandSave.inputItemData.id == dust.id && commandSave.inputItemData.quantity == dust.quantity,
                "The stock crusher save did not preserve its in-flight custom input.");

            var hook = new Harmony(Owner + ".load-observation");
            observingLoad = this;
            try
            {
                hook.Patch(AccessTools.DeclaredMethod(typeof(CrusherCommandData), nameof(CrusherCommandData.OnSaveLoaded)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveCrusherLoad)));
                var save = BrickEntity_mp.GetSave(grid, clearSGUID: true);
                save.ClearInstanceData();
                var bytes = MessagePackSerializer.Serialize(save, SaveTools.LZ4Compress);
                var restored = MessagePackSerializer.Deserialize<BrickEntity_mp>(bytes, SaveTools.LZ4Compress);
                position.systemPosition += new Vector3l(40f * Vector3.up);
                var shell = BlockEntityManager.instance.CreateEntity(position, Dependency.PlayerManager.SkyTransform.CurrentSystem);
                if (shell == null || ReferenceEquals(shell, grid))
                    throw new InvalidOperationException("Stock save reconstruction requires a new fixture grid.");
                sourceGrid = grid;
                grid = shell;
                restored.LoadInto(shell);

                Step("awaiting-reconstructed-machine-inventories");
                FurnaceCommandData restoredFurnace = default;
                CrusherCommandData restoredCrusher = default;
                while (!Safe() || !shell.IsInitialized || !TryCommand(furnaceData, out restoredFurnace)
                    || !TryCommand(crusherData, out restoredCrusher) || loadedCrusher == null)
                {
                    Deadline(timer);
                    yield return null;
                }

                var loaded = loadedCrusher.Value;
                Require(loaded.Input.prefabId == dust.id && loaded.Input.quantity == dust.quantity
                    && loaded.PendingOutputId == 0 && loaded.Progress == 0f && loaded.ActiveInputId == 0
                    && loaded.CommittedPellets == expectedPellet && loaded.Drill.prefabId == drill.prefabId
                    && loaded.Drill.damage == drill.damage && Count(restoredFurnace.StorageId, dust) == expectedDust,
                    "Stock reconstruction lost custom items or restored pending progress/output.");
                Logger.LogInfo($"KEEL_MACHINE_PROBE INFLIGHT_LOAD PASS committed={loaded.CommittedPellets} input={loaded.Input.quantity} progress={loaded.Progress} session={session}");

                if (conveyorStorage.HasValue)
                {
                    Require(TryCommand(conveyorStorage.Value, out StorageCommandData restoredCargo)
                        && Count(restoredCargo.StorageId, dust) == dust.quantity,
                        "Stock reconstruction lost the custom part in connected storage.");
                }

                Step("awaiting-restarted-crusher-recipe");
                while (!Safe() || Count(restoredCrusher.StorageId, pellet) < expectedPellet + pellet.quantity)
                {
                    Deadline(timer);
                    yield return null;
                }
                Require(Count(restoredCrusher.StorageId, pellet) == expectedPellet + pellet.quantity
                    && CrusherCommandData.datas[restoredCrusher.CommandId].inputItemId == 0,
                    "The restored crusher did not restart and commit its saved input exactly once.");
                Logger.LogInfo($"KEEL_MACHINE_PROBE ROUNDTRIP PASS committed={expectedPellet} total={expectedPellet + pellet.quantity} session={session}");
            }
            finally
            {
                observingLoad = null;
                hook.UnpatchSelf();
            }
        }

        private static void ObserveCrusherLoad(ref CrusherCommandData __instance)
        {
            var probe = observingLoad;
            if (probe?.grid == null || !ReferenceEquals(__instance.brickCoord.Parent, probe.grid)) return;
            probe.loadedCrusher = new LoadedCrusher(
                ItemInstanceResources.GetItemData(__instance.inputItemId),
                ItemInstanceResources.GetItemData(__instance.drillBitItemId),
                __instance.inputItemIdForCraft, __instance.outputItemId, __instance.completion,
                Count(__instance.StorageId, probe.pellet));
        }

        private readonly struct LoadedCrusher
        {
            internal readonly ItemInstanceData Input;
            internal readonly ItemInstanceData Drill;
            internal readonly int ActiveInputId;
            internal readonly int PendingOutputId;
            internal readonly float Progress;
            internal readonly float CommittedPellets;

            internal LoadedCrusher(ItemInstanceData input, ItemInstanceData drill, int activeInputId, int pendingOutputId,
                float progress, float committedPellets)
            {
                Input = input;
                Drill = drill;
                ActiveInputId = activeInputId;
                PendingOutputId = pendingOutputId;
                Progress = progress;
                CommittedPellets = committedPellets;
            }
        }

        private void Place(BrickInstanceData brick)
        {
            var action = new VoxelActionData { entityID = grid!.ID, brickData = brick, type = VoxelActionType.build };
            Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(action, false, 0);
        }

        private bool TryCommand<T>(BrickInstanceData expected, out T command)
        {
            var live = grid!.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(expected));
            if (live.brickId == expected.brickId && live.gridPosition.Equals(expected.gridPosition)
                && new BrickCoord(grid.ID, live).Command is T result)
            {
                command = result;
                return true;
            }
            command = default!;
            return false;
        }

        private EntitySystemResources.EntitySystemData Power() => EntitySystemResources.datas[grid!.IndexID];

        private void ReportFurnaceWait(int furnaceId)
        {
            var power = Power();
            var furnace = FurnaceCommandData.datas[furnaceId];
            var context = StorageCommon.CONTEXT;
            var storage = context.GetStorageData(furnace.StorageId);
            int smeltingSignal = furnace.isSmelting.id;
            bool smelting = CommandStateData.stateDatas.IsCreated && smeltingSignal > 0
                && smeltingSignal < CommandStateData.stateDatas.Length && furnace.isSmelting.IsTrue;
            float outputVolume = dust.ToItemData.GetVolumeInLiter(context);
            Logger.LogInfo($"KEEL_MACHINE_PROBE FURNACE_WAIT grid={grid!.IndexID} stored={power.powerStored:F1}/{power.powerCapacity:F1} stalled={power.IsStalled} shouldStall={power.ShouldBeStalled} online={power.AreSystemOnline} loading={power.isLoading} stallSeconds={power.stallTime:F1} state={furnace.State:F2} input={furnace.inputItemId} output={furnace.outputItemId} progress={furnace.completion:F3} smelting={smelting} storage={storage.id}/{storage.type} brick={storage.brickInstanceId} used={storage.currentVolume:F1}/{storage.maximumVolume:F1} outputVolume={outputVolume:F1} session={session}");
        }

        private static BrickInstanceData BrickAt(Brick brick, int x, int y, int z)
        {
            var data = brick.PreviewBrickData;
            data.gridLevel = 3;
            data.scale = 0;
            data.gridPosition = new Vector3i(x, y, z);
            return data;
        }

        private static CraftingElement Item(string key, float quantity) =>
            ItemRegistration.TryCreateElement(key, quantity, out var item) ? item
                : throw new InvalidOperationException("The fixture item is unavailable: " + key);

        private static float Count(int storageId, CraftingElement identity)
        {
            var context = StorageCommon.CONTEXT;
            float count = 0f;
            int id = context.GetStorageData(storageId).rootItemId;
            int remaining = context.ItemChainGuard;
            while (id != 0 && remaining-- > 0)
            {
                var item = context.GetItemData(id);
                if (item.itemType == identity.itemType && item.prefabId == identity.id
                    && item.contextualDataId == identity.contextualDataId)
                    count += item.quantity;
                id = item.nextElementId;
            }
            Require(id == 0, "The fixture's native storage chain is cyclic or corrupt.");
            return count;
        }

        private static bool Safe() => PlayerState.sessionMode == PlayerState.SessionMode.solo
            && !PlayerState.InLoadingScreen && !GameloopManager.IsNativeContainerAccessUnsafe
            && NetworkServer.active && NetworkServer.dontListen
            && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal;

        private static bool TryPosition(out WorldTransform position)
        {
            position = default;
            var player = Dependency.PlayerManager;
            if (player.TransformData.systemPosition.Equals(Vector3l.zero)
                || !player.SkyTransform.CurrentSystem.Equals(PlanetarySystem.CurrentStarData)
                || PlanetarySystem.SolarBodies.Count == 0) return false;

            foreach (var direction in new[] { Vector3.up, Vector3.right, Vector3.forward })
            {
                var candidate = player.TransformData.systemPosition + new Vector3l(direction * 250f);
                if (PlanetarySystem.SolarBodies.Any(body => body.IsShipTooClose(candidate))) continue;
                position = new WorldTransform(candidate, Quaternion.identity);
                return true;
            }
            return false;
        }

        private void Deadline(Stopwatch timer)
        {
            if (timer.Elapsed > TimeSpan.FromMinutes(3))
                throw new TimeoutException("The fixture timed out during " + stage + ".");
        }

        private void Step(string next)
        {
            stage = next;
            Logger.LogInfo($"KEEL_MACHINE_PROBE stage={stage} session={session}");
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
