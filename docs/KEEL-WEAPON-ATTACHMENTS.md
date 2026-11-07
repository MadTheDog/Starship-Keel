# Keel weapon attachment slots

**Keel 0.1.4** adds stock-template attachments with optional custom render meshes. This experimental API targets managed disposable solo profiles on Steam build **26451593 / 26w45a**, Unity **6000.6.6f1** on Windows x64 Mono. Keel checks the native attachment contracts before admitting declarations.

## Register a receiver and its attachments

Call these methods during your dependent plugin's `Awake`. Set its minimum dependency to Keel `0.1.4` when using the attachment API.

```csharp
ContentRegistration.RegisterWeaponAttachmentReceiver(
    ownerId, localName, revision, displayName,
    selectReceiverTemplate, compatibleAttachmentKeys, behaviorFactory);

ContentRegistration.RegisterWeaponAttachment(
    ownerId, localName, revision, displayName, selectAttachmentTemplate);
```

The receiver accepts an `IEnumerable<string>` of complete `ownerId:localName` keys. Keel copies the list at registration. Each addon gets its own durable content key and ID. Two addons can select the same stock template and still be distinct. The compatible keys can belong to another mod.

Selectors return stock `Brick` definitions after native resources load. A receiver selects a vertically stretched stock weapon template with the native side-slot layout. An addon selects a fixed-size nonstackable side attachment with a static collider and baked materials. Keel reuses the stock geometry, including a supported native repeat ring, and the receiver's side slot at each vertical segment. Stock weapon firing and upgrade effects are owned by the stock weapon system. Your receiver's behavior implements its own effects and rules.

## Read the current placement

On the game thread, call `IReadOnlyList<WeaponAttachmentSlot> slots = context.GetWeaponAttachmentSlots()`. Each entry has an `Index`, the native `BrickCoord Slot`, and the nullable `string? AttachmentKey` and `BrickCoord? Attachment`. An empty slot has both attachment fields null. The index describes the current receiver layout. Use the full key to identify the module. Take a fresh snapshot after placement, removal, or reconstruction. Loaded attachment links resolve after entity initialization, so an early lifecycle callback can observe an empty slot.

The managed receiver accepts only its declared managed attachment keys. Stock weapons retain their own attachments. Keep the mod's effects, asset choices, and compatibility rules in its own code. Give a new receiver a separate content key from an already shipped block whose geometry or native lifecycle differs. Preserve the Keel registry alongside any corresponding saves.

## Supply a custom render mesh

Require Keel `0.1.4` when using the optional visual overload. The selected stock template supplies the attachment type, mounting and collider rules, mass, gameplay bounds, baked material palette, and fallback visual. Keep the same key and revision for this cosmetic change.

```csharp
ContentRegistration.RegisterWeaponAttachment(
    ownerId, localName, revision, displayName,
    selectAttachmentTemplate,
    () => new WeaponAttachmentVisual(modMesh, materialSlotPerTriangle));
```

Use one readable triangle submesh with at most 4,096 input vertices and 2,048 triangles. Supply Unity-local positions inside the stock render envelope, unit normals and tangents, and UV0 in the unit square. The tangent `w` must be `-1` or `1`. UV1 and vertex colors are optional. Supply one zero-based index into the selected template's baked material palette per triangle. Supported slots use static opaque, glow-paint, or glow-diffuse materials. The mod owns the input mesh. Keel copies it and the native constructor creates a separate preview.

The game uses the same native mesh batching, renderer, and stock collider. A missing or rejected visual falls back to the selected stock template under the same content key. Keel logs invalid geometry, materials, or native compatibility failures. If the stock placement template itself is incompatible, the declaration is skipped. Scope the initial feature to static render meshes within a stock attachment footprint.

The isolated 0.1.4 probe verified a custom two-material mesh, the native preview and opaque batch, an invalid-material fallback, stock collider and bounds parity, and attachment placement and in-memory reload. It replayed a 0.1.3 registry byte for byte while Hull Filler 0.1.3 registered its four blocks and convex colliders. The probe used a purpose-built fixture. Mod-specific assets and interactive Hull Filler placement require separate testing.

Use a disposable profile with `EnableFunctionalBlocks` enabled. The automated solo lab exercised the native preview predicate and builder on a rotated, stretched receiver with three slots. It verified repeat ring lookup, distinct keys sharing one template, rejection of incompatible and occupied placements, in-memory save reconstruction, detachment, and receiver removal. Interactive build-tool input, child-grid placement, attachment symmetry, multiplayer, blueprints, and saves across process restarts require separate validation.
