# Starship Keel

Starship Keel is a modding framework for Starship EVO. Its SDK assigns persistent numeric IDs to namespaced mod content. Its BepInEx runtime provides experimental registration, placement, lifecycle callbacks, and sustained logic input. Mods supply their own block designs and behavior.

[Starship Uplink](https://github.com/MadTheDog/Starship-Uplink) installs Keel, prepares BepInEx, and manages the profile and launch session used by the runtime. Players can get both from the [Uplink releases](https://github.com/MadTheDog/Starship-Uplink/releases). Mod authors can download the SDK and runtime DLLs from [Keel releases](https://github.com/MadTheDog/Starship-Keel/releases).

## Manual install with BepInEx

Use this option if Starship EVO already runs with BepInEx 5.4.23.5 on Windows x64 Mono.

1. Close the game and download [Starship-Keel-0.1.2-BepInEx.zip](https://github.com/MadTheDog/Starship-Keel/releases/download/v0.1.2/Starship-Keel-0.1.2-BepInEx.zip).
2. Extract the ZIP into the folder containing `Starship EVO.exe`. Its two DLLs go into `BepInEx/plugins/SevoPlatform`. Keep a single copy of each Keel DLL under `BepInEx/plugins`.
3. Launch the game through Steam. Check `BepInEx/LogOutput.log` for `Loading [Starship Keel 0.1.2]` and `Mode=audit`.

This verifies Keel startup with your existing loader. Experimental block registration requires a valid Uplink-managed session and an explicit opt-in. To undo a manual install, close the game and remove the two Keel DLLs you extracted.

## Current scope

Keel defaults to audit mode. Experimental registration requires an explicit opt-in and a valid Uplink-managed session. The ordinary sample mods demonstrate stock hull registration and persistent IDs. The [functional-block API](docs/KEEL-FUNCTIONAL.md) lets a mod choose a supported native template and receive native Switch or Event Gate input in solo play. Disk persistence, blueprints, custom collision geometry, and multiplayer remain outside the verified release scope.

The test target is Windows x64 Mono, **Steam build 26451593 / 26w45a**, Unity **6000.6.6f1**. Keel evaluates required methods, layouts, and lifecycle hooks at runtime. Its BepInEx plugin ID is `local.sevo.modplatform`.

See the [framework API](docs/FRAMEWORK.md), [examples](samples/README.md), and [changelog](CHANGELOG.md).

## Build from source

Install the .NET 8 SDK. Use the reference assemblies from your own Starship EVO installation and the [official BepInEx 5.4.23.5 Windows x64 distribution](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5).

- Copy the game assemblies referenced by `src/Sevo.Framework.Runtime/Sevo.Framework.Runtime.csproj` from `Starship EVO_Data/Managed` into `.refs/game`.
- Copy `BepInEx.dll`, `0Harmony.dll`, and `Mono.Cecil.dll` from `BepInEx/core` into `.refs/bepinex`.
- Run `dotnet build src/Sevo.Framework.Runtime/Sevo.Framework.Runtime.csproj -c Release`. Build the SDK separately with `dotnet build src/Sevo.Framework.Sdk/Sevo.Framework.Sdk.csproj -c Release` if you only need the game-independent allocator.
- Run `dotnet run --project tests/Sevo.Framework.Tests/Sevo.Framework.Tests.csproj -c Release` for the framework checks, or `./samples/Build-Samples.ps1` for sample mod ZIPs.

Game and third-party reference assemblies are supplied locally. The Keel release contains the two Keel DLLs.

Starship Keel is an unofficial community project.
