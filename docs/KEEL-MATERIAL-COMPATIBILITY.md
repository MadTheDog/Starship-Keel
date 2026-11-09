# Custom blocks and voxel materials

Use a namespaced content key and register your block through Keel. Let Keel assign the persistent brick ID. Pick a stock template whose placement, collision, and behavior contract matches the block you are creating.

For an attachment or other visual supported by the [attachment API](KEEL-WEAPON-ATTACHMENTS.md), declare its mesh, face material slots, and optional palette through the documented registration calls. Every custom slot needs a valid baked material slot on the stock template. This fallback lets Keel draw the attachment if custom palettes are unavailable. Use stock-backed visuals for block types that have no documented custom palette contract.

## Capability and limitation reference

| Capability | Supported contract | Limit |
| --- | --- | --- |
| Custom block identity | Keel assigns persistent IDs to namespaced keys in an opted-in managed profile. | Legacy hardcoded IDs and save migration require separate compatibility work. |
| Block placement and collision | Use an audited stock template through the block or functional-block API. | Arbitrary native collision geometry is outside this contract. |
| Attachment mesh and palette | Declare mesh, face slots, materials, and baked fallbacks through the attachment API. | A custom palette is available only when Keel admits the profile and audits the native bindings. |
| Ordinary block palette | Use a documented stock-backed template. | This release has no general-purpose custom voxel palette API for ordinary blocks. |
| Legacy atlas extension | Keep Keel-owned rows and referenced texture bytes at their existing indices. | Keel verifies its owned managed data. It cannot enforce another plugin's private GPU writes or runtime changes after admission. |
| Save and multiplayer behavior | Validate placement and save/load in an isolated solo profile for the content you ship. | These checks establish neither general blueprint migration nor multiplayer synchronization. |

## Shared resource rules

- Submit declarations during registration. Keel assigns native material rows and texture layers for the session. Treat native row and layer numbers as runtime details.
- Keep assets and visual choices in the owning mod. Keep stock resources and the contents of other mods' rows and layers untouched.
- A Keel mod must leave writes to the voxel material table, its cursor and GPU buffer, the voxel diffuse alias, and the global diffuse and specular texture bindings to the runtime. A private Harmony patch that changes these bypasses Keel's registration contract.
- A legacy atlas extension can coexist if it preserves the layout, Keel-owned rows and their indices, and every mip of the layers those rows reference. Any replacement must also keep the CPU diffuse alias and the shader binding in agreement. Keel checks the managed bindings and readable mip data at readiness and stock world entry. Mods that bypass the contract remain responsible for coherent GPU uploads and for their own content.
- Keel retains the expanded attachment atlases across stock world loads. Verify their bindings after the game unloads unused assets during character entry.
- A conflict diagnostic names the registered palettes and first mismatched binding. If preparation cannot safely publish a custom palette, Keel uses the baked fallback. If bindings become incompatible after publication, restart with a compatible profile. The stock new-character, saved-character, and lobby buttons stay at the menu rather than starting a session with those bindings.

## Mod author checks

Use an isolated profile. Verify registration and visuals with your mod alone and with an independent custom-material mod. Reload a copied world, confirm assigned content identities still resolve, and inspect both mods' meshes and materials. Check the baked fallback too. For an attachment mod, include placement preview and paint when the material supports paint.
