# ModernAtlas project guide

## Product goal

ModernAtlas is a client-side Vintage Story 1.22.6 mod that should turn the
world map into a readable, Google-Earth-like 3D atlas while remaining an
independent product with its own name and visual identity.

The target view contains terrain relief, mountains, water, block-built
structures, ruins and trees. It should resemble the approved ModernAtlas
mock-up: a tilted, textured, softly lit map with optional live-looking cloud
cover.

The atlas is a dedicated full-screen 3D GUI opened with `G`; it is not a skin
for the vanilla blue 2D map. `G` and `Escape` must both close it.

## Project language

- Use English only in source code, comments, documentation, filenames, logs,
  configuration, UI labels and release metadata.
- Do not add translated language files unless the project owner explicitly
  changes this rule. Conversation with the project owner may use Polish.

## Rendering scope

- Render world blocks and their actual block shapes and textures.
- Support blocks registered by other mods through the public Vintage Story
  block and texture-atlas APIs. Do not hard-code only vanilla block IDs.
- Render terrain, buildings, ruins, vegetation and fluids when they are made
  from blocks.
- Preserve the game's live material appearance: texture-atlas coordinates,
  biome tint, sunlight, shadows and connected or multipart block geometry.
- Render water and lava as stable world-aligned block surfaces using their
  registered atlas textures and biome tint. Exclude camera-dependent Fresnel,
  shadows and scene lighting from atlas fluids. Drive texture frames and flow
  from the engine's native water counters, preserve authored water alpha, and
  keep lava opaque. Other transparent block materials may continue through the
  engine OIT path.
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
- Fog must conceal terrain that the client is not allowed to know. The clear
  radius is anchored to the player's actual world position; panning or rotating
  the atlas camera must never move or enlarge that revealed area.

## Camera and controls

- Support zoom, a freely rotatable 360-degree yaw and a useful top-down to
  tilted camera range without clipping the top or bottom of loaded terrain.
- Left-drag pans in screen space: horizontal mouse motion stays horizontal and
  vertical mouse motion stays vertical regardless of camera yaw. Do not map a
  downward drag to an unexpected compass direction.
- Right-drag rotates and tilts the camera. Middle-click resets the view.
- Keep atlas GUI controls clickable and prevent atlas input from leaking into
  the hotbar, inventories or dialogs underneath it.

## Radius and multiplayer rules

- Offer radii of 250, 500, 750, 1000 and 1500 blocks. Do not expose values
  above 1500 because Vintage Story 1.22.6 has a 1536-block view-distance limit.
- Default to a 500-block radius. Validate old or malformed configuration values
  and clamp them to 1500 without crashing.
- Singleplayer may expose radius, fog, performance and animation settings.
  Multiplayer must keep conservative client-only limits and must not request
  distant chunks or disclose activity outside data already sent by the server.

## Performance and compatibility

- Build chunk snapshots on the main thread only when required by the API;
  perform expensive mesh/raster work off-thread using immutable snapshots.
- Apply strict per-frame and per-tick budgets. Opening the atlas must not cause
  a large synchronous scan or visible gameplay freeze.
- Invalidate cached atlas chunks after block changes and rebuild them lazily.
- Prefer public Vintage Story APIs. Isolate any unavoidable game-content API
  integration behind a small adapter and document why it is needed.
- For Vintage Story 1.22.6, exact live chunk rendering is isolated in
  `ExactChunkRendererAdapter`. Opaque terrain and stable atlas liquids use
  Primary. Non-fluid transparent geometry uses the engine OIT buffers and must
  be composed without leaving the GUI on a world framebuffer.
- Do not use the stock camera-dependent liquid shader for the atlas. Temporarily
  exclude liquid mesh pools from chunk OIT, then draw those same completed
  meshes with the ModernAtlas stable-liquid shader. Keep hardware depth testing
  so terrain and walls occlude fluids, but do not let camera angle or panning
  change a fluid surface's world position or completeness.
- Vintage Story 1.22.6 requires its registered `SystemRenderOITLayers`
  before/after setup renderers for remaining transparent chunk materials.
  Invoke those setup renderers directly; never trigger the global OIT stage
  because that would also render entities and particles outside atlas scope.
- Compose layered OIT into `Primary` at its native framebuffer resolution and
  only then blit the completed atlas to the window. Direct composition onto the
  window breaks `texelFetch(gl_FragCoord)` when SSAA changes framebuffer size
  and makes fluids slide relative to terrain during camera movement.
- Test with an isolated Vintage Story data directory containing vanilla plus
  ModernAtlas. Do not delete or permanently disable the user's other mods.

## Current verified baseline

- The working public version remains `0.5.9`. Do not change the version number
  unless the project owner explicitly requests it. Package-content changes may
  continue under this version during the current test cycle.
- The verified liquid implementation uses completed liquid chunk meshes and a
  dedicated stable shader. Water and lava must remain anchored to their block
  coordinates when the atlas camera pans, rotates or tilts.
- Read `WaterStillCounter` and `WaterFlowCounter` from Vintage Story's live
  shader uniforms. Do not advance a separate ModernAtlas liquid clock. Preserve
  the water texture's authored alpha and keep lava opaque.
- Do not replace the stable liquid shader with the stock `chunkliquid` shader.
  A test of that approach reproduced camera-relative liquid displacement even
  though its animation and transparency matched the normal world more closely.
- The current confirmed Git baseline is commit `3b2ba29` (`Match native liquid
  timing and transparency`). Treat changes after it as new work that requires
  a fresh build and in-game verification.

## Build and in-game test workflow

- For every rendering change, build `ModernAtlas.csproj` in Release mode,
  create `Releases/modernatlas_0.5.9.zip`, validate the ZIP, and copy that exact
  archive to the active Vintage Story `Mods` directory. Compare SHA-256 hashes
  so the release and active archives are demonstrably identical.
- Close the running game cleanly before replacing or retesting the active mod.
  Launch Vintage Story again with the user's normal data path, wait for the
  world to finish loading, open the atlas with `G`, and inspect the new
  `client-main.log` rather than relying on an older session.
- Check for ModernAtlas shader compilation failures, disposed shaders, OpenGL
  errors, exceptions, and the stable-liquid geometry diagnostic. A successful
  log is necessary but does not prove visual correctness; ask the project owner
  to confirm camera stability, animation speed and transparency on screen.
- Vintage Story's unpack cache is keyed by package contents. A new cache suffix
  after replacing the ZIP proves the new package was read; do not delete world
  saves or the vanilla map database to force an update.
- The user's other mods may remain installed. If one prevents a focused test,
  it may be disabled temporarily only when needed and must never be deleted.
  Record unrelated failures separately; for example, `immersivelight@0.2.5`
  has produced an intermittent server-side `AccessViolationException` during
  test-world startup.

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
