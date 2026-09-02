# ModernAtlas project guide

## Product goal

ModernAtlas is a Vintage Story 1.22.6 mod that should turn the world map into a
readable, Google-Earth-like 3D atlas while remaining an independent product
with its own name and visual identity. Rendering is client-side; the optional
server side owns the authoritative multiplayer policy for living-entity
disclosure and client Settings access.

The target view contains terrain relief, mountains, water, block-built
structures, ruins and trees. It should resemble the approved ModernAtlas
mock-up: a tilted, textured, softly lit map with optional live-looking cloud
cover.

The atlas is a dedicated interactive 3D GUI opened with `G`; it is not a skin
for the vanilla blue 2D map. Its default presentation is an open 3D parchment
scroll containing the live atlas and controls, while Settings offers an
optional full-screen presentation. `G` and `Escape` must both close it.

ModernAtlas work is judged by the atlas view. Do not expand a map task into
changes to ordinary world rendering unless ModernAtlas failed to restore state
that it changed. Native world-camera haze, shadows or horizon color are outside
the product scope when the atlas itself is correct.

## Project language

- Use English only in source code, comments, documentation, filenames, logs,
  configuration, UI labels and release metadata.
- Do not add translated language files unless the project owner explicitly
  changes this rule. Conversation with the project owner may use Polish.

## Rendering scope

- ModernAtlas must never manipulate ordinary world rendering, chunk loading,
  chunk visibility, render distance, world textures, world materials, lighting,
  fog, color, opacity or appearance. Treat all Vintage Story world and client
  data as read-only input obtained through the public API or narrowly isolated
  compatibility adapters. Rendering filters, shader uniforms, texture
  substitutions, camera changes and visual effects may affect only the atlas
  framebuffer while the atlas is being drawn, and every temporarily changed
  engine value must be restored in `finally` before ordinary world rendering
  resumes. Increasing or decreasing the atlas view must never request chunks,
  alter the game's `viewDistance`, delay normal chunk presentation or leave an
  atlas shader path enabled in the world renderer.
- Render world blocks and their actual block shapes and textures.
- Support blocks registered by other mods through the public Vintage Story
  block and texture-atlas APIs. Do not hard-code only vanilla block IDs.
- Render terrain, buildings, ruins, vegetation and fluids when they are made
  from blocks.
- Vintage Story renders loaded windmill sails, axles, gears and other
  mechanical-power devices outside the chunk meshes. Render those instances
  through the narrowly isolated native mechanical renderer only inside the
  atlas framebuffer. Filter its transient device lists to the player-anchored
  disclosure radius and the exterior surface allowance, restore the native
  lists and render state in `finally`, and never invoke the global world stage
  or advance a mechanical network from the atlas pass.
- Preserve the game's live material appearance: texture-atlas coordinates,
  biome tint, directional daylight and connected or multipart block geometry.
  Vegetation silhouettes against the atlas background must use native 3D depth
  testing plus completed surface-column and disclosure guards. Do not require
  an opaque screen pixel behind every leaf: that strips crowns on the skyline,
  leaves isolated trunks and changes the apparent tree shape with camera yaw.
  Do not sample the normal camera's shadow map from the atlas camera; it causes
  severe frame loss and square shadow boundaries.
- Give the atlas its own safe directional celestial lighting. Live mode must
  read the public client calendar's normalized sun or moon direction, light
  color and daylight strength. Fixed-hour mode must evaluate the calendar's
  sun position for the current date and player location, then apply bounded
  dawn, daylight, sunset and moonlit-night colors. Directional face shading is
  allowed; never re-enable the perspective camera's cast-shadow texture to
  make atlas shadows. Never pass a below-horizon sun or moon vector directly
  into native chunk face lighting: preserve its azimuth but clamp the atlas
  light to a shallow positive elevation so completed mesh sections cannot turn
  into large dark patches resembling chunk shadows.
- Keep atlas exposure neutral and stable. Player-local underwater, lava, fog
  sphere, night vision, perception, held-light and world-warp effects must not
  change atlas brightness, tint or haze. Restore every engine uniform after the
  atlas draw so normal gameplay keeps its effects.
- Render water and lava as stable world-aligned block surfaces using their
  registered atlas textures and biome tint. Exclude camera-dependent Fresnel,
  shadows and scene lighting from atlas fluids. Drive texture frames and flow
  from the engine's native water counters, preserve authored water alpha, and
  keep lava opaque. Other transparent block materials may continue through the
  engine OIT path.
- Reuse the game's 3D models and current animation poses for already client-loaded players,
  animals, hostile mobs and NPCs only when allowed by the server policy. Invoke
  only the selected entities' renderers inside the atlas world framebuffer;
  never invoke the global entity render stage or render dropped items,
  particles, labels, damage effects and other unrelated transient objects.
- Hold each living model on the animation pose present when the atlas opens.
  Running, walking and gesture poses may remain visible, but the atlas must not
  advance their skeleton animation while singleplayer is paused.
- Use a neutral stone material when a block or texture cannot be resolved.
  Never intentionally display the missing-texture question-mark material.
- Conceal atlas-only cave cutouts and entrances with a subdued neutral-gray
  occlusion treatment so the empty framebuffer cannot shine through and draw
  attention to hidden underground space. This treatment may cover a clipped
  opening, but must not create world blocks, a terrain shell or persistent
  geometry.
- Clouds are a separate visual overlay. They must not become cached world
  geometry and must not disclose unexplored terrain.

## World-data safety

- Never replace, migrate or write Vintage Story's vanilla map database or save
  database.
- Read only world/chunk data already available to the client. Never request or
  generate unexplored terrain solely for the atlas.
- Existing vanilla exploration, waypoints and maps must remain usable.
- Exact block geometry is available only while chunks are client-loaded. Draw
  those completed meshes directly and leave unavailable terrain absent against
  the opaque atlas background.
- Do not create, load, render or maintain a persistent ModernAtlas terrain
  cache. The former colored-relief cache and its Clear Cache UI were removed
  because stale or incomplete tiles produced black terrain and excessive work.
  Existing old ModernAtlas cache files are inert and must not be deleted
  automatically.
- Do not generate an artificial textured surface, stone shell or boundary wall
  around the atlas radius. Draw only client-loaded world geometry; leave
  unavailable terrain absent against the opaque atlas background rather than
  inventing blocks.
- The hard disclosure boundary must exclude terrain that the client is not
  allowed to know. Its radius is anchored to the player's actual world
  position; panning or rotating the atlas camera must never move or enlarge the
  revealed area.
- Living models must iterate only the client's `LoadedEntities`; never send,
  request, cache or infer hidden entity positions for the atlas. Render them
  into the same depth buffer as terrain before fluids so blocks and the
  disclosure boundary conceal them correctly.

## Boundary and final-compositor guardrails

These rules come from a failed boundary rewrite that left the atlas completely
empty while the automated exact-terrain check still reported success.

- Never let the final compositor fail closed on a newly introduced GPU mask
  before that mask has been visualized in game and its coordinate system,
  channel layout, dimensions and non-zero coverage have been confirmed.
- `supportedTerrainColumns`, `visibleTerrainColumns` and
  `consideredTerrainColumns` are not proof that a surface was rendered.
  `consideredTerrainColumns` also holds the partial vertical mesh columns the
  boundary is meant to reject, and a non-empty CPU set never proves that the
  GPU texture contains readable markers at the reconstructed world coordinates.
- Never feed an already filtered visible-column set into the next frame's mask.
  That feedback repeatedly shrinks the atlas.
- Introduce a completed-column mask and a below-surface compositor discard as
  separate, independently tested changes, so an empty result has exactly one
  diagnosable cause.
- `BoundaryResolvedLastFrame`, a successful draw call and any exact-terrain
  counter are not proof that the resulting color framebuffer contains map
  pixels.
- A failed optional refinement must never disable exact rendering for the whole
  session. Preserve the last known visible atlas output or bypass only the
  refinement that failed.
- Before another boundary rewrite: render the proposed mask as a temporary
  debug overlay in a distinct color and confirm it follows the intended
  loaded-mesh footprint; log mask origin, dimensions, marked-cell count and the
  player/camera chunk coordinates; analyze the resolved framebuffer and require
  a reasonable number of non-background pixels; test one boundary rule at a
  time, completed-column coverage before any vertical surface-envelope rule;
  capture tilted views from both opposite yaw directions, because the original
  defect changes appearance after a 180-degree rotation; keep water, lava,
  leaves and plants on their existing material-specific paths, so the boundary
  may reject pixels but never replaces those materials with opaque blocks; and
  install the result only after a real captured image from that exact package
  shows terrain with no isolated trunks, leaves or vertical chunk walls outside
  the accepted footprint.
- The automated checks validate renderer activity and lifecycle, not the
  semantic content of the final image. A green log is necessary but not
  sufficient: inspect a captured atlas image produced by that exact package. A
  future test should read framebuffer pixels and fail when the atlas is
  entirely or almost entirely background.
- `KNOWN_VISUAL_ISSUES.md` records the open baseline image defects that a green
  log does not cover: vertical stubs at the terrain boundary and the tiled
  screenshot's central dark band plus per-tile brightness steps. Keep it current
  when one of them is fixed or a new image defect is confirmed.

## Camera and controls

- Support zoom, a freely rotatable 360-degree yaw and a useful top-down to
  tilted camera range without clipping the top or bottom of loaded terrain.
- Left-drag pans in screen space: horizontal mouse motion stays horizontal and
  vertical mouse motion stays vertical regardless of camera yaw. Do not map a
  downward drag to an unexpected compass direction.
- Right-drag rotates and tilts the camera. Middle-click resets the view.
- Survival-safe atlas views retain a useful tilt range. In singleplayer
  Creative or explicitly accepted Cheat Mode, allow the pitch to reach a
  horizontal zero-degree floor. Never let the camera rotate below the atlas
  ground plane or invert through negative pitch; clamp or collide at the floor.
- Smoothly interpolate pan, rotation, tilt and wheel zoom using real render
  time so controls continue to animate while singleplayer is paused.
- Opening `G` must not pause singleplayer. World ticks, client chunk streaming
  and completion of GPU chunk meshes continue while the atlas is open, and
  newly completed exact meshes must appear without closing and reopening it.
  Do not reintroduce an atlas-owned `PauseGame(true)` call. If another game UI
  or external state already paused the game, real render time still drives the
  atlas camera and optional render-only animation offsets.
- Keep atlas GUI controls clickable and prevent atlas input from leaking into
  the hotbar, inventories or dialogs underneath it. Forward both `OnKeyDown`
  and `OnKeyPress` to the active atlas composer because editable text inserts
  printable characters during `OnKeyPress`. When the search input has focus,
  it owns printable hotkeys such as `G`; only `Escape` still closes the atlas.
  Opening the atlas must leave search unfocused, clicking outside the text box
  must release its focus, and an always-visible `Exit` button must close the
  atlas without depending on keyboard focus. A compact `Hide UI` action may
  conceal all atlas controls for inspection; the first `Escape` restores that
  UI and a later `Escape` closes the atlas.
- Keep atlas panels translucent with high-contrast white typography so controls
  remain readable without covering more exact terrain than necessary.
- Draw the full-screen atlas after ordinary mod HUD dialogs so class HUDs,
  clocks and other overlays cannot cover the map.

## Atlas inspection, search and layers

- In singleplayer Creative or explicitly accepted Cheat Mode, clicking a
  rendered living model may open an atlas unit frame. Show the loaded entity's
  name or player nickname, current and maximum health, category and other
  useful public details. The panel must not advance animation or reveal an
  entity that the atlas was not already allowed to draw.
- In singleplayer Creative or explicitly accepted Cheat Mode, add an optional
  atlas search UI for loaded blocks and, where disclosure permits, loaded
  players, animals, hostile mobs, NPCs and dropped-item matches. A query such as
  `chicken` should highlight matching visible results with an outline or
  restrained emissive marker that remains readable at long atlas distances.
  Search must be incremental, budgeted and limited to client-loaded data; it
  must never request chunks or infer hidden positions.
- Dropped items remain absent from the ordinary atlas render. A search result
  may represent an already loaded dropped item only in singleplayer Creative
  or accepted Cheat Mode, using a lightweight marker rather than invoking the
  global item/entity render stage. Multiplayer never enables dropped-item
  search without a future explicit server policy.
- Add selectable atlas data layers where live loaded data supports them,
  specifically soil fertility, moisture/rainfall and temperature. Do not expose
  forest density because the exact 3D trees already communicate it, and do not
  expose the unclear geologic-activity value. Ore heatmaps and similarly
  revealing layers are restricted to singleplayer Creative or accepted Cheat
  Mode. Layers must use clearly differentiated colors, preserve visible 3D
  relief, remain reversible and must not overwrite the textured base map or
  persist a terrain cache.
- Expose the useful ambient/developer controls formerly reached through the
  `~` editor from the atlas Settings UI. Clearly label them as developer visual
  controls, scope them to the atlas framebuffer and restore all engine state
  after every atlas draw.
- The current data-layer implementation samples only already loaded map chunks,
  map regions and exact chunk block data into a transient,
  eight-block-resolution GPU color texture. It tints the exact 3D geometry
  instead of drawing a replacement heightfield. If a loaded client map region
  contains no ore-potential maps, the Creative/Cheat ore layer samples vertical
  columns from already loaded chunks and recognizes registered blocks through
  `EnumBlockMaterial.Ore`; it must not request, unpack or generate a chunk.
- `Atlas visual lab` is the supported replacement for relying on the engine's
  `~` ambient editor while `G` owns input. Its exposure and cave-mask
  brightness controls affect only the atlas framebuffer. Keep their defaults
  neutral and their ranges bounded. Overlay opacity belongs to the layer it
  applies to and is exposed in `Map options` next to the layer selector.
- GuiComposer paints static elements first and interactive ones (buttons,
  switches, sliders, dynamic custom draws) afterwards in insertion order. Any
  overlay that must cover controls — a panel header a scrolling body slides
  under, an indicator next to a button — has to be an interactive element
  added after them. A static strip is painted under every control and will
  not cover anything.
- Settings is one scrolling panel of five named sections — `Map & Data`,
  `Presentation & Lighting`, `Entities`, `Safety`, `Advanced` — flowed into
  three columns on a wide panel, two on a medium one and a single column when
  the viewport is narrow. Content height and the scroll range are measured
  from that layout, never from a constant, and every control must be
  reachable by scrolling. Control keys are stable API for the smoke test; do
  not rename them when moving a control between sections. A control that is
  disabled says why: `Creative/Cheat only`, `Disabled by server policy` or
  `Requires volumetric clouds`. `Performance`, `Visual lab` and
  `Creative / Cheat` live in `Advanced`, and returning from one of them keeps
  the Settings scroll position.
- The toolbar reads `Settings`, `Shot`, `Setup`, `Layers`, `Search`, `Hand`,
  `Hide`, `Exit`. `Exit` is always present and enabled. A small code-drawn dot
  on the `Layers` button reports the active layer: neutral grey for textured
  terrain, the layer's own mid-ramp color otherwise. It is drawn inside the
  toolbar's existing width — the button yields the space — so the interface
  never covers more of the map than before. Active, hover and disabled states
  stay visually distinct, and every button keeps its tooltip.
- Layer status lines follow one shape: `Preparing · 63% · loaded data only`
  while sampling, `Ready · 1,842 loaded samples · 8 × 8 grid` when done,
  `Waiting for streamed data` when nothing is loaded in range and
  `Unavailable · textured terrain remains active` after a failure. The ore
  layer replaces the sample count with its sources: regional maps report maps
  and regions, loaded columns report columns and observed blocks, and a mixed
  radius reports both groups separately — finding one OreMap must never hide
  the columns that filled the rest. The active filter appears by readable
  name, with the asset code as secondary detail in the caption.
- Branches that a given test world cannot reach — regional ore potential in a
  world whose loaded regions carry no OreMaps, and the mixed status that needs
  both sources at once — are covered by pure functions with deterministic
  self-checks (`AtlasRegionalOreReadings.Validate`,
  `AtlasOreStatusText.Validate`) invoked from the smoke test. Those never
  replace the in-game path: the smoke log must keep stating when the real
  branch was not exercised.
- The Creative/Cheat ore layer is presented as `Ore analysis`; its stored
  config value and enum stay `ore`/`OreDensity`. Both its panel status and its
  pointer card must name the source they speak for — `Regional potential` from
  loaded regional OreMaps, or `Loaded column` from blocks actually seen in the
  exact loaded column — and must never present one as the other. With no ore
  filter the color is the strongest single reading in that cell, never a sum,
  and the UI has to say so. Ore names are shown in readable form with the
  asset code kept as secondary detail, and the filter carries an `All` entry
  plus an `n/total` position counter.
- `Map options` must state what the active layer means without relying on
  color alone: a legend ramp painted from the same palette the overlay uses,
  named low/middle/high stops, the separate "unavailable" swatch of the ore
  layer, and a caption naming the world-generation source, the eight-block
  grid and the loaded-data-only limit. The pointer read-out works for every
  data layer, reads only samples the layer already holds and must name
  world-generation climate as such so it is never read as current weather.

## View distance and multiplayer rules

- Do not expose a separate atlas radius. Fit and cull the atlas from Vintage
  Story's current `viewDistance` graphics setting because that setting controls
  which exact chunk meshes the client has loaded.
- Rendering performance, view distance and waving vegetation remain controlled
  by Vintage Story's graphics settings.
  Multiplayer must not request distant chunks or disclose activity outside data
  already sent by the server.
- `ModernAtlasServer.json` is authoritative in multiplayer. Its default is
  `LivingEntitiesEnabled: false`, `AllowClientSettings: true` and
  `AllowHideVegetation: true`. Players,
  animals, hostile mobs and NPCs have independent allow flags after the master
  entity switch is enabled. Setting `AllowClientSettings: false` sends an
  explicit `ClientSettingsLocked` policy bit that disables the Settings
  hierarchy and its preset mutations for multiplayer clients without changing
  or deleting their saved local configuration. Once a matching policy channel
  is observed it is latched for the world session, and Settings stay locked
  until its packet arrives; an absent channel, an
  older packet without that optional field, and singleplayer retain Settings
  access for client-only compatibility. A missing server policy channel still
  uses the safe defaults for Cheat Mode and living-entity disclosure, and
  clients must not override those defaults. Client settings may hide all living
  models or any individual allowed category, but may never enable a category
  denied by the multiplayer server. Singleplayer uses those client visibility
  switches without the multiplayer server restriction.
- Starting with ModernAtlas `0.6.9`, `modinfo.json` must declare
  `requiredOnClient: true` and `networkVersion: "0.6.9"`. When the server has
  ModernAtlas installed, Vintage Story's native pre-world mod handshake then
  rejects clients without ModernAtlas or with a different ModernAtlas
  `networkVersion` before they enter the world. Keep `requiredOnServer: false`
  so a client-only ModernAtlas installation remains allowed on servers that do
  not install the mod. `networkVersion` describes network-protocol
  compatibility: change it only when the protocol becomes incompatible, not
  automatically for every product-version bump.
  `AllowHideVegetation: false` keeps registered atlas vegetation visible,
  disables that Performance control with the server-policy reason and prevents
  Low/High presets from changing the saved client preference. Singleplayer,
  absent channels and older packets retain the control for compatibility.
  `PlayerOverrides` holds optional exceptions keyed only by the stable
  `PlayerUID`; player names are display metadata and must never be used as the
  authority key. Every nullable exception has three UI states: `Inherit`,
  `Deny` and `Allow`. `Inherit` resolves from the current server default, and a
  player entry with no remaining exception is removed instead of persisting an
  empty override. Compute a separate effective policy for each recipient on
  join and after every policy edit; never broadcast one player's exception to
  another player.
  `/ma admin` opens the server policy editor only for an in-game caller with
  Vintage Story's `controlserver` privilege. The server must independently
  recheck that privilege and validate every submitted value; displaying the
  client GUI is never proof of authority. The panel edits global defaults and
  persistent online or previously stored player exceptions, writes
  `ModernAtlasServer.json`, and immediately redistributes effective policies
  to all online players without requiring a restart. An unauthorized packet
  must be rejected and logged without changing config or client policy.
  The panel's `Creative / Cheat atlas tools` permission governs only
  ModernAtlas cave mode, loaded-data search, unit inspection, dropped-item
  markers, camera-angle tools and ore analysis. It must never grant or revoke
  Vintage Story Creative mode, change a player's `CurrentGameMode`, or mutate
  the world's `AllowCreativeMode`. Revoking the atlas permission must disable
  any active ModernAtlas Cheat Mode on that client.
  Multiplayer must never restore Cheat Mode from `CheatModeByWorld` or another
  client configuration value. Enabling it requires a received authoritative
  policy with `CheatModeAllowed: true` followed by the server-owned command
  packet. Server denial also cancels every deferred Settings mutation before
  it can update or save the local configuration.

## Performance and compatibility

- ModernAtlas must never write, override, clamp, normalize, cache and restore,
  or otherwise manipulate Vintage Story's global frame-rate and frame-pacing
  controls. This includes the foreground or background FPS limit, VSync,
  engine render-loop cadence, frame timers, sleep/yield behavior, driver caps
  and equivalent settings. Atlas performance profiles may control only
  atlas-owned work and atlas-framebuffer redraws after atlas opening begins;
  they must never throttle or accelerate ordinary world rendering or govern
  work owned by Vintage Story or another mod.
- With the atlas and its opening or closing transition inactive, every
  performance profile, including High, must remain dormant. Do not perform
  periodic atlas screenshots, framebuffer readbacks, blur or texture uploads,
  renderer prewarming, background scans or scheduled atlas rendering. A
  one-time transition capture may begin only after the player requests the
  atlas to open. Changing a ModernAtlas profile while the atlas is closed may
  persist its own configuration, but must not change the game's frame behavior.
  Treat this as a compatibility requirement for clients with any number of
  other mods, and keep an automated check that High queues no atlas capture or
  rendering work before the opening input.
- Apply strict per-frame and per-tick budgets. Opening the atlas must not cause
  a large synchronous scan or visible gameplay freeze.
- Keep the focused Vintage Story atlas on its smooth refresh cadence even when
  no camera button is held. The reduced 12 FPS cadence is reserved for an
  unfocused/background game window, not for an idle foreground atlas.
- Do not register terrain-cache tick listeners or scan chunks for atlas-owned
  fallback data. Rendering should consume the game's already completed meshes.
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
- In scroll presentation, compose the completed opaque atlas at 100 percent
  opacity only inside the scroll's rectangular map viewport. The parchment and
  stationary opaque backdrop must not change with atlas zoom. The backdrop
  conceals world shadows, held items and HUD content behind and around the
  scroll. In the opened atlas viewport, render the physical parchment sheet
  and rollers with alpha one and blending disabled; nominal alpha-one blending
  is not sufficient because a leaked or externally changed blend state can
  reveal the world through the scroll. The separate opening and closing
  transition may still animate its own arm alpha, but its local first-person
  parchment and rollers remain opaque. Render that local transition without
  testing against the normal world's depth buffer, otherwise nearby bowls,
  jugs, walls and their shadows punch silhouettes through the paper. Draw the
  local first-person scroll at the transition dialog's late GUI order using a
  captured perspective projection; drawing it in the Opaque world stage lets
  entities and held objects render over it afterward. Remote third-person
  scrolls continue in the world Opaque stage with world depth. After the local
  late-GUI scroll stops its custom shader, reactivate the engine GUI shader;
  later GUI renderers such as the crosshair assume it is active and otherwise
  throw `Can't set uniform on not active shader gui`. Do not use a full-window
  translucent layer, framebuffer alpha leakage or geometry clipping as a
  substitute for the final viewport clip.
- Never call a global shader reload when the atlas closes, when `G` and
  `Escape` are alternated, or during world leave. A global reload previously
  caused a red window border, changed normal-world rendering and produced a
  severe closing hitch. Disable atlas-only shader paths through their managed
  state and per-draw uniforms, restore engine values in `finally`, and leave
  ordinary world shaders compiled and active.
- Test with an isolated Vintage Story data directory containing vanilla plus
  ModernAtlas. Do not delete or permanently disable the user's other mods.
- World-leave and client shutdown are required lifecycle tests. Dispose dialogs,
  textures, shaders, callbacks and Harmony patches idempotently; leaving a
  world after opening or closing the atlas must never crash the client.

## Current verified baseline

- The current working version is `0.6.9`, matching `modinfo.json` and
  `Releases/modernatlas_0.6.9.zip`. Do not change the version number unless the
  project owner explicitly requests it. Package-content changes may continue
  under this version during the current test cycle.
- The current server package includes `/ma admin`, a ModernAtlas-styled
  operator dialog for Server defaults and UID-keyed player exceptions. Its
  controls cover Settings access, Hide vegetation, Creative/Cheat atlas tools
  and the four living-entity categories. Updates are persisted server-side and
  effective policy packets are refreshed immediately. The command is
  player-only and uses `controlserver`; console invocation correctly stops at
  the player requirement. Pure policy validation covers target isolation and
  inheritance, and the admin state protobuf round trip is verified separately.
- The verified liquid implementation uses completed liquid chunk meshes and a
  dedicated stable shader. Water and lava must remain anchored to their block
  coordinates when the atlas camera pans, rotates or tilts.
- Read `WaterStillCounter` and `WaterFlowCounter` from Vintage Story's live
  shader uniforms. While singleplayer is paused by the atlas, add a render-only
  real-time offset to liquid and wind-wave shader uniforms so atlas liquids and
  vegetation keep moving without changing the game's counters or simulation.
  The atlas animation switch may instead hold those liquid and wind uniforms
  on one captured frame, including in multiplayer, without pausing the server.
  Honor Vintage Story's waving-vegetation graphics setting, preserve the water
  texture's authored alpha and keep lava opaque.
- Do not replace the stable liquid shader with the stock `chunkliquid` shader.
  A test of that approach reproduced camera-relative liquid displacement even
  though its animation and transparency matched the normal world more closely.
- Water exposure follows the selected live or fixed sun state so water does
  not remain bright blue against dark terrain at night. Lava stays emissive.
- Live atlas lighting uses `IClientGameCalendar` rather than inferring the sun
  from the normal camera. Fixed-hour lighting uses `IGameCalendar.GetSunPosition`
  for the current date and player location, preserving seasonal and latitude
  changes while keeping atlas exposure bounded and independent of player-local
  vision effects.
- The atlas disables `DropShadowIntensity` only for its own draw. Reusing the
  game's perspective-camera shadow map caused square shadows and roughly one
  frame per second. The engine value is restored after atlas draw.
- The atlas uses smooth target-camera interpolation and draws at GUI order
  `0.98` so ordinary third-party HUDs remain behind the scroll or full-screen
  map presentation.
- Commits `37af5c1`, `3d132d4` and `7bc6876` are the scroll-viewport clipping,
  shader-reload removal and live chunk-streaming safety checkpoints. Preserve
  their invariants when changing GUI composition, engine uniforms, closing
  transitions, view-distance fitting or atlas lighting.
- Persistent ModernAtlas terrain cache code, shaders and Settings controls are
  removed. The atlas renders only current exact chunk meshes.
- Cave openings use a thin neutral-gray atlas-only band of real geometry,
  while deeper cave faces are discarded so their tunnels cannot appear as a
  visible underground network. A one-block exterior envelope preserves cliff
  sides, building walls and floor faces near the three-block safety allowance.
- Singleplayer Creative and accepted Cheat Mode permit a zero-degree pitch
  floor, optional cave and loaded-map search modes, unit inspection,
  dropped-item search, camera-angle locking and ore-density analysis. The
  camera never crosses into negative pitch. These controls remain absent in
  safe Survival and multiplayer modes.
- Atlas living models never render held items. Temporarily suppress both hand
  slots and the renderer's held-item path before `BeforeRender`, then restore
  every entity and renderer field in `finally` so camera-relative shields,
  tools and modded accessories cannot float over the atlas or alter gameplay.
- Loaded-data search resolves registered block asset codes plus English and
  active-game-language aliases incrementally, then scans only already loaded
  chunks under a strict per-frame budget. Search aliases use private isolated
  translation services and never mutate Vintage Story's global translation
  diagnostics. Entity results come only from models already authorized and
  rendered by the atlas. During world startup, do not call translated
  `GetHeldItemName`, `ItemStack.GetName` or `Entity.GetName` from atlas search
  or unit inspection: the survival handbook can concurrently mutate Vintage
  Story's non-thread-safe translation diagnostics. Use the isolated search
  services, stable asset codes, explicit custom names and player nicknames.
- Opening the atlas with `G` now plays a short real-time first-person scene:
  the camera remains fixed while the left hand retrieves a procedural pocket
  scroll from below-left, the right hand takes the other end and both hands
  unroll it before the existing light enters the atlas. Closing the atlas rolls
  and stows the scroll in reverse. Creative skips both transitions. The local
  opening hotkey first cancels attack, block breaking, eating and other held
  hand use through the public hand-action API, clears both in-world mouse
  controls and removes any remaining local first- or third-person action clip.
  Maintain that suppression throughout the opening scene and open atlas, and
  keep it latched after close until the physical mouse buttons are released.
  The atlas entity renderer also removes a late queued local hand-action clip
  immediately before pose preparation. A cancelled action is not restored
  when the atlas closes; a new mouse press is required. Only the two
  scroll-owned arms can appear in first person.
  Remote players' third-person scroll uses the same phase timings and follows
  the animated `LeftHand` and `RightHand` attachment points. Every captured
  camera and held-item field is restored on cancellation, completion and world
  leave without reviving the cancelled hand action.
- The lower-right handheld instrument is selectable as Off, Compass or Time.
  Compass and Time share one flat wooden shell and one synchronized toss
  transform so the casing cannot separate into overlapping or ghosted copies.
  Compass keeps a restrained red-and-blue needle. Time uses a fixed wooden
  gnomon and a narrow moving shadow driven by the live world calendar rather
  than atlas fixed-hour lighting; the shadow is absent outside 06:00-18:00.
- Survival-safe atlas rendering replaces all registered
  `EnumBlockMaterial.Ore` composite textures with their baked host-rock base
  textures through cycle-safe, incrementally built transient GPU lookups.
  Creative/Cheat keeps the authored ore textures, and neither mode mutates the
  world block atlas, save data or a persistent cache.
- The 0.6.1 test-cycle feature checkpoints include `7fe0f01` (surface safety,
  lifecycle smoke test and Creative camera), `6640a5d` (unit inspection) and
  `5bba376` (loaded-data search). Commit `669c4bb` adds loaded-data analysis
  layers, the atlas visual lab, translation-safe search labels, streaming-race
  guards and shader-safe world teardown. Its full automated atlas and
  world-exit regression passed with the normal full mod set.
- World teardown starts after Vintage Story replaces `DefaultShaderUniforms`.
  `ExactChunkRendererAdapter.Dispose` must never activate an engine shader or
  upload uniforms at that point; doing so can pass a null `colorMapRects[40]`
  array to `glUniform4fv` and crash Mesa. Restore managed shader source and
  Harmony state only. Atlas filter switches are cleared in each render
  `finally` block while the renderer is still valid.
- The screenshot feature is a tiled camera-grid capture, not a styled
  re-render. `AtlasTiledScreenshot` divides the current view into an
  `ScreenshotScale` grid (1x-8x, every integer step), re-renders the exact
  world for every tile with a zoomed and offset camera, reads the Primary
  sub-region per tile and stitches one seamless top-down PNG on a background
  thread. The stitched output is bounded by a pixel budget (about 100
  megapixels) so extreme grids cannot exhaust memory, every tile is
  downsampled to that budget before it is stored, and the PNG is written
  band by band through `AtlasPngStreamWriter` so the whole RGB image is
  never buffered at once. A
  `CANCEL / CLOSE` progress modal owns input while the capture, stitch and
  save run; Escape cancels the capture and restores the camera. The camera
  baseline (zoom, X/Y/Z centers) is snapshotted, snapped per tile and
  restored after the last tile. Tile offsets are computed in screen space:
  the tilted LookAt camera needs a ground-forward part (sin pitch) plus a
  world-Y lift (cos pitch) per vertical grid step, and exactly one integer
  tile of Primary pixels per step. Every shared edge has a small overlap
  margin that the stitcher linearly crossfades, hiding the sub-pixel drift
  between camera positions. Wind, water and cloud offsets are frozen for all
  tiles (`screenshotFrozen*`, `GetLiveCloudOffset`) so animated surfaces
  cannot tear at seams. Native mechanical devices are snapshotted before
  tile 0 as well: every tile must read the same captured `AngleRad` through
  an atlas-render-call-only override that is cleared in `finally`, without
  pausing or mutating the mechanical network. Devices loaded after tile 0 are
  excluded from that capture instead of appearing halfway across the PNG.
  The 0-degree Creative pitch capture is covered.
  The smoke test queues a 2x capture only after every other exercise
  (including the presentation debounce test) has passed, returns the
  presentation to scroll first without re-requesting the debounce every
  frame, and verifies the saved PNG plus the restored camera. Tile row 0 is
  the TOP of the stitched image (the `[1][2] / [3][4]` grid order): the
  vertical eye offsets must follow the working drag sign convention
  (`imageDown = (-forwardPart*sinPitch + upPart*cosPitch) / worldPerPixel`),
  so the top tiles capture the content above the view center. The stitcher
  keeps the outer overlap margins in the image, so the composite covers the
  full live-view world area and text or structures at the viewport edges are
  not cropped. Tiled screenshots preserve the same disclosed living models
  as the interactive atlas, including the local player's own 3D model. The
  closing transition's pocket sound fires at
  `ClosingDurationSeconds - 0.2f`, not 1.78f, because the closing transition
  lasts only 1.5 seconds.

## Source layout and code rules

- `ModernAtlasDialog` and `ExactChunkRendererAdapter` are split into `partial`
  files by concern. `ModernAtlasDialog.cs` keeps the fields, constructor,
  lifecycle, input handling and `OnRenderGUI`, with `.SmokeTest`, `.Interface`,
  `.Screenshot`, `.ScreenshotCapture`, `.Layers`, `.Camera`, `.Settings`,
  `.Inspection` and `.DamageWarning` beside it. `ExactChunkRendererAdapter.cs` keeps creation,
  `Render` and the reflection helpers, with `.Shaders`, `.Visibility`,
  `.Liquids` and `.Lighting` beside it. The split is a pure move: reassembling
  the partial files reproduces the previous single-file sources exactly, and
  the analyzer diagnostics are identical before and after.
- Keep every field initializer in the class' main file. The relative order of
  field initializers declared in different partial files is not guaranteed by
  the language, so moving one into a partial can silently change initialization
  order.
- `ExactChunkRendererAdapter.Shaders.cs` carries the injected GLSL in raw string
  literals whose content depends on the indentation of the closing `"""`. Never
  let an editor reformat that file; a reindent silently changes the shader and
  can produce an empty atlas while the log stays clean.
- Guard optional GPU resources with `is not { TextureId: > 0 }`. The lifted
  comparison `texture?.TextureId <= 0` evaluates to `false` for `null`, so a
  missing texture passes a guard that was meant to reject it.

## Atlas damage warning

- A detected health drop while the atlas is open draws a dark red edge vignette
  plus the caption `TAKING DAMAGE — CLOSE THE ATLAS` over the atlas framebuffer.
  Every health-loss source counts, including fire, falling and hunger; the
  warning never identifies or points toward an attacker. The pulse lasts about
  3.2 seconds, is driven by real render time so it animates while singleplayer
  is paused, and a further hit restarts and slightly strengthens it.
  The first frame of a damage signal is intentionally quiet: the vignette and
  caption share one zero-starting smooth attack, a slow breathing pulse and a
  smooth release to zero. The four edge quads use reusable tiny gradient
  textures, a small animated width (zero pixels at each breath trough), and a
  fully transparent centre; never rebuild or upload a viewport-sized texture
  during the pulse. The caption texture contains only text plus a subtle
  transparent shadow, never a raised panel or background strip. Neither layer
  may use a fixed alpha floor that makes the warning appear instantly. A repeat
  hit carries the current visible alpha into its restarted and strengthened
  episode instead of blinking down.
- It is a GUI overlay only. No shader, no world rendering and no engine state is
  involved, it adds no composer element, and it never captures mouse or keyboard
  input: `G`, `Escape` and `Exit` keep closing the atlas immediately. Do not turn
  it into a full-screen red layer; that would hide the map and resemble the old
  red-border defect.
- The normal warning and its optional damage-triggered emergency close are
  decided by the local player's actual game mode only. Survival, Survival with
  accepted Cheat Mode, and multiplayer all keep the warning; real Creative
  suppresses both. A separate confirmed death guard closes the atlas in every
  mode when the local player is no longer alive or observed `currenthealth <= 0`,
  regardless of `CloseAtlasOnDamage`; it is still deferred to the next safe
  frame before an atlas framebuffer is bound. Never gate this on
  `CreativeCheatSettingsAvailable` or `UnitInspectionEnabled`: Cheat Mode is a
  protected feature switch and must not disable a safety warning.
- `CloseAtlasOnDamage` is one persisted preference defaulting to true. Its
  effective value is `actual game mode != Creative && config.CloseAtlasOnDamage`,
  and the switch stays visible in every mode so the stored value still applies
  after leaving Creative.
- The emergency close skips the scroll stowing transition and the compass stow so
  the player regains control in the same frame. A running tiled capture or an
  open screenshot preview is cancelled and the camera restored first; a capture
  must never block the safety close.
- The warning is never drawn while a tiled capture or the screenshot preview owns
  the frame, so it cannot be baked into a saved PNG or the BEFORE/AFTER preview.
  The opt-in `damage-warning` smoke UI screenshot is an intentional exception:
  it seeks a visible inhale sample and captures the GUI warning for visual
  inspection without using the tiled-capture path.
- The standard warning animation subtest drives its warning through a direct
  signal and never hurts the player; its surrounding smoke fixture is allowed
  to prepare only the named disposable Creative copy at authoritative 100/100.
  It changes `CloseAtlasOnDamage` in memory only and verifies the emergency
  close between frames so the atlas dialog is never closed from inside its own
  render pass.
- The only supported real-damage variant requires
  `MODERNATLAS_SMOKE_REAL_DAMAGE=1`, an operator-provided disposable copied
  data directory, and the exact world name `MODERNATLAS_REAL_DAMAGE_TEST`. The
  two allowed fixture copies are `MODERNATLAS_CREATIVE_TEST` for the standard
  Creative/god-mode run and `MODERNATLAS_REAL_DAMAGE_TEST` for the Survival
  run; no other world may receive fixture mutations. On the Survival copy only,
  the server prepares authoritative 100/100 health, applies one bounded
  nonlethal `ReceiveDamage` request (100 -> 99), observes the replicated health
  drop through the production warning watcher, captures the intentional warning
  screenshot and restores 100 authoritatively. It must log
  `AUTOMATED SMOKE HEALTH FIXTURE READY`, `REAL DAMAGE APPLIED`,
  `REAL DAMAGE OBSERVED` and `REAL DAMAGE HEALTH RESTORED`, and must never
  install the standard smoke god-mode patch. A timeout or teardown also attempts
  restoration. `scripts/run-smoke-test.sh` remains canonical: without the flag
  it performs the Creative first flow and requires god-mode plus fixture
  markers; with the flag it performs the Survival second flow and requires the
  real-damage markers plus the `damage-warning-real` screenshot. The separate
  `scripts/run-real-damage-smoke-test.sh` wrapper supplies the same strict
  disposable-world guard for operators who prefer an explicit command. The
  fixture guard derives that exact name from the process `-o` launch argument;
  Vintage Story's `SavegameIdentifier` is a globally unique UUID, not the save
  filename, and is retained only for world-session identity and per-world
  configuration keys. Server-side `WorldData.CurrentGameMode` changes call
  the public `IServerPlayer.BroadcastPlayerData(false)` API so the client can
  observe the authoritative mode during the bounded readiness wait.
- Creative is correctly suppressed for the real warning, while the in-memory
  Survival policy override still exercises warning behavior and accepted Cheat
  Mode without changing production mode resolution.

## Build and in-game test workflow

- For every rendering change, build `ModernAtlas.csproj` in Release mode,
  create `Releases/modernatlas_0.6.9.zip`, validate the ZIP, and copy that exact
  archive to the active Vintage Story `Mods` directory. Compare SHA-256 hashes
  so the release and active archives are demonstrably identical.
- Close the running game cleanly before replacing or retesting the active mod.
  Launch Vintage Story again with the user's normal data path, wait for the
  world to finish loading, open the atlas with `G`, and inspect the new
  `client-main.log` rather than relying on an older session.
- Maintain an opt-in automated atlas smoke-test path that can launch the client,
  join a named test world, open and close the atlas through ModernAtlas code,
  capture diagnostics and exit cleanly without X11/Wayland key injection. It
  must be disabled during normal play, must never generate terrain or bypass
  multiplayer disclosure policy, and may mutate health/mode only in the two
  explicitly disposable fixture copies `MODERNATLAS_CREATIVE_TEST` and
  `MODERNATLAS_REAL_DAMAGE_TEST`.
- Run the automated game-control test with a disposable singleplayer data
  directory and the exact standard fixture world, for example:

  ```sh
  env MODERNATLAS_SMOKE_TEST=1 /opt/vintagestory/Vintagestory \
    --dataPath /path/to/disposable/VintagestoryData -o 'MODERNATLAS_CREATIVE_TEST'
  ```

  The default smoke target must be the existing standard-generated disposable
  `MODERNATLAS_CREATIVE_TEST` world, never the owner's source world and never
  the superflat `TESTCREATIVE` fixture. `bash scripts/run-smoke-test.sh` is the
  canonical runner: it defaults to `MODERNATLAS_CREATIVE_TEST`, records the
  pre-launch log and crash-log timestamps, requires exit code zero, checks
  every required log marker, verifies the captured screenshots and runs the
  ordinary-world red-border check. That check tests for the defect's shape —
  a continuous red run along the outer 8 px of at least three sides, measured
  against a control band 16-24 px into the image — because a red pixel count
  cannot separate the defect from ordinary scene content such as wood or dry
  grass touching an edge. `scripts/check-red-border.sh` holds the detector and
  still prints the historical pixel totals as diagnostics;
  `scripts/test-red-border-detector.sh` validates it against the committed
  frames in `tests/fixtures/red-border/` (lossless PNG, expectation "no
  border"), synthetic 1/2/8 px frames generated at run time, and red objects
  touching one or two sides. Every PNG in `tests/fixtures/red-border-defect/`
  is a required failing case, so a captured real defect frame belongs there.
  Test data never enters the mod ZIP: `scripts/package.sh` packs only
  `modinfo.json`, `LICENSE`, `NOTICE.md`, the built DLL and `assets/`. It accepts `MODERNATLAS_SMOKE_WORLD`,
  `MODERNATLAS_SMOKE_DATA_PATH` and `VINTAGE_STORY_PATH` overrides, and needs
  `rg` plus ImageMagick (`magick`) when screenshots are requested.

  Add `MODERNATLAS_SMOKE_SCREENSHOT=/tmp/modernatlas-smoke` to capture the
  Survival-safe surface before Cave Mode is enabled, followed by the base
  atlas, Settings, Creative/Cheat and visual-lab frames for UI inspection.
  Add `MODERNATLAS_SMOKE_FIXED_SUN_HOUR=6`, `12`, `18` or `0` together with
  the screenshot variable to inspect fixed dawn, noon, sunset and moonlit
  night without persisting the temporary lighting selection.

  The client also honors `MODERNATLAS_SMOKE_SCREENSHOT_MASK`,
  `MODERNATLAS_SMOKE_SCREENSHOT_SCALE`, `MODERNATLAS_SMOKE_CAPTURE_AREA`,
  `MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE`,
  `MODERNATLAS_SMOKE_SCREENSHOT_INTENSITY_ZERO`,
  `MODERNATLAS_SMOKE_SCREENSHOT_CANCEL`, `MODERNATLAS_SMOKE_DISABLE_CLOUDS`,
  `MODERNATLAS_SMOKE_EXPECT_VIEW_DISTANCE`, `MODERNATLAS_SMOKE_YAW` and
  `MODERNATLAS_SMOKE_PITCH`. Every one of them stays out of normal play.

  The `-o` argument performs the world join. `OnLevelFinalize` waits for that
  world to be ready, then `BeginAutomatedSmokeTest` and `TryOpen` open the same
  atlas dialog normally toggled by `G`. Do not use `xdotool`, `sendkey`, fake
  mouse input or other synthetic desktop input for this test; those methods
  are unreliable and may control the wrong window. Reserve real `G`, `Escape`
  and mouse controls for the owner's final visual inspection.
- The smoke test must exercise exact terrain, stable liquids, the zero-degree
  Creative pitch, the map-layer switch, Creative/Cheat cave, search and
  camera-angle controls, hidden UI restored by `Escape`, an initially unfocused
  search box, focused text entry (including typing `G` without closing the
  atlas), focus release, English plus active-game-language aliases, unit
  inspection, entity and block search, a climate layer, the Creative/Cheat ore
  layer, atlas closure and the normal soft-exit path.
  Route button and switch press/release events through the dialog's own
  composers, then use the dialog's control methods for pitch, entity selection,
  search queries and map layers. Schedule the final
  `ClientPlatform.WindowExit(..., SoftExit)`
  request through `ScreenManager.EnqueueCallBack` so it runs between frames;
  invoking `ExitOrRedirect` from inside the atlas render or pre-setting
  `exitToMainMenu` can invalidate or bypass game-session teardown.
- Record the pre-launch modification time of `client-crash.log`, wait for the
  game process to exit and require exit code zero. A standard run's fresh
  `client-main.log` must contain `AUTOMATED SMOKE GOD MODE ENABLED`,
  `AUTOMATED SMOKE GOD MODE RESTORED`, `AUTOMATED ATLAS CHECKS PASSED` once per
  atlas cycle, `AUTOMATED ATLAS TWO-CYCLE CHECK PASSED`; a real-damage run must
  contain `REAL DAMAGE SMOKE START`, `REAL DAMAGE SMOKE WORLD GUARD`,
  `REAL DAMAGE APPLIED`, `REAL DAMAGE OBSERVED` and `REAL DAMAGE HEALTH
  RESTORED` instead and must contain no god-mode marker. Both modes also require
  `AUTOMATED ATLAS TWO-CYCLE CHECK PASSED`,
  `Automated resolved-atlas alpha validation passed`,
  `Atlas close state check passed`, `World leave received`,
  `Released world-specific atlas rendering resources` and
  `AUTOMATED WORLD-EXIT CHECK PASSED`; a screenshot run additionally requires
  `Automated screenshot filter preview passed`.
  `scripts/run-smoke-test.sh` enforces exactly this list. It must not contain a new critical
  error, ModernAtlas exception, shader failure, disposed-shader report or
  ModernAtlas-attributable OpenGL error, and the test must not create a newer
  crash log or core dump. Record failures from unrelated mods separately.
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
- `ChunkLOD` owns its separate distant-terrain renderer and `ShadowGen`
  process; neither belongs to ModernAtlas. ModernAtlas must not invoke
  ChunkLOD's global render stage, alter its configuration, or treat its custom
  LOD meshes as exact Vintage Story chunk meshes. The current full mod set can
  emit `OpenGL InvalidOperation` before the atlas opens; do not attribute that
  error to ModernAtlas without an isolated test. ChunkLOD can overload its
  streaming queue when configured with an extreme full-LOD range.
- Test both the safe server defaults and a policy with living models enabled.
  Confirm the generated server JSON, received policy log, per-category filter,
  `AllowClientSettings` allow/deny behavior (including `/ma low`/`high`, `.ma`
  commands and incoming preset packets), temporary connected-channel lock,
  fallback behavior when the server has no policy channel, and compatibility
  with an older packet that has no Settings field.
- Test `/ma admin` with both an unauthorized player and a caller holding
  `controlserver`. Exercise Server defaults plus a named player's `Inherit`,
  `Deny` and `Allow` states; verify the UID-keyed JSON, immediate target-only
  policy refresh, reconnect persistence, removal of an all-Inherit entry and
  active Cheat Mode revocation. Confirm that no selection changes the player's
  Vintage Story game mode or the world's `AllowCreativeMode`.
- `scripts/run-admin-policy-smoke-test.sh` is the separate opt-in dedicated
  server/client smoke for that administrator workflow. It uses
  `MODERNATLAS_ADMIN_POLICY_SMOKE_TEST=1`, requires distinct disposable data
  paths plus a persisted admin/root fixture player, and exercises Target, all
  seven policy selectors, `Apply policy`, `Inherit all`, `Close`, reopening,
  JSON persistence, target isolation and immediate effective-policy refresh.
  Keep it independent from `MODERNATLAS_SMOKE_TEST` and
  `scripts/run-smoke-test.sh`: run it when server policy, `/ma admin`, its
  network messages or admin GUI changes, not after every ordinary renderer
  edit. It must restore the fixture's effective defaults and remove its player
  exception before reporting success.

## Legal and repository rules

- Do not include Google names, logos, map tiles, imagery or other Google
  branding in the mod or release assets.
- Do not redistribute Vintage Story game assets or source files. Runtime use of
  installed texture atlases through the official API is allowed.
- Keep original ModernAtlas code under the repository's MIT license.
- Use `apply_patch` for hand-written source changes, run a Release build, test
  the release archive and keep Git commits focused.
- Prefix every Git commit subject with an appropriate Conventional Commits
  type such as `fix:`, `feat:`, `docs:`, `refactor:`, `test:` or `build:`.

## Delivery phases

1. Preserve the vanilla map and add terrain relief (prototype complete).
2. Render a tilted, navigable 3D block atlas from client-loaded exact meshes
   with mod texture support (current baseline).
3. Keep unavailable terrain absent behind an opaque background without
   persistent fallback tiles.
4. Fix cave-opening concealment, Creative/Cheat camera pitch and world-leave
   lifecycle stability.
5. Add atlas unit inspection, loaded-data search and safe result highlighting.
6. Add selectable climate, soil and Cheat/Creative ore-analysis layers.
7. Add atlas-scoped developer visual controls and automated smoke testing.
8. Continue optional animated clouds and visual polish matching the mock-up.
