# Known visual issues

Open visual defects in the atlas image. They are **baseline defects, not
regressions** of the partial-file refactor or the nullable texture-guard fixes:
the same two remaining artifacts are present in the owner's `modernatlas-frozen-*`
captures from 2026-08-20 10:12, produced by the package built at 10:10
(`modernatlas_0.6.7.zip`, SHA-256 `b174f2f7…ca1a0`), and they reappear
unchanged in the 17:44 package (`5e937c20…d8e704`). No further smoke run of the
older package is needed to establish that.

A green `client-main.log` does not cover any of these: the automated checks
validate renderer activity and lifecycle, not the semantic content of the final
image. See `AGENTS.md`, "Boundary and final-compositor guardrails".

## Resolved: camera-dependent bare tree trunks

Tree crowns on the skyline were discarded whenever no opaque terrain pixel
already existed behind every leaf in a 5x5 screen-space neighbourhood. The
opaque trunk pass remained visible, producing a forest of bare vertical poles;
rotating the camera changed which leaves had a hill behind them and therefore
changed the apparent tree shape.

Resolved in the package with SHA-256 `04195b15…cd090b`: the dedicated
vegetation pass retains native 3D depth testing, completed surface-column
guards, the player-anchored disclosure boundary and the final per-pixel
boundary resolve, but no longer requires an unrelated opaque screen pixel
behind a leaf. The owner confirmed the corrected crowns from the same location
at opposite camera yaws.

## Resolved: saturated colors on dark modded blocks

The atlas applied its minimum-brightness floor by scaling the final material
color to a target luminance. On very dark block faces, a tiny surviving color
channel could therefore be multiplied into a saturated red, green or blue
pixel. This was most visible on loaded structures from Primitive Survival and
Butchering, even though their ordinary-world textures were correct.

Resolved in the current `0.6.8` test package by removing the final-color
luminance lift while preserving the opaque shader's scalar lighting floor.
The source texture hue is no longer changed by the visibility safeguard. The
owner confirmed the corrected modded structures in the live atlas from the
same test world.

## 1. Central dark band in the tiled capture

The stitched tiled screenshot shows a hard vertical dark band near the middle
of the image instead of a crossfaded seam. The stitcher is documented to keep a
small overlap margin per shared edge and to linearly crossfade it, so a visible
hard band points at the tile camera offsets or the crossfade, not at the source
frames.

Evidence: smoke capture `*-screenshot-tiled.png` (2x grid).

## 2. Rectangular brightness differences between tiles

Neighbouring tiles of the stitched image differ in brightness, so tile
boundaries are visible as rectangular blocks. The capture freezes wind, water
and cloud offsets per job, so animated surfaces are not the cause; per-tile
camera state or exposure is the more likely source.

Evidence: smoke capture `*-screenshot-tiled.png`, lower half.

## Reproduction

```sh
MODERNATLAS_SMOKE_SCREENSHOT=/tmp/modernatlas-smoke bash scripts/run-smoke-test.sh
```

Inspect `*-screenshot-tiled.png` for issues 1 and 2. Both are produced with
Creative/Cheat cave mode active during
the smoke sequence, which legitimately reveals cave interiors; that revealed
geometry is not part of these defects.

## Acceptance for a fix

- The stitched tiled screenshot has no visible seam or per-tile brightness step
  at 2x and at one higher grid size.
