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
env MODERNATLAS_SMOKE_TEST=1 \
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
    )
    for screenshot in "${required_screenshots[@]}"; do
        if [[ ! -s "$screenshot" ]]; then
            printf '[ModernAtlas] Required smoke screenshot is missing or empty: %s\n' "$screenshot" >&2
            exit 1
        fi
    done
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
