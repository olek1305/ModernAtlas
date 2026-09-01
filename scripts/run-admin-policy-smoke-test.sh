#!/usr/bin/env bash
set -euo pipefail

# This is deliberately separate from run-smoke-test.sh. It launches a real
# dedicated server and multiplayer client, but does not render the atlas or run
# the ordinary post-change smoke suite.
project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
game_path="${VINTAGE_STORY_PATH:-/opt/vintagestory}"
server_data="${MODERNATLAS_ADMIN_SMOKE_SERVER_DATA_PATH:-/tmp/modernatlas-policy-server}"
client_data="${MODERNATLAS_ADMIN_SMOKE_CLIENT_DATA_PATH:-/tmp/modernatlas-policy-client}"
port="${MODERNATLAS_ADMIN_SMOKE_PORT:-42431}"
server_config="$server_data/ModConfig/ModernAtlasServer.json"
server_log="$server_data/Logs/server-main.log"
client_log="$client_data/Logs/client-main.log"
client_crash_log="$client_data/Logs/client-crash.log"
release="$project_dir/Releases/modernatlas_0.6.9.zip"

if [[ ! -d "$server_data" || ! -d "$client_data" ]]; then
    printf '[ModernAtlas] Admin policy smoke requires existing disposable server and client data directories.\n' >&2
    exit 1
fi
normal_data="$(realpath -m "${XDG_CONFIG_HOME:-$HOME/.config}/VintagestoryData")"
if [[ "$(realpath -m "$server_data")" == "$normal_data" \
    || "$(realpath -m "$client_data")" == "$normal_data" \
    || "$(realpath -m "$server_data")" == "$(realpath -m "$client_data")" ]]
then
    printf '[ModernAtlas] Refusing to use the normal data directory or one shared server/client directory.\n' >&2
    exit 1
fi
if [[ ! -f "$server_config" || ! -f "$server_data/serverconfig.json" ]]; then
    printf '[ModernAtlas] Missing disposable server configuration.\n' >&2
    exit 1
fi
if ! command -v jq >/dev/null 2>&1; then
    printf '[ModernAtlas] jq is required for the admin policy persistence checks.\n' >&2
    exit 1
fi
if ! jq -e '(.PlayerOverrides // {}) | length == 0' "$server_config" >/dev/null; then
    printf '[ModernAtlas] The fixture must begin with an empty PlayerOverrides object.\n' >&2
    exit 1
fi
if ! jq -e 'any(.[]; .RoleCode == "admin" or .RoleCode == "root")' \
    "$server_data/Playerdata/playerdata.json" >/dev/null
then
    printf '[ModernAtlas] The fixture needs a persisted admin/root player for the real controlserver check.\n' >&2
    exit 1
fi
admin_uid="$(jq -r 'first(.[] | select(.RoleCode == "admin" or .RoleCode == "root")) | .PlayerUID' \
    "$server_data/Playerdata/playerdata.json")"

running_on_data_path() {
    local path="$1" pid args
    for pid in $(pgrep -f 'Vintagestory' 2>/dev/null || true); do
        [[ "$pid" == "$$" ]] && continue
        args="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
        [[ "$args" == *"--dataPath $path"* ]] || continue
        printf '    pid %s: %s\n' "$pid" "$args"
    done
}

active="$(running_on_data_path "$server_data")$(running_on_data_path "$client_data")"
if [[ -n "$active" ]]; then
    printf '[ModernAtlas] Admin policy smoke not started: a fixture process is already running.\n%s\n' "$active" >&2
    exit 1
fi

original_effective="$(jq -c '[
    .AllowClientSettings,
    .AllowHideVegetation,
    .CheatModeAllowed,
    (.LivingEntitiesEnabled and .ShowPlayers),
    (.LivingEntitiesEnabled and .ShowAnimals),
    (.LivingEntitiesEnabled and .ShowMobs),
    (.LivingEntitiesEnabled and .ShowNpcs)
]' "$server_config")"
client_log_mtime=0
[[ -f "$client_log" ]] && client_log_mtime="$(stat -c '%Y' "$client_log")"
crash_log_mtime=0
[[ -f "$client_crash_log" ]] && crash_log_mtime="$(stat -c '%Y' "$client_crash_log")"

"$project_dir/scripts/package.sh"
mkdir -p "$server_data/Mods" "$client_data/Mods"
cp "$release" "$server_data/Mods/modernatlas_0.6.9.zip"
cp "$release" "$client_data/Mods/modernatlas_0.6.9.zip"

control_fifo="$(mktemp -u /tmp/modernatlas-admin-policy-control.XXXXXX)"
server_stdout="$(mktemp /tmp/modernatlas-admin-policy-server.XXXXXX.log)"
client_stdout="$(mktemp /tmp/modernatlas-admin-policy-client.XXXXXX.log)"
mkfifo "$control_fifo"
exec 9<>"$control_fifo"
server_pid=""
client_pid=""

cleanup() {
    set +e
    if [[ -n "$client_pid" ]] && kill -0 "$client_pid" 2>/dev/null; then
        kill -TERM "$client_pid" 2>/dev/null
        wait "$client_pid" 2>/dev/null
    fi
    if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
        printf '/stop\n' >&9
        for _ in {1..80}; do
            kill -0 "$server_pid" 2>/dev/null || break
            sleep 0.25
        done
        if kill -0 "$server_pid" 2>/dev/null; then
            kill -TERM "$server_pid" 2>/dev/null
        fi
        wait "$server_pid" 2>/dev/null
    fi
    exec 9>&-
    rm -f "$control_fifo" "$server_stdout" "$client_stdout"
}
trap cleanup EXIT INT TERM

printf '[ModernAtlas] Starting separate admin policy server/client smoke on port %s.\n' "$port"
env -u MODERNATLAS_SMOKE_TEST \
    MODERNATLAS_ADMIN_POLICY_SMOKE_TEST=1 \
    "$game_path/VintagestoryServer" \
    --dataPath "$server_data" \
    --port "$port" \
    <"$control_fifo" >"$server_stdout" 2>&1 &
server_pid=$!

ready=false
for _ in {1..480}; do
    if rg -q "Dedicated Server now running on Port $port" "$server_stdout"; then
        ready=true
        break
    fi
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.25
done
if ! $ready; then
    printf '[ModernAtlas] Dedicated server did not become ready.\n' >&2
    sed -n '1,240p' "$server_stdout" >&2
    exit 1
fi

env -u MODERNATLAS_SMOKE_TEST \
    MODERNATLAS_ADMIN_POLICY_SMOKE_TEST=1 \
    "$game_path/Vintagestory" \
    --dataPath "$client_data" \
    --connect "127.0.0.1:$port" \
    >"$client_stdout" 2>&1 &
client_pid=$!

client_finished=false
for _ in {1..720}; do
    if ! kill -0 "$client_pid" 2>/dev/null; then
        client_finished=true
        break
    fi
    sleep 0.25
done
if ! $client_finished; then
    printf '[ModernAtlas] Client automation timed out.\n' >&2
    exit 1
fi
wait "$client_pid"
client_status=$?
client_pid=""
if (( client_status != 0 )); then
    printf '[ModernAtlas] Client exited with status %d.\n' "$client_status" >&2
    exit "$client_status"
fi

printf '/stop\n' >&9
for _ in {1..120}; do
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.25
done
if kill -0 "$server_pid" 2>/dev/null; then
    printf '[ModernAtlas] Dedicated server did not stop cleanly.\n' >&2
    exit 1
fi
wait "$server_pid"
server_pid=""

if [[ ! -f "$client_log" || "$(stat -c '%Y' "$client_log")" -le "$client_log_mtime" ]]; then
    printf '[ModernAtlas] The client log was not refreshed.\n' >&2
    exit 1
fi
required_client_patterns=(
    'ADMIN POLICY SMOKE START: separate dedicated-server/client GUI test; main atlas smoke is disabled.'
    'Applied server policy: client Settings allowed=True; Hide vegetation allowed=True; Cheat Mode allowed=True; live 3D models players=True, animals=True, mobs=True, npcs=True.'
    'ADMIN POLICY SMOKE PASSED: Target, all seven policy selectors, Apply policy, Inherit all, Close, server persistence, per-player isolation and immediate policy refresh were verified.'
    'World leave received'
)
for pattern in "${required_client_patterns[@]}"; do
    if ! rg -q "$pattern" "$client_log"; then
        printf '[ModernAtlas] Missing client marker: %s\n' "$pattern" >&2
        exit 1
    fi
done
if rg -q 'ADMIN POLICY SMOKE FAILED|AUTOMATED ATLAS .*PASSED' "$client_log"; then
    printf '[ModernAtlas] Failure marker or main atlas smoke marker appeared in the separate test.\n' >&2
    exit 1
fi
if [[ -f "$client_crash_log" \
    && "$(stat -c '%Y' "$client_crash_log")" -gt "$crash_log_mtime" ]]
then
    printf '[ModernAtlas] The admin policy smoke updated client-crash.log.\n' >&2
    exit 1
fi
if [[ ! -f "$server_log" ]] \
    || ! rg -q 'updated the atlas policy for server defaults' "$server_log" \
    || ! rg -q "updated the atlas policy for $admin_uid" "$server_log"
then
    printf '[ModernAtlas] Server mutation markers are missing.\n' >&2
    exit 1
fi
if ! jq -e '(.PlayerOverrides // {}) | length == 0' "$server_config" >/dev/null; then
    printf '[ModernAtlas] Inherit all did not remove the persisted player exception.\n' >&2
    exit 1
fi
restored_effective="$(jq -c '[
    .AllowClientSettings,
    .AllowHideVegetation,
    .CheatModeAllowed,
    (.LivingEntitiesEnabled and .ShowPlayers),
    (.LivingEntitiesEnabled and .ShowAnimals),
    (.LivingEntitiesEnabled and .ShowMobs),
    (.LivingEntitiesEnabled and .ShowNpcs)
]' "$server_config")"
if [[ "$restored_effective" != "$original_effective" ]]; then
    printf '[ModernAtlas] Server defaults were not restored: before=%s after=%s\n' \
        "$original_effective" "$restored_effective" >&2
    exit 1
fi

trap - EXIT INT TERM
cleanup
printf '[ModernAtlas] Separate admin policy server/client smoke passed.\n'
