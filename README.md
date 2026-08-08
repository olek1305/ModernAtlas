# ModernAtlas

ModernAtlas is a client-side map enhancement for Vintage Story 1.22.6. Its
public name is deliberately independent from Google trademarks.

## Current 0.1.0 prototype

- keeps the vanilla map, discovered areas and waypoints intact;
- adds dynamic hillshade and contour accents from client-visible height data;
- displays terrain height under the cursor;
- opens the enhanced world map with `G` (rebindable in Controls);
- does not generate unexplored chunks and does not write to a save file;
- works as a client-only mod, so a vanilla multiplayer server does not need it.

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

The source-only ZIP is intentionally the default because it builds through the
game's own compiler and avoids bundling game binaries.

## Planned architecture

1. Persistent, world-specific ModernAtlas tile cache (separate from saves).
2. Surface classification for trees, buildings, paths and ruins.
3. Zoom-dependent level of detail.
4. Tilted 3D mesh view with rotation and pitch controls.
5. Optional server companion for multiplayer height/building tiles, respecting
   server map permissions and never revealing unexplored terrain by default.

## Public distribution

Source code is MIT-licensed. See `NOTICE.md` for trademark and attribution
details. The upload description on the Vintage Story Mod DB must be in English
and clearly explain the mod's function.
