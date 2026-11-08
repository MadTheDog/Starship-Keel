# Fluid loop probe

This separate sample mod registers two fluids and uses the stock tank, pipe and tank sequence to exercise the Keel fluid API. It targets the Keel 0.1.6 development lab on Steam build **26451593 / 26w45a**.

Build with the Keel SDK and runtime projects plus the lab's private game and BepInEx references. Copy only `Sevo.Example.FluidProbe.dll` to the disposable lab's `BepInEx/plugins` directory. Set `SEVO_KEEL_EXPERIMENTAL_FLUIDS=1` and `SEVO_KEEL_FLUID_PROBE=1` in the managed launch environment. Set `SEVO_KEEL_FLUID_PROBE_START_SOLO=1` to make a fresh disposable creative fixture from the title screen, or open an isolated solo world yourself. Use a rendered launch.

The probe places two tanks connected by a stock pipe, assigns both the same fluid, and checks that a different registered fluid is rejected. It fills the source and queues a refill at the sink. After the stock job has moved fluid, it checks that the combined volume matches the produced volume and then consumes half of the observed volume at the sink. The BepInEx log records `KEEL_FLUID_PROBE PASS` with the four volumes or `KEEL_FLUID_PROBE FAIL` with a stage and an error.

Set `SEVO_KEEL_FLUID_PROBE_ROUNDTRIP=1` to also serialize the fixture using the stock MessagePack save codec, reconstruct it in a fresh grid in the same process, and check the saved fluid and remaining litres before reporting a pass.

For lab diagnosis only, `SEVO_KEEL_FLUID_PROBE_DRAIN_STALLED_FRAME=1` lets the probe invoke the stock end-of-render method at the end of a frame when the game's current frame jobs are still pending. It waits for at least six missed frame completions over 500 ms before intervening. The diagnostic stays available after the fluid check so other probes can finish in the same run, until the bounded lab runner quits the game. An assisted run reports `mode=harness` and the number of intervened frames at the fluid result. Such a result verifies the exercised transfer path with a harness-driven frame boundary. A normal run reports `mode=native`.
