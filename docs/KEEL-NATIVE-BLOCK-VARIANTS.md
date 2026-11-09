# Native block variants

`NativeBlockVariantRegistration.Register<T>` registers a new block identity with a native command of type `T` and an existing stock placement and resource recipe. Call it from the dependent plugin's `Awake`. Use managed experimental brick registration and enable `EnableNativeBlockVariants` or `SEVO_KEEL_EXPERIMENTAL_NATIVE_BLOCK_VARIANTS=1` in an isolated solo profile.

The initial adapters cover the stock Mag-Lock command and ordinary seated poses. Bed, standing and saddle behavior depends on stock brick identity and those templates are excluded. Keel keeps the native command's saved state, activation and cleanup paths, and copies the verified stock command arguments. Mods choose their stock template, content key, display name and Codex category. The attachment API for custom weapon visuals is a separate contract.

The Mag-Lock still applies the native mass, authority, and attachment eligibility rules. Terrain anchoring requires another contract.

## Solo acceptance

Load the probe in an isolated installation and confirm both variants appear in the Codex. Place a clamp, lock and release an eligible loaded grid, and verify the native lock signal. Save with the lock engaged and confirm it restores after reload. Delete an engaged clamp and verify release. Enter and leave the passenger seat and confirm ordinary seat behavior after reload. Use stock controls in the same world as the baseline.
