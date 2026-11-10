# Terrain mining drop probe

Enable the item, terrain material and terrain mining drop hooks in a disposable managed solo profile and set `SEVO_KEEL_TERRAIN_MINING_PROBE=1`. The probe requires the Keel hooks lab and a creative solo session.

The sample calls the audited native drop method with synthetic voxel values. It checks one pickable quantity-two registered drop, a stock impulse, stock drops for an ambiguous declaration and missing item, and zero drops when the voxel does not cross the mining surface. The probe releases synthetic drops immediately. It does not dig terrain or write terrain saves.

The scale check compares the native item scale and both transform scales against the stock seeded draw. Raw ore uses the stock fixed range and an ordinary rock replacement exercises the variable range. Set `SEVO_KEEL_TERRAIN_MINING_ALUMINIUM=1` with the Aluminium fixture installed to compare its registered chunk with stock raw Silver. This mode uses the same synthetic completed voxel path.
