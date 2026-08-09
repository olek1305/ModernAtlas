# ModernAtlas

ModernAtlas is a 3D map enhancement for Vintage Story 1.22.6. Its public name
is deliberately independent from Google trademarks. The renderer remains
client-side; an optional server component supplies fog and living-entity policy.

## Current 0.6.1 prototype

- keeps the vanilla map, discovered areas and waypoints intact;
- opens a separate full-screen 3D atlas with `G` (rebindable in Controls);
- pauses the game while the atlas is open in singleplayer and never attempts
  to pause a multiplayer server;
- provides an `Atlas animations` switch: enabled keeps atlas liquids and
  graphics-enabled waving vegetation and live clouds moving during a
  singleplayer pause with a render-only clock, while disabled freezes the
  atlas animation phase; world simulation and native game counters are untouched;
- provides a separate `Live clouds` switch and reuses Vintage Story's current
  volumetric cloud map, weather phase, world coordinates and game time over the
  atlas depth buffer as a stable 64-block 3D layer with soft cloud-only
  shadows; it respects the game's own cloud-quality setting;
- renders the game's completed terrain chunk meshes directly with the official
  world shaders on Vintage Story 1.22.6;
- renders water and lava as stable, world-aligned block surfaces without
  camera-dependent lighting or shadows, while preserving native animation
  timing and authored water transparency; other transparent chunk materials
  continue through the engine's OIT pass;
- supports left-drag panning, right-drag 360-degree rotation and tilt, mouse
  wheel zoom, keyboard navigation and middle-click reset;
- uses the same runtime texture atlases, connected chunk geometry, biome color
  maps, lighting uniforms and sun state as the live world renderer, including
  terrain supplied by other mods;
- contains no generated per-block material fallback; the dedicated liquid
  shader samples the game's registered runtime block atlas;
- does not generate unexplored chunks and does not write to a save file;
- fits the atlas to Vintage Story's current view-distance setting and never
  pretends that a second radius control can load more or fewer exact chunks;
- provides an optional unexplored-area fog mask (`M`) and fog-off defaults on
  `Home`; its reduced-resolution texture avoids a full-screen Cairo upload;
- suppresses the normal camera-distance haze while rendering the atlas, then
  restores the world's fog values before returning to the game;
- removes player-local underwater, lava, fog-sphere, night-vision, perception
  and held-light effects from the atlas pass, giving it stable neutral exposure;
- disables player-camera shadow maps during the atlas pass and uses a fixed
  neutral light direction, preventing dusk from drawing square chunk shadows;
- leaves rendering quality, view distance and vegetation animation controlled
  by Vintage Story's graphics options;
- anchors the atlas camera to the live rain-height surface and keeps a
  20-degree-or-higher tilt range so an underground player does not open a cave
  cutaway instead of the exterior; singleplayer Creative and accepted Cheat
  Mode may tilt to exactly zero degrees but never below the ground plane;
- draws only the game's exact loaded chunk geometry and does not generate an
  artificial textured surface or wall around the atlas radius;
- covers clipped cave openings with a quiet neutral-gray atlas material while
  retaining real cliff sides, building walls and floor faces near the surface;
- provides Creative/Cheat unit frames for clicked, already rendered living
  models, including name, category, health and loaded public details;
- searches already loaded blocks and permitted entities incrementally, with
  visible atlas markers and no distant chunk requests; Creative/Cheat may also
  search already loaded dropped items without rendering the global item stage;
- offers strongly differentiated fertility, moisture and temperature
  overlays, plus a Creative/Cheat ore-density layer, by colorizing only the
  exact 3D geometry already present in the atlas while retaining its relief;
  ore density uses loaded regional data when available and otherwise samples
  registered ore blocks from already loaded chunk columns;
- includes an atlas-only visual lab under Settings for exposure, layer opacity,
  boundary softness, cave-mask brightness and neutral fog palettes;
- forces the unexplored-area mask in multiplayer while using only exact chunk
  data that the server has already sent to the client;
- can render the game's live animated 3D models for already client-loaded
  players, animals, hostile mobs and NPCs only when the server enables them;
- keeps multiplayer living models off and fog on when the server has no
  ModernAtlas policy channel, so a vanilla multiplayer server remains safe.

Dropped items, particles, labels and unrelated transient objects are never
drawn by the ordinary atlas render. Creative/Cheat search may inspect already
loaded dropped-item entities and represent matches with lightweight markers.
Optional living models iterate only `LoadedEntities`, so the atlas
does not request or receive hidden entity positions. Models use the same world
depth buffer as terrain and fluids, so walls and fog conceal them. In paused
singleplayer they hold the pose captured when the atlas opens instead of
continuing run, walk or gesture animation. The renderer is an
isolated 1.22.6 integration. If it is unavailable, ModernAtlas reports the
failure instead of displaying substitute block models or invented materials.
There is no persistent ModernAtlas terrain cache; unavailable exact terrain is
concealed by the player-anchored fog boundary.

The atlas settings include a `Living entities` master switch and separate
`Players`, `Animals`, `Hostile mobs`, and `NPCs` switches. These are normal
visibility preferences in singleplayer. In multiplayer they may hide a server-
allowed category but cannot enable one that the server has disabled.

The existing vanilla map database remains in
`VintagestoryData/Maps/<world-id>.db`. ModernAtlas does not rename, replace,
convert, purge or delete that database. Older explored areas stay visible via
the separate vanilla map. ModernAtlas 3D reads only block columns currently
available to the client and keeps its rendering independent from that database.

## Server policy

When ModernAtlas is installed on a server it creates
`VintagestoryData/ModConfig/ModernAtlasServer.json`. Defaults are deliberately
safe: fog is enabled and all living models are disabled by the master switch.

```json
{
  "FogEnabled": true,
  "LivingEntitiesEnabled": false,
  "ShowPlayers": true,
  "ShowAnimals": true,
  "ShowMobs": true,
  "ShowNpcs": true
}
```

Set `LivingEntitiesEnabled` to `true` to opt in. Category flags are then applied
independently; for example, `ShowNpcs: false` hides only NPCs while the other
enabled categories remain visible. Restart the server after editing the file.
Clients cannot override this policy. A server without ModernAtlas is treated as
fog on and every entity category off.

## Run on Linux

This client map extension is distributed as a DLL. A project-local .NET 10 SDK
is currently available at `~/.local/share/modernatlas-dotnet`; `run-client.sh`
detects it automatically. Build, package and launch with:

```bash
cd /home/arcylisz/ModernAtlas
chmod +x scripts/*.sh
./scripts/run-client.sh
```

To open a particular existing world directly:

```bash
./scripts/run-client.sh -o "awesome kingdom story"
```

The script adds `Releases/` as an additional mod path. It does not copy or
overwrite anything in the normal Mods directory. A DLL is used because the
1.22.6 in-game source compiler does not reliably reference
the collections and client rendering APIs used by the 3D scene builder.

## Build a DLL on Linux

For IDE completion and a distributable compiled DLL, install the .NET 10 SDK.
On Arch Linux, the system-wide alternative is `sudo pacman -S
dotnet-sdk-10.0`. Then run:

```bash
dotnet --list-sdks
dotnet build -c Release -p:VintageStoryPath=/opt/vintagestory
```

The release ZIP contains only ModernAtlas files and its compiled DLL. It does
not bundle any Vintage Story binaries or assets.

## Planned architecture

1. Continue rendering exact client-loaded block meshes without a terrain cache.
2. Refine the player-anchored fog transition and loaded-data analysis layers.
3. Add atlas quality and compatibility polish within strict frame budgets.
4. Continue optional cloud and presentation polish matching the concept image.

See `DESIGN.md` for the renderer and compatibility design. `AGENTS.md` records
the product goal and safety rules for future Codex sessions.

## Public distribution

Source code is MIT-licensed. See `NOTICE.md` for trademark and attribution
details. The upload description on the Vintage Story Mod DB must be in English
and clearly explain the mod's function.
