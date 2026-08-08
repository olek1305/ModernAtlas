# ModernAtlas

ModernAtlas is a client-side map enhancement for Vintage Story 1.22.6. Its
public name is deliberately independent from Google trademarks.

## Current 0.5.2 prototype

- keeps the vanilla map, discovered areas and waypoints intact;
- opens a separate full-screen 3D atlas with `G` (rebindable in Controls);
- renders the game's completed terrain chunk meshes directly with the official
  world shaders on Vintage Story 1.22.6;
- renders water, lava and transparent chunk materials through the engine's OIT
  framebuffer and composition pass;
- supports left-drag panning, right-drag 360-degree rotation and tilt, mouse
  wheel zoom, keyboard navigation and middle-click reset;
- uses the same runtime texture atlases, connected chunk geometry, biome color
  maps, lighting uniforms and sun state as the live world renderer, including
  terrain supplied by other mods;
- contains no generated per-block material fallback or custom world-material
  shader;
- does not generate unexplored chunks and does not write to a save file;
- uses a configurable client-only radius (500 blocks by default, covering a
  1000 by 1000 block diameter) without requesting distant chunks;
- provides an optional fog mask beyond the atlas radius (`M`), radius steps on
  `Page Up`/`Page Down`, and safe defaults on `Home`;
- includes a top-right settings panel for radius, unexplored fog, performance
  mode and animation preference;
- locks multiplayer to a 500-block radius with unexplored fog enabled while
  preserving separate singleplayer radius and fog preferences;
- restores the safe 500-block radius after an atlas session that did not close
  cleanly;
- works as a client-only mod, so a vanilla multiplayer server does not need it.

Entities, players, creatures, dropped items, equipment and particles are not
queried and therefore cannot appear in the scene. The exact renderer is an
isolated 1.22.6 integration. If it is unavailable, ModernAtlas reports the
failure instead of displaying substitute block models or invented materials.
Persistent coverage of previously visited distant terrain is the next cache
stage.

The existing vanilla map database remains in
`VintagestoryData/Maps/<world-id>.db`. ModernAtlas does not rename, replace,
convert, purge or delete that database. Older explored areas stay visible via
the separate vanilla map. ModernAtlas 3D reads only block columns currently
available to the client and keeps its rendering independent from that database.

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

1. Persistent, world-specific ModernAtlas block cache (separate from saves).
2. More selective exposed-face geometry for terrain, trees, buildings, paths
   and ruins.
3. Zoom-dependent levels of detail and a larger navigable world area.
4. Animated cloud overlay and visual polish matching the concept image.
5. Optional server companion for multiplayer block tiles, respecting
   server map permissions and never revealing unexplored terrain by default.

See `DESIGN.md` for the renderer and compatibility design. `AGENTS.md` records
the product goal and safety rules for future Codex sessions.

## Public distribution

Source code is MIT-licensed. See `NOTICE.md` for trademark and attribution
details. The upload description on the Vintage Story Mod DB must be in English
and clearly explain the mod's function.
