using System;
using System.Linq;
using System.Numerics;
using BepInEx;
using Sevo.Framework.Runtime;
using Sevo.Framework.Sdk;
using UnityEngine;
using World;
using World.Terrain;

namespace Sevo.Example.OreDiscovery;

[BepInPlugin(Owner, "Keel Ore Discovery Probe", "0.1.0")]
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id, "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Owner = "example.ore-discovery";
    private int calls;
    private bool pending;

    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("SEVO_KEEL_ORE_DISCOVERY_PROBE") != "1"
            || Application.companyName != "SEVO Keel Gen Lab 0110" || Application.productName != "SEVO WG Test") return;
        OreDiscoveryProvider.Register(Owner, "probe", context =>
        {
            calls++;
            return new[]
            {
                new OreDiscoveryResult(Owner + ":near", new System.Numerics.Vector3(0, 0, 5), 2),
                new OreDiscoveryResult(Owner + ":far", new System.Numerics.Vector3(context.RangeMeters + 1, 0, 0), 1)
            };
        });
        pending = true;
    }

    private void Update()
    {
        if (!pending || !OreDiscoveryProvider.IsAvailable || PlayerState.sessionMode != PlayerState.SessionMode.solo
            || PlanetarySystem.InSystemTransition || !PlayerState.SystemEntered
            || OreScannerResources.ScannerChargingFactor < 1) return;
        pending = false;
        OreScannerResources.OnScannerKeyPressed();
        OreScannerResources.OnScannerKeyPressed();
        var snapshot = OreDiscoveryProvider.Latest;
        if (calls == 1 && snapshot != null && snapshot.Context.SystemSeed == PlanetarySystem.CurrentStarData.seed
            && snapshot.Results.Count == 1 && snapshot.Results.Single().MaterialKey == Owner + ":near")
            Logger.LogInfo("KEEL_ORE_DISCOVERY_PROBE PASS accepted pulse, charge rejection, and range filter");
        else
            Logger.LogError("KEEL_ORE_DISCOVERY_PROBE FAIL callbacks=" + calls + " discoveries=" + snapshot?.Results.Count);
    }
}
