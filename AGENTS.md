# ModernAtlas project guide

## Product goal

ModernAtlas is a client-side Vintage Story 1.22.6 mod that should turn the
world map into a readable, Google-Earth-like 3D atlas while remaining an
independent product with its own name and visual identity.

The target view contains terrain relief, mountains, water, block-built
structures, ruins and trees. It should resemble the approved ModernAtlas
mock-up: a tilted, textured, softly lit map with optional live-looking cloud
cover.

## Rendering scope

- Render world blocks and their actual block shapes and textures.
- Support blocks registered by other mods through the public Vintage Story
  block and texture-atlas APIs. Do not hard-code only vanilla block IDs.
- Render terrain, buildings, ruins, vegetation and fluids when they are made
  from blocks.
- Do not render entities, players, creatures, dropped items, held tools,
  weapons, armor, particles, damage effects or other transient scene objects.
- Use a neutral stone material when a block or texture cannot be resolved.
  Never intentionally display the missing-texture question-mark material.
- Clouds are a separate visual overlay. They must not become cached world
  geometry and must not disclose unexplored terrain.

## World-data safety

- Never replace, migrate or write Vintage Story's vanilla map database or save
  database.
- Read only world/chunk data already available to the client. Never request or
  generate unexplored terrain solely for the atlas.
- Store persistent rendered tiles or meshes in a ModernAtlas-owned cache with
  the world identity in its path. A cache failure must not damage a world.
- Existing vanilla exploration, waypoints and maps must remain usable.
- Exact block geometry is available only while chunks are client-loaded.
  Cache useful atlas data as chunks are visited so the 3D atlas fills in over
  time; fall back to vanilla terrain colors/height where exact data is absent.

## Performance and compatibility

- Build chunk snapshots on the main thread only when required by the API;
  perform expensive mesh/raster work off-thread using immutable snapshots.
- Apply strict per-frame and per-tick budgets. Opening the atlas must not cause
  a large synchronous scan or visible gameplay freeze.
- Invalidate cached atlas chunks after block changes and rebuild them lazily.
- Prefer public Vintage Story APIs. Isolate any unavoidable game-content API
  integration behind a small adapter and document why it is needed.
- Test with an isolated Vintage Story data directory containing vanilla plus
  ModernAtlas. Do not delete or permanently disable the user's other mods.

## Legal and repository rules

- Do not include Google names, logos, map tiles, imagery or other Google
  branding in the mod or release assets.
- Do not redistribute Vintage Story game assets or source files. Runtime use of
  installed texture atlases through the official API is allowed.
- Keep original ModernAtlas code under the repository's MIT license.
- Use `apply_patch` for hand-written source changes, run a Release build, test
  the release archive and keep Git commits focused.

## Delivery phases

1. Preserve the vanilla map and add terrain relief (prototype complete).
2. Capture loaded block surfaces into a safe per-world atlas cache.
3. Render a tilted, navigable 3D block atlas with mod texture support.
4. Add incremental block-change updates, quality settings and fallback tiles.
5. Add optional animated clouds and visual polish matching the mock-up.
