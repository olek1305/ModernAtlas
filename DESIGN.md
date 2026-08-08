# ModernAtlas 3D renderer design

## Why a separate atlas cache is required

The vanilla client map retains explored map imagery and height information, but
it is not a permanent copy of every block in every visited chunk. Precise roofs,
walls, trees and ruins can therefore be reconstructed only while their chunks
are loaded on the client. ModernAtlas will capture a compact representation at
that time and keep it in its own disposable per-world cache.

## Data flow

1. Observe client-loaded chunk columns and block-change events.
2. On the game thread, copy only the block data required for visible surfaces.
3. Resolve registered block definitions. Unknown or broken definitions map to a
   configurable neutral stone fallback.
4. Use Vintage Story's block tessellator and texture atlas so modded block
   shapes and materials retain their appearance.
5. Build atlas meshes or raster tiles incrementally outside the render loop.
6. Upload a bounded number of completed resources per frame.
7. Draw them with an orthographic camera that supports pan, zoom, rotation and
   tilt. Draw the game's live clouds afterward as a non-persistent, optional,
   bounded 3D layer with soft independent shadows, using their current weather
   phase and world position.

## Visibility policy

The capture includes blocks and fluids that contribute to the visible exterior
of the world. Fully enclosed and subterranean cave geometry may be discarded.
The default camera follows the client rain-height surface and does not provide
an underground cutaway. Server-authorized 3D models may render already loaded
players, animals, hostile mobs and NPCs. They are drawn before fog so the fog
still conceals them. Model renderers and all other transient systems remain
excluded, including armor, held or dropped items, weapons and particles.

The live prototype does not generate a height-field shell or textured boundary
wall. It draws only exact client-loaded chunk geometry and uses the existing
unexplored-area fog where data is unavailable. Any future cave mask must avoid
inventing visible blocks or cutting valleys, slopes and building walls at a
single global height.

The atlas does not reuse shadow maps rendered for the normal player camera.
Those maps do not align with the elevated orthographic atlas eye and produce
chunk-sized dark squares near dusk. Atlas terrain instead keeps stable vertex
lighting and normal-based directional shading from a fixed neutral light.

The live exact-mesh prototype follows Vintage Story's own view-distance setting.
It does not expose a second radius because only the game controls which chunk
meshes are loaded and therefore available to the atlas.

## Compatibility policy

Block identity is stored by asset code rather than numeric ID because numeric
IDs may change when a mod list changes. The cache records a content fingerprint.
If a contributing mod disappears or changes, unresolved entries use neutral
stone and affected chunks are rebuilt when source world data becomes available.

The initial implementation may simplify special dynamic block entities. The
ordinary registered block remains visible, while custom animated or entity-like
attachments are excluded unless they can be obtained safely through the normal
block tessellation path.

## Graceful levels of detail

- Far: vanilla map color plus stored height relief.
- Medium: one representative textured surface per block column with elevation.
- Near: cached exposed block geometry, including vertical walls and structures.

This prevents a continent-sized atlas from keeping millions of detailed block
meshes on the GPU at once.
