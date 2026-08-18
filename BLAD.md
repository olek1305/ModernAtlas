# Atlas boundary failure notes

## Observed failure

An attempted final boundary mask made the atlas completely empty even though
the automated smoke test reported that exact terrain rendering had passed.
The failure was visible in the real atlas window: the background rendered, but
no terrain, vegetation, fluids or structures remained.

## Do not repeat these changes

- Do not make the final compositor fail closed from a newly introduced GPU
  mask until that mask has been visualized and its coordinate system, channel
  layout, dimensions and non-zero coverage have been verified in game.
- Do not use `supportedTerrainColumns` as proof that a surface was rendered.
  It may be empty even when the engine rendered many completed terrain columns.
- Do not assume that switching to `visibleTerrainColumns` proves the final
  texture lookup is correct. A non-empty CPU set does not prove that the GPU
  texture contains readable markers at the reconstructed world coordinates.
- Do not use `consideredTerrainColumns` as a completed-surface footprint. It
  also contains the partial vertical mesh columns that the boundary is meant
  to reject, so eroding that set still leaves the original artifacts.
- Do not feed an already filtered visible-column set into the next frame's
  mask. That creates feedback and repeatedly shrinks the atlas every frame.
- Do not add a below-surface discard to the final full-screen compositor at
  the same time as a new completed-column mask. These are separate changes and
  must be tested independently so an empty result has one diagnosable cause.
- Do not disable exact rendering for the entire session because an optional
  boundary refinement failed. Preserve the last known visible atlas output or
  bypass only the failed refinement.
- Do not treat `BoundaryResolvedLastFrame`, a successful draw call or an exact
  terrain counter as proof that the resulting color framebuffer contains map
  pixels.
- Do not install a boundary rewrite as the active mod before checking an
  actual captured atlas image produced by that exact package.

## Required validation before another boundary rewrite

1. Render the proposed mask as a temporary debug overlay with a distinct
   color and confirm that it follows the intended loaded-mesh footprint.
2. Log mask origin, dimensions, marked-cell count and the player/current
   camera chunk coordinates.
3. Read back a small sample or analyze the resolved framebuffer and require a
   reasonable number of non-background pixels.
4. Test one boundary rule at a time: completed-column coverage first, then any
   vertical surface-envelope rule.
5. Capture and inspect tilted views from both opposite yaw directions. The
   original defect changes appearance after a 180-degree rotation.
6. Keep water, lava, leaves and plants on their existing material-specific
   render paths. The final boundary may reject pixels, but it must not replace
   those materials with ordinary opaque blocks.
7. Only package and install the result after the real image contains terrain
   and shows no isolated trunks, leaves or vertical chunk walls outside the
   accepted footprint.

## Smoke-test gap

The current automated checks validate renderer activity and lifecycle, not the
semantic content of the final image. A future test must inspect framebuffer
pixels and fail when the atlas is entirely or almost entirely background.
