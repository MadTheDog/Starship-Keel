using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx;
using Game;
using Game.Player;
using HarmonyLib;
using Manager;
using Mirror;
using Sevo.Framework.Runtime;
using UI;
using UI.Inventory;
using UnityEngine;
using VoxelEngine;
using World;

namespace Sevo.Example.RecipeProbe
{
    [BepInPlugin("example.keel-recipes", "Keel Recipe Probe", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.1.6")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const float Duration = 7.5f;
        private int expectedOutput;
        private bool published;
        private int probeHullId;
        private bool checkedBlockRecipe;
        private static bool deferredTutorial;

        private void Awake()
        {
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_RECIPE_PROBE_BLOCK") == "1")
                RegisterBlockRecipe();

            RecipeRegistration.Register("example.keel-recipes", "bench_probe", 1, () =>
            {
                var stock = TabSchematics.STONE_SLAB;
                var input = stock.element0;
                input.quantity = 3f;
                var output = stock.output;
                output.quantity = 2f;
                expectedOutput = output.id;
                return new RecipeDefinition(CraftingLevel.craftingBench, Duration, output, input);
            });
        }

        private void Update()
        {
            CheckBlockRecipe();
            if (published || expectedOutput == 0 || !CraftingSchematic.schematicsNativeArray.IsCreated
                || !RecipeRegistration.TryGetSchematicId("example.keel-recipes:bench_probe", out var id)
                || id <= 0 || id >= CraftingSchematic.Index.Length || id >= CraftingSchematic.schematicsNativeArray.Length)
                return;
            var entry = CraftingSchematic.Index[id];
            var native = CraftingSchematic.schematicsNativeArray[id];
            if (entry.id != id || entry.output.id != expectedOutput || entry.craftingLevel != CraftingLevel.craftingBench
                || entry.duration != Duration || entry.output.quantity != 2f || entry.element0.quantity != 3f
                || entry.InputsLength != 1 || !native.Equals(entry)
                || !CraftingSchematic.SchematicsForCodex.Any(value => value.id == id))
                return;
            published = true;
            Logger.LogInfo("Recipe probe passed: additive bench recipe is present in the managed index, native job table and Codex list. id=" + id);
            if (Environment.GetEnvironmentVariable("SEVO_KEEL_RECIPE_PROBE_CRAFT") == "1")
            {
                var exerciseKey = Environment.GetEnvironmentVariable("SEVO_KEEL_RECIPE_PROBE_CRAFT_KEY");
                if (!string.IsNullOrEmpty(exerciseKey))
                {
                    if (!RecipeRegistration.TryGetSchematicId(exerciseKey, out var exerciseId))
                    {
                        Logger.LogError("KEEL_RECIPE_CRAFT FAIL no published recipe for " + exerciseKey);
                        return;
                    }
                    entry = CraftingSchematic.Index[exerciseId];
                }
                StartCoroutine(ReportExercise(ExerciseBench(entry)));
            }
        }

        private void RegisterBlockRecipe()
        {
            ContentRegistration.Register("example.keel-recipes", "probe_hull", 1, id =>
            {
                var hull = StockShape.CreateHull(id, "Recipe Probe Hull");
                probeHullId = id;
                return hull;
            });

            RecipeRegistration.Register("example.keel-recipes", "probe_hull_recipe", 1, () =>
            {
                var iron = TabSchematics.Schematic_IronIngots.output;
                iron.quantity = 1f;
                return new RecipeDefinition(CraftingLevel.assembler, 4f,
                    new CraftingElement(WorldItemType.brick, probeHullId, 1f), iron);
            });
        }

        private void CheckBlockRecipe()
        {
            if (checkedBlockRecipe || probeHullId == 0
                || !RecipeRegistration.TryGetSchematicId("example.keel-recipes:probe_hull_recipe", out var id))
                return;

            checkedBlockRecipe = true;
            var row = CraftingSchematic.Index[id];
            var primaryId = Brick.BrickProperties[probeHullId].schematicId;
            var isAssemblerSchematic = typeof(UI_Assembler).GetMethod("IsAssemblerSchematic",
                BindingFlags.Static | BindingFlags.NonPublic);
            var eligible = isAssemblerSchematic?.Invoke(null, new object[] { row }) is true;
            var listed = CraftingSchematic.SchematicsByCodexTab.Values.Any(rows => rows.Any(value => value.id == id));
            if (!eligible || !listed || row.output.itemType != WorldItemType.brick || row.output.id != probeHullId
                || row.craftingLevel != CraftingLevel.assembler || primaryId != id
                || !CraftingSchematic.schematicsNativeArray[id].Equals(row))
            {
                Logger.LogError($"KEEL_BLOCK_RECIPE_PROBE FAIL brick={probeHullId} schematic={id} primary={primaryId} eligible={eligible} listed={listed}");
                return;
            }
            Logger.LogInfo($"KEEL_BLOCK_RECIPE_PROBE PASS brick={probeHullId} schematic={id} primary={primaryId} eligible={eligible} listed={listed}");
        }

        private IEnumerator ReportExercise(IEnumerator exercise)
        {
            while (true)
            {
                object next;
                try
                {
                    if (!exercise.MoveNext()) yield break;
                    next = exercise.Current;
                }
                catch (Exception exception)
                {
                    Logger.LogError($"KEEL_RECIPE_CRAFT FAIL deferredTutorial={deferredTutorial} {exception}");
                    yield break;
                }
                yield return next;
            }
        }

        private IEnumerator ExerciseBench(CraftingSchematic entry)
        {
            var state = Environment.GetEnvironmentVariable("SEVO_PLATFORM_STATE_DIR");
            var session = Environment.GetEnvironmentVariable("SEVO_PLATFORM_SESSION");
            if (Application.companyName != "SEVO Keel Hooks Lab016" || Application.productName != "SEVO HK Test"
                || state == null || !Guid.TryParseExact(session, "N", out _))
                throw new InvalidOperationException("This exercise requires the disposable Keel hooks lab.");
            ProfileProtocol.Open(state, Paths.GameRootPath, session);

            var tutorial = new Harmony("example.keel-recipes.lab-tutorial");
            var tutorialMethod = AccessTools.DeclaredMethod(typeof(GameIntroductionResources.GameIntroductionLogic), "UpdateCreativeTutorial", Type.EmptyTypes)
                ?? throw new InvalidOperationException("The stock creative tutorial method is missing.");
            tutorial.Patch(tutorialMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(DeferCreativeTutorial)));
            ParentEntity? grid = null;
            bool benchWindow = false;
            try
            {
                var timer = Stopwatch.StartNew();
                Logger.LogInfo("KEEL_RECIPE_CRAFT stage=waiting-for-positioned-player");
                WorldTransform position;
                while (!SoloReady() || !TrySafePosition(Dependency.PlayerManager, out position))
                {
                    CheckDeadline(timer, "waiting for a positioned player and safe spawn location");
                    yield return null;
                }
                var player = Dependency.PlayerManager;
                Logger.LogInfo($"KEEL_RECIPE_CRAFT stage=placing-native-bench position={position.systemPosition}");
                if (deferredTutorial) Logger.LogInfo("KEEL_RECIPE_CRAFT temporarily deferred the stock creative tutorial for the isolated bench exercise.");
                var brick = TabStorage.CRAFTING_BENCH.PreviewBrickData;
                grid = player.NetworkSetup.ServerCreateEntity(brick, position, EntityType.spaceship);
                if (grid == null) throw new InvalidOperationException("The native bench grid was rejected.");

                CraftingBenchCommandData? bench = null;
                while (!bench.HasValue)
                {
                    CheckDeadline(timer, "waiting for the placed bench command");
                    if (grid.IsInitialized && SoloReady())
                    {
                        var data = grid.Bricks.GetBrickData(grid.Bricks.GetBrickInstanceId(brick));
                        if (data.brickId == brick.brickId && data.gridPosition.Equals(brick.gridPosition)
                            && new BrickCoord(grid.ID, data).Command is CraftingBenchCommandData command && command.CommandId > 0)
                            bench = command;
                    }
                    if (!bench.HasValue) yield return null;
                }
                Logger.LogInfo("KEEL_RECIPE_CRAFT stage=waiting-for-stock-respawn-and-menu-release");
                while (!SoloReady() || player.Health.IsRespawning || PlayerState.SystemEntered || PlayerState.InMenu)
                {
                    CheckDeadline(timer, $"waiting for stock respawn and menu release (respawning={player.Health.IsRespawning} systemEntered={PlayerState.SystemEntered} inMenu={PlayerState.InMenu})");
                    yield return null;
                }
                Logger.LogInfo($"KEEL_RECIPE_CRAFT stage=opening-native-bench commandId={bench.Value.CommandId}");
                bench.Value.OnKeyUp(null);
                benchWindow = true;
                if (UI_CommandGeneric.windowsParams.craftingBenchId != bench.Value.CommandId)
                    throw new InvalidOperationException("The stock command window did not select the placed bench.");

                var slotType = typeof(UI_SchematicSlot);
                const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
                var schematicField = RequireField(slotType, "schematic", instancePrivate);
                var complete = slotType.GetMethod("CompleteCraft", instancePrivate)
                    ?? throw new InvalidOperationException("The native craft action is missing.");
                var window = (UI_CommandGeneric?)RequireField(typeof(UI_CommandGeneric), "instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)
                    ?? throw new InvalidOperationException("The stock command window is missing.");
                var benchUi = (UI_PlayerCrafting?)RequireField(typeof(UI_CommandGeneric), "craftingBench", instancePrivate).GetValue(window)
                    ?? throw new InvalidOperationException("The stock bench recipe panel is missing.");
                var slots = (List<UI_SchematicSlot>?)RequireField(typeof(UI_PlayerCrafting), "schematicSlots", instancePrivate).GetValue(benchUi)
                    ?? throw new InvalidOperationException("The stock bench recipe slot collection is missing.");
                var builtField = RequireField(typeof(UI_PlayerCrafting), "listBuilt", instancePrivate);
                var levelField = RequireField(typeof(UI_PlayerCrafting), "craftingLevel", instancePrivate);
                UI_SchematicSlot? slot = null;
                string? lastState = null;
                while (slot == null || !SoloReady())
                {
                    var panelState = $"active={benchUi.gameObject.activeInHierarchy} built={builtField.GetValue(benchUi)} level={levelField.GetValue(benchUi)} rows={slots.Count}";
                    if (panelState != lastState)
                    {
                        Logger.LogInfo("KEEL_RECIPE_CRAFT stage=stock-bench-recipe-list " + panelState);
                        lastState = panelState;
                    }
                    CheckDeadline(timer, "waiting for the native bench to list the recipe: " + panelState);
                    if (benchUi.gameObject.activeInHierarchy && UI_CommandGeneric.windowsParams.craftingBenchId == bench.Value.CommandId)
                        slot = slots.FirstOrDefault(candidate =>
                            candidate != null && schematicField.GetValue(candidate) is CraftingSchematic displayed && displayed.id == entry.id);
                    if (slot == null || !SoloReady()) yield return null;
                }

                Logger.LogInfo("KEEL_RECIPE_CRAFT stage=matched-native-recipe-slot");
                int storageId = player.PlayerInventory.storageId;
                var input = entry.element0;
                var output = entry.output;
                var originalInput = Count(storageId, input);
                var originalOutput = Count(storageId, output);
                var seed = input.ToItemData;
                seed.Register();
                player.PlayerInventory.AddItem(seed.id);
                var seeded = Count(storageId, input);
                RequireDelta(seeded - originalInput, input.quantity, "native inventory did not receive the probe inputs");

                if (!CraftingCommon.HasCraftingElement(storageId, input, StorageCommon.CONTEXT))
                    throw new InvalidOperationException("The stock crafting matcher rejected the seeded items.");
                Logger.LogInfo("KEEL_RECIPE_CRAFT stage=running-native-complete-craft");
                complete.Invoke(slot, null);
                RequireDelta(seeded - Count(storageId, input), input.quantity, "native bench consumed the wrong quantity");
                RequireDelta(Count(storageId, output) - originalOutput, entry.outputQuantity, "native bench produced the wrong quantity");
                Logger.LogInfo("KEEL_RECIPE_CRAFT stage=verified-stock-inventory-deltas");
            }
            finally
            {
                tutorial.UnpatchSelf();
                if (benchWindow) UI_CommandGeneric.CloseWindow();
                if (grid != null && !grid.WillBeUnspawned && SoloReady())
                    Dependency.PlayerNetwork.ServerDestroyEntity(grid);
            }
            Logger.LogInfo($"KEEL_RECIPE_CRAFT PASS session={session} id={entry.id} input={entry.element0.itemType}/{entry.element0.id} x{entry.element0.quantity} output={entry.output.itemType}/{entry.output.id} x{entry.outputQuantity}");
        }

        private static bool DeferCreativeTutorial(GameIntroductionResources.GameIntroductionLogic __instance)
        {
            if (__instance.missionPhase != 0) return true;
            deferredTutorial = true;
            return false;
        }

        private static FieldInfo RequireField(Type type, string name, BindingFlags flags) =>
            type.GetField(name, flags) ?? throw new InvalidOperationException($"Missing native field {type.Name}.{name}.");

        private static bool TrySafePosition(PlayerCommandManager player, out WorldTransform position)
        {
            position = default;
            if (player.SkyTransform.CurrentSystem.Equals(default(StarData))
                || !player.SkyTransform.CurrentSystem.Equals(PlanetarySystem.CurrentStarData)
                || PlanetarySystem.SolarBodies.Count == 0)
                return false;
            var anchor = player.TransformData.systemPosition;
            if (anchor.Equals(Vector3l.zero)) return false;

            foreach (var offset in new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
            {
                var candidate = anchor + new Vector3l(offset * 250f);
                if (candidate.Equals(Vector3l.zero) || PlanetarySystem.SolarBodies.Any(body => body.IsShipTooClose(candidate)))
                    continue;
                position = new WorldTransform(candidate, Quaternion.identity);
                return true;
            }
            return false;
        }

        private static bool SoloReady() => PlayerState.sessionMode == PlayerState.SessionMode.solo
            && NetworkServer.active && !PlayerState.InLoadingScreen && !GameloopManager.jobStarted
            && Dependency.PlayerManager != null && Dependency.PlayerManager.IsLocal
            && Dependency.PlayerManager.PlayerInventory != null && Dependency.ItemBar != null;

        private static float Count(int storageId, CraftingElement element)
        {
            using var items = StorageCommon.InventoryContent(storageId, searchNeighbors: false, StorageCommon.CONTEXT);
            Dependency.ItemBar.AppendInventoryList(items);
            float quantity = 0f;
            for (int i = 0; i < items.Length; i++)
                if (items[i].itemType == element.itemType && items[i].prefabId == element.id) quantity += items[i].quantity;
            return quantity;
        }

        private static void RequireDelta(float actual, float expected, string error)
        {
            if (float.IsNaN(actual) || Math.Abs(actual - expected) > 0.001f)
                throw new InvalidOperationException($"{error}. Expected {expected}, observed {actual}.");
        }

        private static void CheckDeadline(Stopwatch timer, string stage)
        {
            if (timer.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException(stage);
        }
    }
}
