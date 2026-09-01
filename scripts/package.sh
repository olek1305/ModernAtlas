#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release_dir="$project_dir/Releases"
package_path="$release_dir/modernatlas_0.6.9.zip"

cd "$project_dir"

# Vintage Story performs the pre-world mod handshake from these manifest
# fields. Keep the multiplayer compatibility gate in the package metadata:
# an installed server must require the same ModernAtlas network version on
# the client, while requiredOnServer=false keeps the client-only installation
# compatible with servers that do not install ModernAtlas.
if ! command -v jq >/dev/null 2>&1; then
  printf 'jq is required to validate the ModernAtlas multiplayer manifest.\n' >&2
  exit 1
fi
manifest_gate='
  .modid == "modernatlas"
  and .version == "0.6.9"
  and .networkVersion == "0.6.9"
  and .side == "Universal"
  and .requiredOnClient == true
  and .requiredOnServer == false
'
if ! jq -e "$manifest_gate" modinfo.json >/dev/null; then
  printf 'modinfo.json failed the ModernAtlas 0.6.9 multiplayer compatibility check.\n' >&2
  printf 'Expected networkVersion=0.6.9, requiredOnClient=true and requiredOnServer=false.\n' >&2
  exit 1
fi

mkdir -p "$release_dir"
rm -f "$package_path"

dotnet build -c Release

zip -q -j "$package_path" \
  modinfo.json LICENSE NOTICE.md bin/Release/net10.0/ModernAtlas.dll
zip -q -r "$package_path" assets

if ! unzip -p "$package_path" modinfo.json | jq -e "$manifest_gate" >/dev/null; then
  printf 'The generated package failed the ModernAtlas multiplayer manifest check.\n' >&2
  exit 1
fi

printf 'Created %s\n' "$package_path"
