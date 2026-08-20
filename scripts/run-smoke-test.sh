#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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

# A second client on the same data path would share the world save and rotate
# client-main.log underneath the marker checks below, which reports a passing
# run as a failure. Refuse to start and let the operator close the game; never
# terminate somebody else's client from here.
running_clients_on_data_path() {
    local pid args
    for pid in $(pgrep -f 'Vintagestory' 2>/dev/null || true); do
        [[ "$pid" == "$$" ]] && continue
        args="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
        [[ -z "$args" ]] && continue
        # Only the game binary itself, not this script, an editor or a pgrep.
        [[ "$args" == *"/Vintagestory "* || "$args" == *"/Vintagestory" ]] || continue
        if [[ "$args" == *"--dataPath"* ]]; then
            [[ "$args" == *"--dataPath $data_path"* ]] || continue
        else
            [[ "$data_path" == "$HOME/.config/VintagestoryData" ]] || continue
        fi
        printf '    pid %s: %s\n' "$pid" "$args"
    done
}

active_clients="$(running_clients_on_data_path)"
if [[ -n "$active_clients" ]]; then
    printf '[ModernAtlas] smoke test not started: a Vintage Story client is already running on this data path (%s).\n' "$data_path" >&2
    printf '%s\n' "$active_clients" >&2
    printf '[ModernAtlas] Close that client and run this script again. No process was terminated.\n' >&2
    exit 1
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
        "${screenshot_prefix}-layer-moisture.png"
        "${screenshot_prefix}-layer-ore-panel.png"
        "${screenshot_prefix}-toolbar-scroll.png"
        "${screenshot_prefix}-toolbar-narrow.png"
        "${screenshot_prefix}-settings-narrow-bottom.png"
        "${screenshot_prefix}-search-wide.png"
        "${screenshot_prefix}-search-narrow.png"
        "${screenshot_prefix}-unit-wide.png"
        "${screenshot_prefix}-unit-narrow.png"
        "${screenshot_prefix}-layer-moisture-opacity-000.png"
        "${screenshot_prefix}-layer-moisture-opacity-100.png"
        "${screenshot_prefix}-screenshot-filter-preview.png"
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
            "${screenshot_prefix}-filtered-atlas-relief.png"
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

    if ! rg -q 'Automated screenshot filter preview passed' "$main_log"; then
        printf '[ModernAtlas] The fresh client log is missing the screenshot-filter preview success marker.\n' >&2
        exit 1
    fi

    if ! command -v magick >/dev/null 2>&1; then
        printf '[ModernAtlas] ImageMagick is required for the ordinary-world red-border check.\n' >&2
        exit 1
    fi
    # The defect is a continuous red frame around the ordinary world, so the
    # check tests for that shape rather than for an amount of red: scene
    # content legitimately changes how many red pixels touch an edge between
    # two frames. scripts/check-red-border.sh still reports the old per-frame
    # pixel totals as diagnostics.
    if ! "$project_dir/scripts/check-red-border.sh" \
        "${screenshot_prefix}-ordinary-before-atlas.png" \
        "${screenshot_prefix}-ordinary-after-cycle-1.png" \
        "${screenshot_prefix}-ordinary-after-cycle-2.png"
    then
        printf '[ModernAtlas] Ordinary-world red-border check failed.\n' >&2
        exit 1
    fi
    printf '[ModernAtlas] Automated ordinary-world red-border check passed: no continuous red frame on three or more sides in any ordinary-world frame.\n'
fi

if rg -n \
    'AUTOMATED .*(FAILED|ABORTED)|ModernAtlas.*(Critical|Exception|shader failure|disposed)|ModernAtlas.*OpenGL' \
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
