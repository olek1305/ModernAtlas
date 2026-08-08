# ModernAtlas

ModernAtlas is a client-side map enhancement for Vintage Story 1.22.6. Its
public name is deliberately independent from Google trademarks.

## Current 0.2.0 prototype

- keeps the vanilla map, discovered areas and waypoints intact;
- colors newly loaded map cells from their real topmost world block, including
  registered blocks and color providers from other mods;
- adds stronger hillshade and roof/cliff edge shading so structures and terrain
  are easier to distinguish;
- rebuilds a visible map tile after a block change;
- displays terrain height under the cursor;
- opens the enhanced world map with `G` (rebindable in Controls);
- does not generate unexplored chunks and does not write to a save file;
- works as a client-only mod, so a vanilla multiplayer server does not need it.

Entities, players, creatures, dropped items, equipment and particles are not
queried and therefore cannot appear on this layer. If precise block data is not
currently client-loaded, ModernAtlas leaves the vanilla image visible and adds
only translucent relief.

The existing vanilla map database remains in
`VintagestoryData/Maps/<world-id>.db`. ModernAtlas does not rename, replace,
convert, purge or delete that database. Older explored areas stay visible via
the vanilla terrain layer. The relief overlay becomes available wherever the
client currently has height data.

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
`System.Collections` while compiling client-only map layers.

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
2. Exposed block geometry for terrain, trees, buildings, paths and ruins.
3. Tilted 3D mesh view with rotation and pitch controls.
4. Zoom-dependent level of detail and animated cloud overlay.
5. Optional server companion for multiplayer block tiles, respecting
   server map permissions and never revealing unexplored terrain by default.

See `DESIGN.md` for the renderer and compatibility design. `AGENTS.md` records
the product goal and safety rules for future Codex sessions.

## Public distribution

Source code is MIT-licensed. See `NOTICE.md` for trademark and attribution
details. The upload description on the Vintage Story Mod DB must be in English
and clearly explain the mod's function.
