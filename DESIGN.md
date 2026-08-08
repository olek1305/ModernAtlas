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
   tilt. Draw clouds afterward as a non-persistent animated overlay.

## Visibility policy

The capture includes blocks and fluids that contribute to the visible exterior
of the world. Fully enclosed blocks may be discarded. Entities and all transient
render systems are never queried, which excludes players, mobs, armor, held or
dropped items, weapons and particles by construction.

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
