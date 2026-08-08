#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release_dir="$project_dir/Releases"
package_path="$release_dir/modernatlas_0.4.1.zip"

mkdir -p "$release_dir"
rm -f "$package_path"

cd "$project_dir"

if [[ ! -f "$project_dir/bin/Release/net10.0/ModernAtlas.dll" ]]; then
  printf 'Missing compiled DLL. Run: dotnet build -c Release\n' >&2
  exit 1
fi

zip -q -j "$package_path" \
  modinfo.json LICENSE NOTICE.md bin/Release/net10.0/ModernAtlas.dll
zip -q -r "$package_path" assets

printf 'Created %s\n' "$package_path"
