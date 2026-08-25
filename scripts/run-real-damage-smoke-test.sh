#!/usr/bin/env bash
set -euo pipefail

# This runner deliberately requires an operator-provided copied/disposable
# Vintage Story data directory. It never copies, migrates or edits the normal
# save directory on its own.
project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
game_path="${VINTAGE_STORY_PATH:-/opt/vintagestory}"
data_path="${MODERNATLAS_REAL_DAMAGE_DATA_PATH:-}"
smoke_world="MODERNATLAS_REAL_DAMAGE_TEST"
normal_data_path="${MODERNATLAS_SMOKE_DATA_PATH:-/home/arcylisz/.config/VintagestoryData}"
screenshot_prefix="${MODERNATLAS_SMOKE_SCREENSHOT:-/tmp/modernatlas-real-damage-smoke}"
main_log="$data_path/Logs/client-main.log"
crash_log="$data_path/Logs/client-crash.log"

if [[ -z "$data_path" ]]; then
    printf '[ModernAtlas] Real-damage smoke requires MODERNATLAS_REAL_DAMAGE_DATA_PATH pointing to a disposable copied data directory.\n' >&2
    exit 1
fi
if [[ ! -d "$data_path" ]]; then
    printf '[ModernAtlas] Disposable real-damage data path does not exist: %s\n' "$data_path" >&2
    exit 1
fi
if [[ "$(realpath -m "$data_path")" == "$(realpath -m "$normal_data_path")" ]]; then
    printf '[ModernAtlas] Refusing to run real damage against the normal Vintage Story data path: %s\n' "$data_path" >&2
    exit 1
fi
if [[ -z "$screenshot_prefix" ]]; then
    printf '[ModernAtlas] MODERNATLAS_SMOKE_SCREENSHOT must name an output prefix for the intentional warning capture.\n' >&2
    exit 1
fi

running_clients_on_data_path() {
    local pid args
    for pid in $(pgrep -f 'Vintagestory' 2>/dev/null || true); do
        [[ "$pid" == "$$" ]] && continue
        args="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
        [[ -z "$args" ]] && continue
        [[ "$args" == *"/Vintagestory "* || "$args" == *"/Vintagestory" ]] || continue
        [[ "$args" == *"--dataPath $data_path"* ]] || continue
        printf '    pid %s: %s\n' "$pid" "$args"
    done
}

active_clients="$(running_clients_on_data_path)"
if [[ -n "$active_clients" ]]; then
    printf '[ModernAtlas] Real-damage smoke not started: a Vintage Story client is already using %s.\n' "$data_path" >&2
    printf '%s\n' "$active_clients" >&2
    exit 1
fi

main_lines_before=0
main_signature_before="<missing>"
main_identity_before="<missing>"
if [[ -f "$main_log" ]]; then
    main_lines_before="$(wc -l < "$main_log")"
    main_signature_before="$(stat -c '%d:%i:%s:%Y:%y' "$main_log")"
    main_identity_before="$(stat -c '%d:%i' "$main_log")"
fi
crash_mtime_before=0
if [[ -f "$crash_log" ]]; then
    crash_mtime_before="$(stat -c '%Y' "$crash_log")"
fi

printf '[ModernAtlas] Starting the opt-in real-damage smoke in disposable world %s using %s\n' "$smoke_world" "$data_path"
status=0
env MODERNATLAS_SMOKE_TEST=1 \
    MODERNATLAS_SMOKE_REAL_DAMAGE=1 \
    MODERNATLAS_SMOKE_WORLD="$smoke_world" \
    MODERNATLAS_SMOKE_SCREENSHOT="$screenshot_prefix" \
    MODERNATLAS_SMOKE_SCREENSHOT_MASK="$screenshot_prefix" \
    "$game_path/Vintagestory" \
    --dataPath "$data_path" \
    -o "$smoke_world" || status=$?

if (( status != 0 )); then
    printf '[ModernAtlas] Real-damage smoke process exited with status %d.\n' "$status" >&2
    exit "$status"
fi
if [[ ! -f "$main_log" ]]; then
    printf '[ModernAtlas] Missing disposable client log: %s\n' "$main_log" >&2
    exit 1
fi
main_lines_after="$(wc -l < "$main_log")"
main_signature_after="$(stat -c '%d:%i:%s:%Y:%y' "$main_log")"
if [[ "$main_signature_after" == "$main_signature_before" ]]; then
    printf '[ModernAtlas] Disposable client log was not refreshed by this smoke run.\n' >&2
    exit 1
fi

run_log="$(mktemp /tmp/modernatlas-real-damage-log.XXXXXX)"
main_identity_after="$(stat -c '%d:%i' "$main_log")"
if [[ "$main_identity_before" == "$main_identity_after" ]] \
    && (( main_lines_after > main_lines_before )); then
    tail -n +$((main_lines_before + 1)) "$main_log" > "$run_log"
else
    # The game may rotate, truncate, or recreate client-main.log between two
    # sequential smoke runs. In those cases the current file is the fresh
    # run and slicing by the previous line count would discard its markers.
    cp "$main_log" "$run_log"
fi
cleanup() {
    rm -f "$run_log"
}
trap cleanup EXIT

required_patterns=(
    'AUTOMATED SMOKE HEALTH FIXTURE READY: world=MODERNATLAS_REAL_DAMAGE_TEST; mode=Survival; health=100/100.'
    'REAL DAMAGE SMOKE START'
    'REAL DAMAGE SMOKE WORLD GUARD'
    'REAL DAMAGE APPLIED:.*health=100->99'
    'REAL DAMAGE OBSERVED'
    'REAL DAMAGE HEALTH RESTORED:.*health=100'
    'AUTOMATED ATLAS TWO-CYCLE CHECK PASSED'
    'AUTOMATED WORLD-EXIT CHECK PASSED'
)
for pattern in "${required_patterns[@]}"; do
    if ! rg -q "$pattern" "$run_log"; then
        printf '[ModernAtlas] Required real-damage marker is missing: %s\n' "$pattern" >&2
        exit 1
    fi
done

if rg -q 'AUTOMATED SMOKE GOD MODE' "$run_log"; then
    printf '[ModernAtlas] Real-damage smoke unexpectedly installed or restored the god-mode patch.\n' >&2
    exit 1
fi
if rg -n 'AUTOMATED .*(FAILED|ABORTED)|REAL DAMAGE SMOKE FAILED|ModernAtlas.*(Critical|Exception|shader failure|disposed)|ModernAtlas.*OpenGL' "$run_log"; then
    printf '[ModernAtlas] A failure marker was found in the disposable real-damage log.\n' >&2
    exit 1
fi

warning_screenshot="${screenshot_prefix}-damage-warning-real.png"
if [[ ! -s "$warning_screenshot" ]]; then
    printf '[ModernAtlas] Intentional real-damage warning screenshot is missing or empty: %s\n' "$warning_screenshot" >&2
    exit 1
fi

crash_mtime_after=0
if [[ -f "$crash_log" ]]; then
    crash_mtime_after="$(stat -c '%Y' "$crash_log")"
fi
if (( crash_mtime_after > crash_mtime_before )); then
    printf '[ModernAtlas] Real-damage smoke created or updated client-crash.log.\n' >&2
    exit 1
fi

printf '[ModernAtlas] Real-damage smoke passed: disposable Survival world, no god mode, server-authoritative hit observed, warning captured, and health restored.\n'
