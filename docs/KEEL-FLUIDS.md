# Keel fluid hooks

Keel 0.1.6 lets a BepInEx mod declare fluid definitions and implement producers and consumers that use compatible stock tanks and pipes. Enable the experimental fluid capability in a managed Uplink solo profile with `SEVO_KEEL_EXPERIMENTAL_FLUIDS=1` or `[Experimental] EnableFluidRegistration = true`. Use a disposable profile for this preview.

## Declare definitions and machine behavior

In the dependent plugin's `Awake`, register definitions under your own namespace. Each definition has a display name, an RGB color, kilograms per litre, and joules per litre. Keel persists the assigned contextual ID in the managed profile. Keep the same key and revision for the same saved fluid identity.

```csharp
FluidRegistration.Register("example.cooling", "coolant", 1,
    new FluidDefinition("Coolant", 0x36A6E6, kilogramsPerLitre: 1.12f, joulesPerLitre: 0f));
FluidRegistration.RegisterTick("example.cooling", "fluid-machines", TickMachines);
```

Keel invokes the registered callbacks after the game's jobs complete, once per rendered gameplay frame with a positive time step. The callback receives a `FluidTickContext`. Resolve live `BrickCoord` values for tanks selected by your mod each time. The context expires when the callback returns. A callback exception disables that callback for the session and logs its key.

```csharp
void TickMachines(FluidTickContext tick)
{
    const string coolant = "example.cooling:coolant";
    if (TryGetProducerTank(out BrickCoord producer) && tick.TryBindTank(producer, coolant))
        tick.Produce(producer, coolant, 4f * tick.DeltaTime);

    if (TryGetConsumerTank(out BrickCoord consumer) && tick.TryBindTank(consumer, coolant))
    {
        tick.TryRequestRefill(consumer, coolant);
        float consumed = tick.Consume(consumer, coolant, 3f * tick.DeltaTime);
        // Apply your machine effect according to the actual volume consumed.
    }
}
```

`TryGetProducerTank` and `TryGetConsumerTank` are supplied by the mod. Connect the endpoints with compatible stock pipes. `TryBindTank` only claims an empty tank without a pending request, or accepts a tank already bound to the requested fluid. Keel configures bound tanks outside the stock reactor and engine fuel pools. Bound tanks use mod controls because the stock tank menu offers only built-in fluids.

`TryGetAmount` reads the volume in litres. `Produce` and `Consume` report the actual volume changed. `TryRequestRefill` queues a stock pull for the tank's available capacity in whole litres. Transport still follows the stock pipe network and its flow limits. Keel provides metadata through `TryGetDefinition`. Apply density, energy and color in your own gameplay or rendering. Stock reactors, electrolyser recipes, harvesters, fuel filters and Burst tank shader colors retain their built-in behavior.

The profile registry preserves numeric assignments and reserves IDs of removed definitions. Keep the registry with its world and mods. Keel seals this catalog on the first eligible post-job frame after loading. The catalog therefore cannot validate or admit a world before the game opens it. Save and blueprint portability between profiles, missing-mod world admission, and multiplayer synchronization are outside this preview.

See the [disposable tank and pipe probe](../samples/Example.FluidProbe/README.md) for a complete fixture and its test criteria.
