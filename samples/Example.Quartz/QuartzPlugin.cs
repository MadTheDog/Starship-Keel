using BepInEx;
using Game;
using Manager;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;

namespace Sevo.Example.Quartz
{
    [BepInPlugin(Owner, "Keel Quartz and Metal Fixture", "0.1.0")]
    [BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.1")]
    public sealed class QuartzPlugin : BaseUnityPlugin
    {
        private const string Owner = "example.quartz";
        private bool observed;

        private void Awake()
        {
            TerrainMaterialRegistration.Register(Owner, "quartz", 1, "TEX_ROCK_B");
            TerrainDepositScanner.Register(Owner, "scanner", Owner + ":quartz", "Qz", new Color32(182, 212, 242, 255));
            OreDepositRegistration.Register(Owner, "shallow-vein", TerrainPlacementScope.Planets,
                "TEX_ROCK_A", Owner + ":quartz", spacing: 24, radius: 4, chance: 0.6, maximumNominalDepth: 24);
            TerrainMaterialRegistration.Register(Owner, "metal", 1, "RAW_IRON_A");
            var tint = new Color32(166, 116, 91, 255);
            ItemRegistration.Register(Owner, "chunk", 1, () => new ItemDefinition(
                WorldPropCommon.WorldPropId.rockSmallB, "Fixture Metal Chunk", "An independent ore identity.",
                WorldItemType.ore, Owner + ":metal", tint, materialSymbol: "Qz"));
            ItemRegistration.Register(Owner, "bar", 1, () => new ItemDefinition(
                WorldPropCommon.WorldPropId.ingot, "Fixture Metal Bar", "An independent ingot identity.",
                WorldItemType.ingot, Owner + ":metal", tint));
        }

        private void Update()
        {
            if (observed || PlayerState.sessionMode != PlayerState.SessionMode.solo
                || !ItemRegistration.TryCreateElement(Owner + ":chunk", 1, out var ore)
                || !ItemRegistration.TryCreateElement(Owner + ":bar", 1, out var bar))
                return;
            observed = true;
            Logger.LogInfo($"KEEL_SECOND_METAL_IDENTITIES ore={ore.itemType}/{ore.id}/{ore.contextualDataId} " +
                $"bar={bar.itemType}/{bar.id}/{bar.contextualDataId}");
        }
    }
}
