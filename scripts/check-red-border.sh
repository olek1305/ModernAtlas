#!/usr/bin/env bash
# Ordinary-world red-border detector.
#
# The defect this guards against is a red frame drawn around the ordinary
# world after the atlas closes. That defect has a shape: a long, continuous
# red run along the outer edge of at least three sides. Counting red pixels
# cannot separate it from ordinary scene content, because wood, dry grass and
# sunset light legitimately change how much red touches an edge between two
# frames.
#
# Criteria, per side:
#   * classification: the historical rule, r > 0.55 && r > g*1.35 && r > b*1.35
#   * edge band: the outer 8 px, projected onto one line by taking the maximum
#     across the band, so a 1 px border counts as covering its side
#   * control band: 8 px starting 16 px into the image, measured the same way
#   * a side looks like a border when its longest continuous red run covers
#     most of the side AND clearly exceeds the control band's longest run
# A frame fails only when at least three sides look like a border. Red pixel
# totals are still reported, as diagnostics only.
set -euo pipefail

readonly RED_RULE='(r > 0.55 && r > g*1.35 && r > b*1.35) ? 1 : 0'
readonly EDGE_DEPTH=8
readonly CONTROL_OFFSET=16
readonly CONTROL_DEPTH=8
readonly MINIMUM_SIDE_FRACTION=0.60
readonly MINIMUM_RUN_PIXELS=64
readonly CONTROL_RUN_FACTOR=2
readonly CONTROL_RUN_MARGIN=32
readonly SIDES_REQUIRED=3

# Longest continuous red run and covered length along one band.
# The band is projected onto a single line by averaging across its depth, so a
# column (or row) counts as covered when it holds at least one red pixel: an
# 8 px deep band gives a minimum non-zero average of 255/8, well above the
# threshold below. A centred maximum filter cannot be used here because its
# window is clipped at the outermost line, which is exactly where a 1 px
# border sits.
# $1 image, $2 crop geometry, $3 orientation (h: run along x, v: run along y),
# $4 length of the projected line
red_profile() {
    local image="$1" crop="$2" orientation="$3" length="$4"
    local target="${length}x1"
    [[ "$orientation" == "v" ]] && target="1x${length}"
    magick "$image" -crop "$crop" +repage \
        -fx "$RED_RULE" \
        -scale "${target}!" \
        -depth 8 txt:- \
    | awk '
        /^[0-9]+,[0-9]+:/ {
            # "12,0: (255,255,255)  #FFFFFF  white" -> first channel value
            channels = $0
            sub(/^[^(]*\(/, "", channels)
            split(channels, parts, /[,)]/)
            value = parts[1] + 0
            if (value > 8) {
                run++
                covered++
                if (run > best) best = run
            } else {
                run = 0
            }
            total++
        }
        END { printf "%d %d %d\n", best + 0, covered + 0, total + 0 }
    '
}

# Exits 1 when the image carries a border-shaped red frame.
check_image() {
    local image="$1"
    local width height
    width="$(magick identify -format '%w' "$image")"
    height="$(magick identify -format '%h' "$image")"
    if (( width < 64 || height < 64 )); then
        printf '[ModernAtlas] Red-border check: %s is too small (%sx%s).\n' \
            "$image" "$width" "$height" >&2
        return 1
    fi

    local inner_top=$((CONTROL_OFFSET))
    local inner_bottom=$((height - CONTROL_OFFSET - CONTROL_DEPTH))
    local inner_left=$((CONTROL_OFFSET))
    local inner_right=$((width - CONTROL_OFFSET - CONTROL_DEPTH))
    local -a names=(top bottom left right)
    local -a edge_crops=(
        "${width}x${EDGE_DEPTH}+0+0"
        "${width}x${EDGE_DEPTH}+0+$((height - EDGE_DEPTH))"
        "${EDGE_DEPTH}x${height}+0+0"
        "${EDGE_DEPTH}x${height}+$((width - EDGE_DEPTH))+0"
    )
    local -a control_crops=(
        "${width}x${CONTROL_DEPTH}+0+${inner_top}"
        "${width}x${CONTROL_DEPTH}+0+${inner_bottom}"
        "${CONTROL_DEPTH}x${height}+${inner_left}+0"
        "${CONTROL_DEPTH}x${height}+${inner_right}+0"
    )
    local -a orientations=(h h v v)

    local border_sides=0
    local report=""
    local index
    for index in 0 1 2 3; do
        local side="${names[$index]}"
        local orientation="${orientations[$index]}"
        local side_length=$width
        [[ "$orientation" == "v" ]] && side_length=$height

        local edge control
        edge="$(red_profile "$image" "${edge_crops[$index]}" "$orientation" "$side_length")"
        control="$(red_profile "$image" "${control_crops[$index]}" "$orientation" "$side_length")"
        local edge_run="${edge%% *}"
        local edge_rest="${edge#* }"
        local edge_covered="${edge_rest%% *}"
        local control_run="${control%% *}"

        local required_run
        required_run="$(awk -v len="$side_length" -v frac="$MINIMUM_SIDE_FRACTION" \
            -v floorpx="$MINIMUM_RUN_PIXELS" \
            'BEGIN { r = len * frac; if (r < floorpx) r = floorpx; printf "%d", r }')"
        local control_gate=$((control_run * CONTROL_RUN_FACTOR + CONTROL_RUN_MARGIN))

        local verdict="scene"
        if (( edge_run >= required_run && edge_run >= control_gate )); then
            verdict="BORDER"
            border_sides=$((border_sides + 1))
        fi
        report+="$(printf '    %-6s run=%s/%s covered=%s control_run=%s needs>=%s and >=%s -> %s\n' \
            "$side" "$edge_run" "$side_length" "$edge_covered" "$control_run" \
            "$required_run" "$control_gate" "$verdict")"$'\n'
    done

    # Diagnostics only: the historical total of red-dominant edge pixels.
    local total_edge_red
    total_edge_red="$( {
        magick "$image" -crop "${width}x${EDGE_DEPTH}+0+0" +repage -fx "$RED_RULE" \
            -format '%[fx:mean] %[fx:w*h]\n' info:
        magick "$image" -crop "${width}x${EDGE_DEPTH}+0+$((height - EDGE_DEPTH))" +repage \
            -fx "$RED_RULE" -format '%[fx:mean] %[fx:w*h]\n' info:
        magick "$image" -crop "${EDGE_DEPTH}x$((height - 2 * EDGE_DEPTH))+0+${EDGE_DEPTH}" +repage \
            -fx "$RED_RULE" -format '%[fx:mean] %[fx:w*h]\n' info:
        magick "$image" -crop "${EDGE_DEPTH}x$((height - 2 * EDGE_DEPTH))+$((width - EDGE_DEPTH))+${EDGE_DEPTH}" \
            +repage -fx "$RED_RULE" -format '%[fx:mean] %[fx:w*h]\n' info:
    } | awk '{ pixels += $1 * $2 } END { printf "%.0f", pixels }')"

    printf '[ModernAtlas] Red-border check: %s (%sx%s), border-like sides=%s, red edge pixels=%s (diagnostic).\n' \
        "$image" "$width" "$height" "$border_sides" "$total_edge_red"
    printf '%s' "$report"

    if (( border_sides >= SIDES_REQUIRED )); then
        printf '[ModernAtlas] Red-border check FAILED: %s shows a continuous red frame on %s sides.\n' \
            "$image" "$border_sides" >&2
        return 1
    fi
    return 0
}

if (( $# == 0 )); then
    printf 'usage: %s <image.png> [more.png ...]\n' "$0" >&2
    exit 2
fi

status=0
for candidate in "$@"; do
    if [[ ! -s "$candidate" ]]; then
        printf '[ModernAtlas] Red-border check: missing or empty image %s\n' "$candidate" >&2
        status=1
        continue
    fi
    check_image "$candidate" || status=1
done
exit "$status"
