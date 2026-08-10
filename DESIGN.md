# ModernAtlas 3D renderer design

## Why the atlas uses exact loaded meshes

The client map does not contain the block geometry needed to reproduce roofs,
walls, trees, connected models or modded block shapes. ModernAtlas therefore
draws Vintage Story's already completed client chunk meshes directly while the
corresponding chunks are loaded. It does not request distant chunks, invent a
height-field replacement or maintain a persistent terrain cache. Unavailable
geometry is concealed by the player-anchored fog boundary.

## Render flow

1. Play the short input-capturing first-person opening scene while preparing
   atlas-only transient resources within a frame budget; cancellation restores
   the captured camera and animation state without opening the atlas.
2. Open a dedicated full-screen atlas framebuffer and orthographic camera.
3. Reuse completed opaque chunk meshes with the engine's registered block
   texture atlases, color maps and current animation uniforms.
4. Draw only server-authorized, already loaded living models into the same
   terrain depth buffer.
5. Run the engine's required before/after OIT setup around non-fluid
   transparent chunk materials, without invoking the global entity or particle
   render stage.
6. Draw completed liquid mesh pools with the stable ModernAtlas liquid shader.
7. Apply the transient cave-safety, Survival ore-concealment, loaded-data layer
   and disclosure-boundary
   filters, compose OIT into the native Primary framebuffer, and blit once to
   the window.
8. Draw optional live cloud cover, fog and lightweight GUI markers afterward.
9. Restore every modified engine uniform and framebuffer state before normal
   world rendering resumes.

## Visibility and disclosure

The atlas follows Vintage Story's own view-distance setting because that is
what determines which exact meshes exist on the client. The clear radius is
anchored to the player's real position, so panning the atlas camera cannot
reveal new terrain. Multiplayer fog and living-model categories are limited by
the server policy, with fog on and living models off when no policy arrives.

The transient surface-height texture hides underground geometry in safe mode.
An exterior envelope preserves complete cliff steps, building walls and floors
near the surface. Faces farther below the envelope use a subdued neutral-gray
atlas material so a cave opening cannot expose the empty framebuffer. This is
a shader treatment of real geometry, not a generated shell or world block.

Survival ore concealment enumerates every registered block whose public
material is `EnumBlockMaterial.Ore`, follows its baked composite texture
variants with cycle-safe incremental work, and builds transient GPU lookups
from ore-atlas texels to their baked base host-rock textures. The lookup affects
only the atlas terrain shaders and preserves the original mesh, UVs, alpha and
biome lighting. It is disabled in Creative/Cheat, never changes a block or
runtime atlas, and is discarded with the world-specific renderer.

## Search and analysis layers

Loaded-map search is exposed only in singleplayer Creative or a per-world
accepted Cheat Mode. It resolves registered block definitions and scans only
chunks for which the public block accessor already returns client data. Work is
incremental and budgeted; entities are limited to authorized rendered living
models, while dropped-item results use markers instead of the global item
renderer.

Climate and land-analysis layers sample only loaded map chunks and regions into
a transient eight-block-resolution color texture. The shader tints existing 3D
geometry and can return immediately to normal textured terrain. Ore-density
analysis is restricted to singleplayer Creative or accepted Cheat Mode. When a
loaded region has no ore-potential map, the layer samples registered
`EnumBlockMaterial.Ore` blocks in already loaded vertical chunk columns; it
never requests or generates missing chunks. No layer is stored in a save, map
database or ModernAtlas terrain cache.

Search and inspection labels avoid the engine's translated collectible and
entity name methods while the survival handbook may still be building on a
worker thread. Stable asset codes, custom names and player nicknames avoid a
known concurrent mutation path in Vintage Story's translation diagnostics.

World teardown restores managed shader source and Harmony state without
activating engine shaders. Vintage Story clears default shader-uniform arrays
before the `LeaveWorld` event, so uniform uploads during disposal can reach the
graphics driver with null color-map data. Atlas filter uniforms are instead
disabled by the render-pass `finally` block before teardown begins.

## Compatibility boundary

Vintage Story 1.22.6 does not publicly expose its completed terrain GPU meshes,
so that integration is isolated in `ExactChunkRendererAdapter` and guarded by a
version check. Block materials and shapes still come from the runtime engine
atlases and meshes, which preserves registered mod content without hard-coded
vanilla block IDs. If exact rendering or a safety shader is unavailable, the
atlas reports the failure instead of substituting invented terrain.

The normal player-camera shadow map is never sampled from the elevated atlas
camera. Atlas visual-lab controls alter only atlas exposure, layer opacity,
boundary softness, cave-mask brightness and fog palette; all world renderer
state is restored after the atlas pass.
