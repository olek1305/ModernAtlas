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
