# Red-border fixtures — expectation: **no border**

Both frames are real ordinary-world captures from `scripts/run-smoke-test.sh`
(`*-ordinary-*.png`), taken in `arcyliszs cave world` at the windmill. They are
kept because they are the historical false positives of the old red-pixel
counting check: warm wooden sails and dry grass touch the outer edge on three
sides, so the frames carry thousands of red-dominant edge pixels — 3,699 and
3,347 respectively — while showing no border at all.

`scripts/check-red-border.sh` must report `border-like sides=0` and exit 0 for
both. They are lossless PNG on purpose: lossy re-encoding shifts pixel values
across the `r > 0.55 && r > g*1.35 && r > b*1.35` classification boundary and
would silently change what the fixture tests.

`scripts/test-red-border-detector.sh` also derives the negative cases from
these frames at run time — synthetic 1, 2 and 8 px red frames plus red objects
touching one and two sides — and writes them to a temporary directory. Do not
commit generated variants.

A captured frame of the real defect belongs in `../red-border-defect/`, where
every PNG is a required failing case. That directory is empty until such a
frame exists; the detector's coverage of the real defect is synthetic until
then.

These fixtures are test data. `scripts/package.sh` only packs `modinfo.json`,
`LICENSE`, `NOTICE.md`, the built DLL and `assets/`, so nothing here reaches
the mod ZIP.
