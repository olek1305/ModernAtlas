#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
game_dir="${VINTAGE_STORY_PATH:-/opt/vintagestory}"
dotnet_cmd="${DOTNET_CMD:-dotnet}"

if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  local_dotnet="${HOME}/.local/share/guglemap-dotnet/dotnet"
  if [[ -x "$local_dotnet" ]]; then
    dotnet_cmd="$local_dotnet"
  else
    printf 'Missing .NET 10 SDK. See README.md.\n' >&2
    exit 1
  fi
fi

"$dotnet_cmd" build "$project_dir/VoxelAtlas.csproj" -c Release \
  -p:VintageStoryPath="$game_dir"
"$project_dir/scripts/package.sh"
exec "$game_dir/Vintagestory" --addModPath "$project_dir/Releases" "$@"
