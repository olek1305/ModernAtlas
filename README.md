# ModernAtlas

ModernAtlas is an independent 3D world-map mod for **Vintage Story 1.22.3 or newer**.
Press `G` to open an interactive parchment atlas with terrain relief, water,
buildings, ruins, trees, climate layers, search tools and optional live-looking
clouds.

## How it works

ModernAtlas renders the exact block meshes and textures that Vintage Story has
already loaded on the client. Terrain and structures therefore appear on the
atlas as they appear in the world, including registered blocks and textures
from other mods. It does not generate unexplored terrain, request distant
chunks, replace the vanilla map or maintain a separate terrain cache.

Rendering and the interface run client-side. The optional server component
only controls which already loaded living entities may be shown in multiplayer;
ModernAtlas can also be used on the client when a server does not install it.

## Screenshots

![ModernAtlas 3D parchment map](ModernAtlas.png)

![ModernAtlas terrain view](ModernAtlas2.png)

![ModernAtlas interface](ModernAtlas3.png)

The atlas screenshot panel offers independent `Resolution` (1x-8x) and
`Capture area` (100%, 75%, 50% or 25%) choices. The area is a centered crop of
the live view, while the PNG dimensions stay tied to the selected resolution;
the preview shows the final dimensions, megapixels and effective detail. A
100 MP safety budget prevents extreme captures from exhausting memory and the
preview warns when a requested result will be downsampled.

Large captures use an isolated ModernAtlas job directory under the game's
`ModData` path. A small JSON manifest records the selected settings, dimensions,
paths and capture stage, while each captured pixel tile is staged in its own
binary file rather than in JSON. The stitched image is written to a private
`*.png.part`, structurally validated, and moved atomically into
`Screenshots/ModernAtlas` only after the PNG stream closes successfully. The
public screenshot folder therefore contains completed PNG files only; manifests,
tiles and partial files are removed from the exact job directory after success
or cancellation.

The opt-in automated smoke path can run the consecutive regression sequence
with `MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE=1`; it captures 1x/25% followed by
8x/25% in one session. `MODERNATLAS_SMOKE_SCREENSHOT_CANCEL=1` instead closes
the progress modal during stitching and verifies that the cancelled job leaves
neither a PNG nor a public sidecar.

The automated smoke test targets the existing standard generated world
`arcyliszs cave world` by default. Run it with `bash scripts/run-smoke-test.sh`;
set `MODERNATLAS_SMOKE_WORLD` to select another standard test world. The
superflat `TESTCREATIVE` world is not a valid rendering regression target.

## Installation

Place the ModernAtlas release ZIP in the Vintage Story `Mods` directory. Keep
the ZIP packed, start Vintage Story 1.22.3 or newer and press `G` in a loaded world.

## AI disclosure

ModernAtlas is a project by **Arcylisz**, created with code and documentation
assistance from **ChatGPT**, an AI system by OpenAI. The project remains under
human direction and review.

## License and trademarks

The original ModernAtlas source code is available under the [MIT License](LICENSE).
See [NOTICE.md](NOTICE.md) for attribution, independence and trademark details.
Vintage Story is required separately; no game binaries, assets or map data are
redistributed by this repository. ModernAtlas is not affiliated with or
endorsed by Anego Studios or OpenAI.
