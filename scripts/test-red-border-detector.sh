#!/usr/bin/env bash
# Validates scripts/check-red-border.sh against real frames and synthetic
# defects. Run it after changing the detector; it needs ImageMagick only.
#
# Cases:
#   * every supplied ordinary-world frame must pass, including frames with
#     wood or dry grass touching an edge
#   * a synthetic 1, 2 and 8 px red frame on each of those frames must fail
#   * a red object touching one or two sides must pass
#   * every image in the defect fixture directory (if present) must fail
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
detector="$project_dir/scripts/check-red-border.sh"
normal_dir="${MODERNATLAS_RED_BORDER_FIXTURES:-$project_dir/tests/fixtures/red-border}"
defect_dir="${MODERNATLAS_RED_BORDER_DEFECTS:-$project_dir/tests/fixtures/red-border-defect}"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

# The committed fixtures are always exercised; extra frames may be passed in,
# for example a fresh smoke run's ordinary-world captures.
frames=()
shopt -s nullglob
frames+=("$normal_dir"/*.png)
shopt -u nullglob
frames+=("$@")
if (( ${#frames[@]} == 0 )); then
    printf 'No fixtures in %s and no frames given.\n' "$normal_dir" >&2
    exit 2
fi

passed=0
failed=0

expect() {
    local expectation="$1" image="$2" label="$3"
    local status=0
    "$detector" "$image" >"$work_dir/out.txt" 2>&1 || status=$?
    if [[ "$expectation" == "pass" && "$status" -eq 0 ]] \
        || [[ "$expectation" == "fail" && "$status" -ne 0 ]]; then
        printf 'ok    %-52s (expected %s)\n' "$label" "$expectation"
        passed=$((passed + 1))
        return 0
    fi
    printf 'FAIL  %-52s (expected %s, got exit %s)\n' "$label" "$expectation" "$status"
    sed 's/^/      /' "$work_dir/out.txt"
    failed=$((failed + 1))
    return 0
}

for frame in "${frames[@]}"; do
    [[ -s "$frame" ]] || continue
    name="$(basename "$frame" .png)"
    expect pass "$frame" "$name (unmodified)"

    for thickness in 1 2 8; do
        bordered="$work_dir/$name-border-$thickness.png"
        # Replace exactly the outer N px with red, leaving the scene intact.
        magick "$frame" -shave "${thickness}x${thickness}" \
            -bordercolor red -border "$thickness" "$bordered"
        expect fail "$bordered" "$name + ${thickness}px red frame"
    done

    height="$(magick identify -format '%h' "$frame")"
    width="$(magick identify -format '%w' "$frame")"
    one_side="$work_dir/$name-one-side.png"
    magick "$frame" -fill red -draw \
        "rectangle 0,$((height / 4)) 80,$((height * 3 / 4))" "$one_side"
    expect pass "$one_side" "$name + red object on one side"

    two_sides="$work_dir/$name-two-sides.png"
    magick "$frame" -fill red \
        -draw "rectangle 0,0 $((width / 3)) 90" \
        -draw "rectangle 0,0 90 $((height / 3))" "$two_sides"
    expect pass "$two_sides" "$name + red object on two sides"
done

shopt -s nullglob
defects=("$defect_dir"/*.png)
shopt -u nullglob
if (( ${#defects[@]} == 0 )); then
    printf 'note  no captured real-defect frame is stored; add one to %s to make it a required case\n' \
        "$defect_dir"
fi
for defect in "${defects[@]}"; do
    expect fail "$defect" "captured defect $(basename "$defect")"
done

printf '\n%s passed, %s failed\n' "$passed" "$failed"
(( failed == 0 ))
