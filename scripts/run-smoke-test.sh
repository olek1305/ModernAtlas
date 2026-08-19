#!/usr/bin/env bash
set -euo pipefail

game_path="${VINTAGE_STORY_PATH:-/opt/vintagestory}"
data_path="${MODERNATLAS_SMOKE_DATA_PATH:-/home/arcylisz/.config/VintagestoryData}"
smoke_world="${MODERNATLAS_SMOKE_WORLD:-arcyliszs cave world}"
main_log="$data_path/Logs/client-main.log"
crash_log="$data_path/Logs/client-crash.log"
main_mtime_before=0
if [[ -f "$main_log" ]]; then
    main_mtime_before="$(stat -c '%Y' "$main_log")"
fi
crash_mtime_before=0
if [[ -f "$crash_log" ]]; then
    crash_mtime_before="$(stat -c '%Y' "$crash_log")"
fi

printf '[ModernAtlas] Starting the automated smoke test in standard world: %s\n' "$smoke_world"
status=0
smoke_mask_prefix="${MODERNATLAS_SMOKE_SCREENSHOT_MASK:-${MODERNATLAS_SMOKE_SCREENSHOT:-}}"
smoke_sequence="${MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE:-0}"
case "${smoke_sequence,,}" in
    1|true|yes) smoke_sequence=1 ;;
    *) smoke_sequence=0 ;;
esac
env MODERNATLAS_SMOKE_TEST=1 \
    MODERNATLAS_SMOKE_SCREENSHOT_SEQUENCE="$smoke_sequence" \
    MODERNATLAS_SMOKE_SCREENSHOT_MASK="$smoke_mask_prefix" \
    "$game_path/Vintagestory" \
    --dataPath "$data_path" \
    -o "$smoke_world" || status=$?

if (( status != 0 )); then
    printf '[ModernAtlas] Smoke process exited with status %d.\n' "$status" >&2
    exit "$status"
fi
if [[ ! -f "$main_log" ]]; then
    printf '[ModernAtlas] Missing client log: %s\n' "$main_log" >&2
    exit 1
fi
main_mtime_after="$(stat -c '%Y' "$main_log")"
if (( main_mtime_after <= main_mtime_before )); then
    printf '[ModernAtlas] client-main.log was not refreshed by this smoke run.\n' >&2
    exit 1
fi

required_patterns=(
    'AUTOMATED SMOKE GOD MODE ENABLED'
    'AUTOMATED SMOKE GOD MODE RESTORED'
    'AUTOMATED ATLAS TWO-CYCLE CHECK PASSED'
    'Automated resolved-atlas alpha validation passed'
    'Atlas close state check passed'
    'World leave received'
    'Released world-specific atlas rendering resources'
    'AUTOMATED WORLD-EXIT CHECK PASSED'
)
for pattern in "${required_patterns[@]}"; do
    if ! rg -q "$pattern" "$main_log"; then
        printf '[ModernAtlas] Required smoke marker is missing: %s\n' "$pattern" >&2
        exit 1
    fi
done

if [[ -n "${MODERNATLAS_SMOKE_SCREENSHOT:-}" ]]; then
    screenshot_prefix="$MODERNATLAS_SMOKE_SCREENSHOT"
    required_screenshots=(
        "${screenshot_prefix}-ordinary-before-atlas.png"
        "${screenshot_prefix}-ordinary-after-cycle-1.png"
        "${screenshot_prefix}-ordinary-after-cycle-2.png"
        "${screenshot_prefix}-resolved-atlas.png"
        "${screenshot_prefix}-opening-immediate.png"
        "${screenshot_prefix}-opening-pocket.png"
        "${screenshot_prefix}-opening-handoff.png"
        "${screenshot_prefix}-opening.png"
        "${screenshot_prefix}-closing-immediate.png"
        "${screenshot_prefix}-closing-pocket.png"
        "${screenshot_prefix}-closing-handoff.png"
        "${screenshot_prefix}-closing.png"
    )
    if (( smoke_sequence == 1 )); then
        required_screenshots+=(
            "${screenshot_prefix}-unfiltered-off.png"
            "${screenshot_prefix}-filtered-google-earth.png"
            "${screenshot_prefix}-validity-mask-filtered-last-nonempty-tile.png"
        )
    else
        required_screenshots+=(
            "${screenshot_prefix}-screenshot-tiled.png"
        )
    fi
    for screenshot in "${required_screenshots[@]}"; do
        if [[ ! -s "$screenshot" ]]; then
            printf '[ModernAtlas] Required smoke screenshot is missing or empty: %s\n' "$screenshot" >&2
            exit 1
        fi
    done

    if ! command -v magick >/dev/null 2>&1; then
        printf '[ModernAtlas] ImageMagick is required for the ordinary-world red-border check.\n' >&2
        exit 1
    fi
    count_red_border_pixels() {
        local ordinary_screenshot="$1"
        local width="$(magick identify -format '%w' "$ordinary_screenshot")"
        local height="$(magick identify -format '%h' "$ordinary_screenshot")"
        if (( width < 16 || height < 16 )); then
            printf '[ModernAtlas] Ordinary-world screenshot is too small for the border check: %s\n' "$ordinary_screenshot" >&2
            exit 1
        fi
        {
            magick "$ordinary_screenshot" -crop "${width}x8+0+0" +repage \
                -fx '(r > 0.55 && r > g*1.35 && r > b*1.35) ? 1 : 0' \
                -format '%[fx:mean] %[fx:w*h]\n' info:
            magick "$ordinary_screenshot" -crop "${width}x8+0+$((height - 8))" +repage \
                -fx '(r > 0.55 && r > g*1.35 && r > b*1.35) ? 1 : 0' \
                -format '%[fx:mean] %[fx:w*h]\n' info:
            magick "$ordinary_screenshot" -crop "8x$((height - 16))+0+8" +repage \
                -fx '(r > 0.55 && r > g*1.35 && r > b*1.35) ? 1 : 0' \
                -format '%[fx:mean] %[fx:w*h]\n' info:
            magick "$ordinary_screenshot" -crop "8x$((height - 16))+$((width - 8))+8" +repage \
                -fx '(r > 0.55 && r > g*1.35 && r > b*1.35) ? 1 : 0' \
                -format '%[fx:mean] %[fx:w*h]\n' info:
        } | awk '{ pixels += $1 * $2 } END { printf "%.0f\n", pixels }'
    }
    ordinary_before="${screenshot_prefix}-ordinary-before-atlas.png"
    ordinary_after_one="${screenshot_prefix}-ordinary-after-cycle-1.png"
    ordinary_after_two="${screenshot_prefix}-ordinary-after-cycle-2.png"
    baseline_red_border_pixels="$(count_red_border_pixels "$ordinary_before")"
    allowed_red_border_pixels=$((baseline_red_border_pixels + 32))
    for ordinary_screenshot in "$ordinary_after_one" "$ordinary_after_two"; do
        red_border_pixels="$(count_red_border_pixels "$ordinary_screenshot")"
        if (( red_border_pixels > allowed_red_border_pixels )); then
            printf '[ModernAtlas] Ordinary-world red-border check failed: %s has %s red-dominant edge pixels (baseline %s, allowed %s).\n' \
                "$ordinary_screenshot" "$red_border_pixels" "$baseline_red_border_pixels" "$allowed_red_border_pixels" >&2
            exit 1
        fi
    done
    printf '[ModernAtlas] Automated ordinary-world red-border check passed: baseline=%s, after-cycle-1=%s, after-cycle-2=%s, tolerance=32.\n' \
        "$baseline_red_border_pixels" \
        "$(count_red_border_pixels "$ordinary_after_one")" \
        "$(count_red_border_pixels "$ordinary_after_two")"
fi

if rg -n \
    'AUTOMATED .*FAILED|ModernAtlas.*(Critical|Exception|shader failure|disposed)|ModernAtlas.*OpenGL' \
    "$main_log"; then
    printf '[ModernAtlas] A ModernAtlas failure marker was found in the fresh client log.\n' >&2
    exit 1
fi

crash_mtime_after=0
if [[ -f "$crash_log" ]]; then
    crash_mtime_after="$(stat -c '%Y' "$crash_log")"
fi
if (( crash_mtime_after > crash_mtime_before )); then
    printf '[ModernAtlas] The smoke run created or updated client-crash.log.\n' >&2
    exit 1
fi

printf '[ModernAtlas] Automated smoke test and world-exit validation passed.\n'
