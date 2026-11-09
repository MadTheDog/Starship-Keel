# Terrain placement probe

The [terrain placement guide](../../docs/KEEL-TERRAIN-PLACEMENT.md) describes the ordinary, nominal-radius, and authored-surface rules. This sample also contains a two-pass stock survey fixture for a bounded patch of the starting planet.

Build and load the probe and the lab's Fluid Probe in the dedicated Keel generation lab. Run these commands from the Keel checkout. Use an unused managed state name for each capture and use that same name for its replay.

```powershell
$state = 'terrain-stock-survey110'
$env:SEVO_KEEL_TERRAIN_PLACEMENT_PROBE = '1'
$env:SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_DIAGNOSTICS = '1'
$env:SEVO_KEEL_TERRAIN_PLACEMENT_PROBE_SOURCES = 'TEX_ROCK_A;TEX_ROCK_B;TEX_CLIFF_A;DIRTY_ROCK_A;DIRTY_ROCK_B;GRASS_A;MUDDY_SOIL_A;DESERT_SAND_DUNE_A;PEBBLE_SAND_A'
$env:SEVO_KEEL_LAB_BOOT_SURVIVAL = '1'
$env:SEVO_KEEL_LAB_SUBMIT_TERRAIN_BUFFER = '1'
$env:SEVO_KEEL_LAB_TERRAIN_GAME = (Resolve-Path 'lab/game').Path
$env:SEVO_KEEL_TERRAIN_PLACEMENT_STOCK_SPACING = '16'
$env:SEVO_KEEL_TERRAIN_PLACEMENT_STOCK_SURVEY = 'capture'
$env:SEVO_KEEL_EXPERIMENTAL_TERRAIN_PLACEMENT = '0'
$env:SEVO_KEEL_EXPERIMENTAL_TERRAIN_AUTHORED_SURFACE_PLACEMENT = '0'
& ./tools/Invoke-ContentHookLab.ps1 -Mode Hooks -Rendered -FluidProbe -DiagnosticJobCompletion `
    -ManagedStateName $state -TimeoutSeconds 180 -ExpectedLog 'TERRAIN STOCK SURFACE SURVEY CAPTURE PASS'

$env:SEVO_KEEL_TERRAIN_PLACEMENT_STOCK_SURVEY = 'replay'
$env:SEVO_KEEL_EXPERIMENTAL_TERRAIN_PLACEMENT = '1'
$env:SEVO_KEEL_EXPERIMENTAL_TERRAIN_AUTHORED_SURFACE_PLACEMENT = '1'
& ./tools/Invoke-ContentHookLab.ps1 -Mode Hooks -Rendered -FluidProbe -DiagnosticJobCompletion `
    -ManagedStateName $state -TimeoutSeconds 180 -ExpectedLog 'TERRAIN STOCK SURFACE SURVEY REPLAY PASS'
```

Keep the same game build, starting system, planet, generation settings, and source materials in both passes. Use fresh terrain in a disposable solo character. Capture writes `terrain-stock-surface-v1.bin` once in the marked managed state directory. The file records the stock generated density lattice at the configured spacing and its observed native level. Native levels count subdivisions from the body root, whose size depends on the body radius.

The survey follows each body-local ray from the outer edge of the captured lattice. It accepts a crossing only after covered air and a uniquely bracketed transition in a trilinear cell. Rays with incomplete or ambiguous coverage retain their original materials. The measured depth refers to the fixed sampled surface, with the precision of the canonical lattice.

Replay requires the same 8,000 packed density values when the canonical node is generated again. It compares the material channels, packed colour and density before native octree publication. The final probe marker also requires remapped solid samples at two detail levels and unchanged solid samples outside the placement band.
