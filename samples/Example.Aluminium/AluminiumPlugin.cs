using BepInEx;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;

namespace Sevo.Example.Aluminium
{
    [BepInPlugin(Owner, "Keel Aluminium Fixture", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.1")]
    public sealed class AluminiumPlugin : BaseUnityPlugin
    {
        private const string Owner = "example.aluminium";
        private const string Material = Owner + ":terrain";
        private const string Ore = Owner + ":ore";
        private const string Bar = Owner + ":bar";
        private bool observed;

        private void Awake()
        {
            TerrainMaterialRegistration.RegisterStockVisual(Owner, "terrain", 1, "RAW_IRON_A", "RAW_IRON_A",
                new Color32(241, 226, 205, 255), brightness: 1.4f);
            TerrainDepositScanner.Register(Owner, "scanner", Material, "Al", new Color32(216, 209, 194, 255));
            // Smoke-test settings. Replace with a StockOreDepositReference measured from the paired control.
            OreDepositRegistration.Register(Owner, "deposit", TerrainPlacementScope.All,
                "TEX_ROCK_A", Material, spacing: 48, radius: 12, chance: 0.8);
            var tint = new Color32(216, 209, 194, 255);
            ItemRegistration.Register(Owner, "ore", 1, () => new ItemDefinition(
                WorldPropCommon.WorldPropId.rockSmallA, "Aluminium Chunk", "Unrefined aluminium.",
                WorldItemType.ore, Material, tint, materialSymbol: "Al"));
            ItemRegistration.Register(Owner, "bar", 1, () => new ItemDefinition(
                WorldPropCommon.WorldPropId.ingot, "Aluminium Bar", "Refined aluminium.",
                WorldItemType.ingot, Material, tint, materialSymbol: "Al"));
            TerrainMiningDropRegistration.Register(Owner, "mining", Material, Ore);
            MachineRecipeRegistration.Register(Owner, "smelt", 1, () =>
            {
                if (!ItemRegistration.TryCreateElement(Ore, 1, out var ore)
                    || !ItemRegistration.TryCreateElement(Bar, 1, out var bar))
                    throw new System.InvalidOperationException("Aluminium identities were not admitted.");
                return new MachineRecipeDefinition(MachineKind.Furnace, 2, ore, bar);
            });
        }

        private void Update()
        {
            if (observed || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || !ItemRegistration.TryCreateElement(Ore, 1, out var ore)
                || !ItemRegistration.TryCreateElement(Bar, 1, out var bar))
                return;
            observed = true;
            Logger.LogInfo($"KEEL_ALUMINIUM_IDENTITIES ore={ore.itemType}/{ore.id}/{ore.contextualDataId} " +
                $"bar={bar.itemType}/{bar.id}/{bar.contextualDataId}");
        }
    }
}
