# Keel machine recipe probe

Build with the repository-local .NET SDK and private game references. Use Keel 0.1.8 with Starship EVO Steam build ID 26451593 / 26w45a. Load the sample in a disposable lab profile with the experimental item and machine recipe hooks enabled. Set `SEVO_KEEL_MACHINE_PROBE=1` and start an isolated solo session through the lab. The sample waits for a valid launcher session ID and a solo server with remote listening disabled.

The fixture places a stock arc furnace and crusher on a new hull. It verifies their input predicates, installs a stock drill bit, and holds the furnace unpowered for longer than the custom recipe duration. Then it places and fuels a stock reactor. The furnace uses the iron context from the stock schematic and the terrain's small-rock ore prefab. It must consume one ore and produce three custom dust in its inventory. The probe transfers those three dust from native storage into the crusher's live input slot. The crusher must consume the dust and put two distinct custom pellets in its inventory. The sample destroys the fixture on exit when the stock job boundary allows cleanup.

Set `SEVO_KEEL_MACHINE_PROBE_CONVEYOR=1` to run the extra conveyor phase. The furnace must pull and process iron from stock connected storage, leave the coal, and push its custom dust back through the tube. A second stock tube and storage unit feed the crusher. It must pull the registered dust, leave the unrelated registered pellets in source storage, and produce its own pellet stack.

Set `SEVO_KEEL_MACHINE_PROBE_ROUNDTRIP=1` to serialize the fixture in memory with a crusher recipe in progress. The reconstructed fixture must retain the committed outputs, drill wear and in-flight input, with zero recipe progress and no pending output. It must then finish the restarted recipe once.

The log reports `KEEL_MACHINE_PROBE POWER PASS`, `FURNACE PASS`, `CHAIN PASS`, and the optional `CONVEYOR PASS` and `ROUNDTRIP PASS`. `KEEL_MACHINE_PROBE PASS` marks the end of the run. The first error logs `KEEL_MACHINE_PROBE FAIL` with the fixture stage. Discard the lab save afterward.
