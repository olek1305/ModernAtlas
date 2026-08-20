# Known visual issues

Open visual defects in the atlas image. They are **baseline defects, not
regressions** of the partial-file refactor or the nullable texture-guard fixes:
the same three artifacts are present in the owner's `modernatlas-frozen-*`
captures from 2026-08-20 10:12, produced by the package built at 10:10
(`modernatlas_0.6.7.zip`, SHA-256 `b174f2f7…ca1a0`), and they reappear
unchanged in the 17:44 package (`5e937c20…d8e704`). No further smoke run of the
older package is needed to establish that.

A green `client-main.log` does not cover any of these: the automated checks
validate renderer activity and lifecycle, not the semantic content of the final
image. See `AGENTS.md`, "Boundary and final-compositor guardrails".

## 1. Vertical stubs at the terrain boundary

Thin vertical columns hang below the terrain surface along the edge of the
loaded-mesh footprint, most visible on the left and lower edges of the atlas.
They are the partial vertical mesh columns that the disclosure boundary is
meant to reject, rendered as isolated stubs outside the accepted footprint.

Evidence: smoke capture `*-resolved-atlas.png`; owner's `modernatlas-frozen-*`
frames from 10:12.

Notes for a fix: the guardrails in `AGENTS.md` apply in full. In particular the
completed-column footprint must not be derived from
`consideredTerrainColumns`, which contains exactly these partial columns, and
the mask must be visualized as a debug overlay before the compositor is allowed
to reject anything.

## 2. Central dark band in the tiled capture

The stitched tiled screenshot shows a hard vertical dark band near the middle
of the image instead of a crossfaded seam. The stitcher is documented to keep a
small overlap margin per shared edge and to linearly crossfade it, so a visible
hard band points at the tile camera offsets or the crossfade, not at the source
frames.

Evidence: smoke capture `*-screenshot-tiled.png` (2x grid).

## 3. Rectangular brightness differences between tiles

Neighbouring tiles of the stitched image differ in brightness, so tile
boundaries are visible as rectangular blocks. The capture freezes wind, water
and cloud offsets per job, so animated surfaces are not the cause; per-tile
camera state or exposure is the more likely source.

Evidence: smoke capture `*-screenshot-tiled.png`, lower half.

## Reproduction

```sh
MODERNATLAS_SMOKE_SCREENSHOT=/tmp/modernatlas-smoke bash scripts/run-smoke-test.sh
```

Inspect `*-resolved-atlas.png` for issue 1 and `*-screenshot-tiled.png` for
issues 2 and 3. Both are produced with Creative/Cheat cave mode active during
the smoke sequence, which legitimately reveals cave interiors; that revealed
geometry is not part of these defects.

## Acceptance for a fix

- A captured image from the exact installed package shows terrain with no
  isolated trunks, leaves or vertical chunk walls outside the accepted
  footprint.
- Tilted views from both opposite yaw directions are captured, because boundary
  defects change appearance after a 180-degree rotation.
- The stitched tiled screenshot has no visible seam or per-tile brightness step
  at 2x and at one higher grid size.
