# Starship Keel content examples

These two independent BepInEx plugins declare `example.alpha:hull` and `example.beta:hull`. Neither requests a numeric ID. Both use the shared allocator and an ordinary stock hull cube; custom native collision templates and command bricks are intentionally unsupported.

Run `./samples/Build-Samples.ps1` from the project root. It builds and packages only the sample DLLs plus manifests into a new directory under `samples/artifacts`. It does not install or launch anything. Private game/loader references and framework DLLs are not included in sample ZIPs.

For a disposable, independently isolated lab only:

1. Provision the platform runtime through the launcher, then import and enable both generated sample ZIPs in that profile. BepInEx's hard dependency requires `local.sevo.modplatform`; the runtime is provisioned separately from normal package dependencies.
2. Keep original saves out of the lab. A launcher profile does not by itself isolate Unity's persistent data path.
3. Explicitly enable `[Experimental] EnableStockShapeContent = true` in the target game's `BepInEx/config/local.sevo.modplatform.cfg`, or set `SEVO_PLATFORM_EXPERIMENTAL_CONTENT=1` for that managed process. This setting has no effect without a valid `SEVO_PLATFORM_STATE_DIR`, profile marker, and `SEVO_PLATFORM_SESSION` launcher nonce.
4. Restart through the managed launcher. Inspect `runtime-status.json`: mode must be `experimental-stock-shapes`, `registrationAdmitted` must be true, and `registeredDefinitions` must be 2. `resourcesReady` records resource-cycle validation only. It does not establish world, blueprint, or multiplayer safety.
5. The examples appear in the native hull tab after successful resource initialization. Their persisted identities are in `registry.json` beneath that profile's runtime-state directory. Do not edit IDs or delete this file after using content.

Default audit mode collects declarations but never invokes these factories, allocates IDs, or modifies `registry.json`. Disable the experimental option and restart to return to audit mode; do not load worlds that contain these definitions while their providers are absent. The current runtime has no broad missing-mod admission gate.

API:

```csharp
[BepInDependency(Sevo.Framework.Runtime.Plugin.Id)]
// In the dependent plugin's Awake, before native resource initialization:
ContentRegistration.Register("example.alpha", "hull", 1,
    id => StockShape.CreateHull(id, "Example Alpha Hull"));
```

Keys and content revisions are durable identities. Renaming a display name is independent of its key. A changed revision is rejected until an explicit migration exists. Submit declarations before the collection phase closes; late registration and duplicate keys are errors. Arbitrary unported plugins can still overwrite native tables outside the supported registration API; the framework detects ownership loss at its resource boundaries but cannot sandbox their code.
