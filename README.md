# Starship Keel

Starship Keel is a modding framework for Starship EVO. Its SDK assigns persistent numeric IDs to namespaced mod content. Its BepInEx runtime provides experimental registration, placement, lifecycle callbacks, and sustained logic input. Mods supply their own block designs and behavior.

[Starship Uplink](https://github.com/MadTheDog/Starship-Uplink) installs Keel, prepares BepInEx, and manages the profile and launch session used by the runtime. Players can get both from the [Uplink releases](https://github.com/MadTheDog/Starship-Uplink/releases). Mod authors can download the SDK and runtime DLLs from [Keel releases](https://github.com/MadTheDog/Starship-Keel/releases).

## Manual install with BepInEx

Use this option if Starship EVO already runs with BepInEx 5.4.23.5 on Windows x64 Mono.

1. Close the game and download the `-BepInEx.zip` asset from your chosen [Keel release](https://github.com/MadTheDog/Starship-Keel/releases).
2. Extract the ZIP into the folder containing `Starship EVO.exe`. Its two DLLs go into `BepInEx/plugins/SevoPlatform`. Keep a single copy of each Keel DLL under `BepInEx/plugins`.
3. Launch the game through Steam. Check `BepInEx/LogOutput.log` for the expected Starship Keel version and `Mode=audit`.

This verifies Keel startup with your existing loader. Experimental block registration requires a valid Uplink-managed session and an explicit opt-in. To undo a manual install, close the game and remove the two Keel DLLs you extracted.

## Current scope

Keel defaults to audit mode. Experimental registration requires an explicit opt-in and a valid Uplink-managed session. The sample mods demonstrate stock hull registration and persistent IDs. The [functional-block API](docs/KEEL-FUNCTIONAL.md) supports native templates and sustained Switch or Event Gate input in solo play. The [attachment API](docs/KEEL-WEAPON-ATTACHMENTS.md) supports native side slots, custom meshes and materials, native paint, and modular receiver visuals within the stock placement contract. Mods can [choose a native Codex category](docs/FRAMEWORK.md#choose-a-codex-category) for their managed content. The verified scope is solo placement and in-memory save reconstruction.

Keel 0.1.6 adds opt-in [native recipe registration](docs/KEEL-RECIPES.md), [fluid definitions and tank hooks](docs/KEEL-FLUIDS.md), and [ore, terrain material, and scanner hooks](docs/KEEL-TERRAIN-HOOKS.md). Each capability has an independent startup audit and opt-in. Use a managed disposable solo profile for the preview.

The 0.1.7 development preview adds opt-in [native crafting items](docs/KEEL-ITEMS.md) and [world-prop gathering drops](docs/KEEL-GATHERING.md).

The 0.1.8 candidate adds optional meshes, tints and box colliders for registered items and [native machine recipes](docs/KEEL-MACHINE-RECIPES.md) for the Crusher and Arc Furnace.

The 0.2.0 candidate adds opt-in [body environment](docs/KEEL-BODY-ENVIRONMENT.md), [celestial layout](docs/KEEL-CELESTIAL-LAYOUT.md), [planet and asteroid terrain inputs](docs/KEEL-TERRAIN-GENERATION-PROVIDERS.md), [terrain texture overrides](docs/KEEL-TERRAIN-TEXTURES.md), [material registration](docs/KEEL-TERRAIN-MATERIALS.md), [material placement](docs/KEEL-TERRAIN-PLACEMENT.md), [terrain mining drops](docs/KEEL-TERRAIN-MINING-DROPS.md), [custom ore discovery](docs/KEEL-ORE-DISCOVERY.md), [stock-backed storage connectors](docs/KEEL-STORAGE-CONNECTORS.md), and [selectable pass-through geometry](docs/KEEL-SELECTABLE-PASS-THROUGH.md). The owning mod supplies its declarations and assets. Use a disposable managed solo profile for these capabilities. [Galaxy catalog tools](docs/KEEL-GALAXY-CATALOG-PREVIEW.md) support read-only preview and opt-in publication for a dedicated, marked character save root.

The same candidate includes [native block variants](docs/KEEL-NATIVE-BLOCK-VARIANTS.md), [context actions and inventory views](docs/KEEL-CONTEXT-INTERACTIONS.md), [seated poses and dismounts](docs/KEEL-SEAT-INTERACTIONS.md), [surface queries and terrain edits](docs/KEEL-WORLD-SURFACES.md), [assemblies and powered machine work rates](docs/KEEL-BLOCK-ASSEMBLIES.md), [grid physics](docs/KEEL-GRID-PHYSICS.md), [slider paths](docs/KEEL-MECHANISM-PATHS.md), [transfer links](docs/KEEL-TRANSFER-LINKS.md), [player-owned grid recovery](docs/KEEL-GRID-RECOVERY.md), and [claiming mod-owned transient NPC wrecks](docs/KEEL-WRECK-CLAIM.md). Their individual guides describe the supported contracts.

See the experimental [grid attachment requests](docs/KEEL-GRID-ATTACHMENTS.md), [grid destruction notifications](docs/KEEL-GRID-DESTRUCTION.md), [navigation and vehicle assistance](docs/KEEL-NAVIGATION.md), [sublight control leases](docs/KEEL-SUBLIGHT-CONTROL.md), [camera follow leases](docs/KEEL-CAMERA-VIEW.md), [logic outputs](docs/KEEL-LOGIC-OUTPUTS.md), and the separate [HoverRail guide sample](samples/Example.HoverRailGuide/README.md).

The test target is Windows x64 Mono, **Steam build 26451593 / 26w45a**, Unity **6000.6.6f1**. Keel evaluates required methods, layouts, and lifecycle hooks at runtime. Its BepInEx plugin ID is `local.sevo.modplatform`.

See the [framework API](docs/FRAMEWORK.md), [examples](samples/README.md), and [changelog](CHANGELOG.md).

For custom blocks and textures, follow the [Keel material compatibility contract](docs/KEEL-MATERIAL-COMPATIBILITY.md).

## Build from source

Install the .NET 8 SDK. Use the reference assemblies from your own Starship EVO installation and the [official BepInEx 5.4.23.5 Windows x64 distribution](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

- Copy the game assemblies referenced by `src/Sevo.Framework.Runtime/Sevo.Framework.Runtime.csproj` from `Starship EVO_Data/Managed` into `.refs/game`.
- Copy `BepInEx.dll`, `0Harmony.dll`, and `Mono.Cecil.dll` from `BepInEx/core` into `.refs/bepinex`.
- Run `dotnet build src/Sevo.Framework.Runtime/Sevo.Framework.Runtime.csproj -c Release`. Build the SDK separately with `dotnet build src/Sevo.Framework.Sdk/Sevo.Framework.Sdk.csproj -c Release` if you only need the game-independent allocator.
- Run `dotnet run --project tests/Sevo.Framework.Tests/Sevo.Framework.Tests.csproj -c Release` for the framework checks, or `./samples/Build-Samples.ps1` for sample mod ZIPs.
- Run `./tests/Sevo.Framework.Tests/ModularReceiverContract.Tests.ps1 -RuntimeAssembly src/Sevo.Framework.Runtime/bin/Release/netstandard2.1/Sevo.Framework.Runtime.dll` after the Release build for the modular renderer contract checks.

Game and third-party reference assemblies are supplied locally. The Keel release contains the two Keel DLLs.

Starship Keel is an unofficial community project.
