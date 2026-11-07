# Keel weapon attachment slots

Keel 0.1.4 adds stock-template attachments and custom addon meshes. Keel 0.1.5 adds mod-owned palettes, paint, custom receiver visuals, and native Codex categories. These features target managed disposable solo profiles on Steam build **26451593 / 26w45a**, Unity **6000.6.6f1** on Windows x64 Mono. Keel checks the native attachment contracts before admitting declarations.

## Register a receiver and its attachments

Call these methods during your dependent plugin's `Awake`. Declare Keel `0.1.4` as the minimum dependency for the original attachment API, or `0.1.5` when using the palettes, paint, receiver visuals, or Codex category options described here.

```csharp
ContentRegistration.RegisterWeaponAttachmentReceiver(
    ownerId, localName, revision, displayName,
    selectReceiverTemplate, compatibleAttachmentKeys, behaviorFactory);

ContentRegistration.RegisterWeaponAttachment(
    ownerId, localName, revision, displayName, selectAttachmentTemplate);
```

The receiver accepts an `IEnumerable<string>` of complete `ownerId:localName` keys. Keel copies the list at registration. Each addon gets its own durable content key and ID. Two addons can select the same stock template and still be distinct. The compatible keys can belong to another mod.

Use the category-aware overloads with `codexCategory: "WEAPONS"` to list a receiver or addon under **Items → Weapons**. The same option accepts other visible native category names. Owned repeats stay hidden. See [Choose a Codex category](FRAMEWORK.md#choose-a-codex-category).

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

## Supply mod-owned colors, textures, and glow

Build against Keel **0.1.5** to use the palette overload.

```csharp
var materials = new[]
{
    new VoxelVisualMaterial(fallbackMaterialSlot: 0, albedo: new Color32(70, 110, 165, 255)),
    new VoxelVisualMaterial(fallbackMaterialSlot: 1, albedo: panelTexture,
        emission: 200, useAlphaAsEmissionMask: true)
};
return new WeaponAttachmentVisual(modMesh, materialSlotPerTriangle, materials);
```

Each triangle slot indexes the mod palette. Each material declares an opaque baked fallback slot from the selected stock template. The fallback slots may repeat or appear in any order. If a palette is rejected, Keel retains the custom mesh and maps its triangles back to those baked slots. If the geometry or fallback slots are invalid, Keel uses the stock visual. Both fallback paths preserve the content key and native placement.

A solid material uses RGB and ignores the color's alpha. A textured material uses the RGB of a readable **256 × 256** `Texture2D` in the game's gamma-encoded albedo convention. Keep caller-owned input meshes and textures available through native resource admission. Keel prepares the mip chain and uses the same BC3 compression as the stock atlas.

Emission ranges from 0 to 255. The optional texture alpha mask scales it per texel, and the native glow takes its color from the surface. These surfaces are opaque with normal mapping disabled. Separate emission colors, transparency, borders, and custom normal or specular maps are outside this API.

### Opt individual materials into stock paint

Use `paintable: true` with either a solid color or texture. Existing calls retain fixed colors. A brick stores one native paint color, and that color applies to the palette entries that opt in. For example, assign one slot to paintable panels and leave a separate emissive slot at its authored color.

```csharp
var panels = new VoxelVisualMaterial(0, panelTexture, paintable: true);
var fixedGlow = new VoxelVisualMaterial(1, new Color32(64, 200, 255, 255), emission: 180);
```

For a paintable material, the game's paint overlay uses the albedo's red channel as grayscale detail. A neutral gray around 128 is a starting point for a solid painted surface. The stock shader falls back to the brick's native material palette for exact RGB zero. A paintable emissive material glows in the painted surface color. With the stock paint contract unavailable, Keel logs the reason and displays the authored fixed colors.

### Choose native placement and item-preview colors

```csharp
return new WeaponAttachmentVisual(modMesh, materialSlotPerTriangle, materials)
    .WithPaint(
        defaultPaint: new Color32(224, 224, 224, 255),
        previewPaint: new Color32(206, 212, 224, 255));
```

`WithPaint` returns a new descriptor. Each nullable argument can inherit its own stock value. `defaultPaint` sets the native default for new placement and paint erase. The game can still use a color carried by a selected inventory item. `previewPaint` feeds the native item preview fields. Loaded instance colors still come from the save. The ordinary build hologram uses the game's placement color. If Keel rejects the visual or its requested custom palette, the definition keeps its stock paint defaults.

The solo paint probe exercised the native builder paint action on a placed attachment, checked the packed GPU color and mixed palette, and confirmed the color through native MessagePack serialization and in-memory grid reconstruction. The probe submitted the same action as the stock tool after its hit checks.

Keel can append up to 32 materials per visual and 64 per session, subject to GPU texture-array limits. It prepares accepted palettes in content-key order and keeps the expanded diffuse and specular atlases for the game process. The first custom palette incurs a copy of the stock slices in both atlases.

Use a disposable profile with `EnableFunctionalBlocks` enabled. The automated solo lab exercised the native preview predicate and builder on a rotated, stretched receiver with three slots. It verified repeat ring lookup, distinct keys sharing one template, rejection of incompatible and occupied placements, in-memory save reconstruction, detachment, and receiver removal. Interactive build-tool input, child-grid placement, attachment symmetry, multiplayer, blueprints, and saves across process restarts require separate validation.

## Supply receiver host and repeat meshes

Keel 0.1.5 supports both host and repeat visuals. For a custom host with stock repeats, append the host selector to the existing receiver declaration.

```csharp
ContentRegistration.RegisterWeaponAttachmentReceiver(
    ownerId, localName, revision, displayName,
    selectReceiverTemplate, compatibleAttachmentKeys, behaviorFactory,
    selectVisual: selectHostVisual);
```

To customize the repeat on newly placed receivers, append the companion name and its visual selector. This form also accepts `selectVisual: null` to keep the stock host.

```csharp
ContentRegistration.RegisterWeaponAttachmentReceiver(
    ownerId, localName, revision, displayName,
    selectReceiverTemplate, compatibleAttachmentKeys, behaviorFactory,
    selectVisual: selectHostVisual,
    repeatLocalName: "receiver-repeat",
    selectRepeatVisual: selectRingVisual);
```

Both selectors return `WeaponAttachmentVisual?` and support the same mesh and palette contract as an attachment. Author the host in the selected template's local render envelope and for its native vertical stretch. Author the repeat in the stock ring's own local envelope. Model the fitting around the template's existing socket positions. Keel retains those positions, stock collision and bounds, resize mode, and repeat start and end offsets. Repeat records inherit the host's command and data handles and follow the stock collision-tree rules.

`repeatLocalName` reserves a second durable brick key under the same mod owner. Keep the receiver key, repeat key, and revision stable for existing saves. Keel derives the repeat template from the selected receiver and keeps the companion out of standalone inventory. A missing or rejected custom ring visual uses the stock ring geometry under the owned repeat ID. The stock repeat definition is shared by native blocks and stays intact.

Older saved receivers keep their saved stock repeat records and can still accept attachments through those rings. New placements use the owned repeat and its custom visual. Host visuals apply to the receiver definition regardless of which ring IDs its saved instances contain. Keep the Keel registry with those saves when using the owned repeat option.

The solo lab checked native world batches and preview meshes for a rotated receiver with three repeat segments. It verified the batch after native health updates, attachment placement through both owned and legacy stock rings, stock bounds and collider parity, and reconstruction from MessagePack data. The composed preview follows the game's native path. Interactive appearance of a particular mod's mesh, saves across process restarts, and multiplayer remain separate checks.

### Render full modular sections

Append `modularSections: true` to the owned-repeat overload when the selected stock receiver has a native repeat at every bay, including bay zero. The repeat selector describes one full section. Author its vertices inside a cube centered at the mesh origin, with each side equal to one native repeat interval. Use local Y for stretch. Keel places that centered mesh at the native bay center along Y, so its lower edge aligns with the repeat placement origin. For the stock side-slot layout, a fitting on the positive Z face sits at authored Y zero.

```csharp
ContentRegistration.RegisterWeaponAttachmentReceiver(
    ownerId, localName, revision, displayName,
    selectReceiverTemplate, compatibleAttachmentKeys, behaviorFactory,
    selectVisual: selectHostOutline,
    repeatLocalName: "receiver-repeat",
    selectRepeatVisual: selectFullSection,
    modularSections: true);
```

Keel renders one unscaled section at each native repeat position and suppresses the visible host batch. The host retains its native render data handle, collider, resize settings, and sockets. The section's world mesh, preview mesh, and renderer culling box use the same axial offset. The host's optional visual can provide the native outline. The game's composed preview renders its first bay rather than generating the stretched section array used in the world.

For a modular receiver, set the placement default on the host descriptor. Native repeat instances inherit that color. Set the item preview color on the repeat descriptor for preview paths that use the repeat's own paint fields. If the layout contract or section mesh is rejected, the host and repeat use their stock visuals under the same registered IDs. A reconstructed stock repeat can render the modular section through a renderer-only substitution while retaining its saved brick ID and paint.
