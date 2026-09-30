#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts}"
version="$(dotnet msbuild "$repo_root/src/CodexWorker/CodexWorker.csproj" -nologo -m:1 -getProperty:Version)"

if [[ -z "$version" ]]; then
  echo "Could not read the application version from MSBuild." >&2
  exit 1
fi

if [[ "${1:-}" == "--version" ]]; then
  printf '%s\n' "$version"
  exit 0
fi

case "$output_dir" in
  /*) ;;
  *) output_dir="$repo_root/$output_dir" ;;
esac

mkdir -p "$output_dir"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

package_app() {
  local name="$1"
  local project="$2"
  local package_dir="$work_dir/$name"
  local archive="$output_dir/$name-$version-linux-x64.tar.gz"

  mkdir -p "$package_dir"
  dotnet publish "$repo_root/$project" \
    --configuration Release \
    --maxcpucount:1 \
    --runtime linux-x64 \
    --self-contained true \
    --output "$package_dir"
  printf '%s\n' "$version" > "$package_dir/VERSION"
  tar -czf "$archive" -C "$package_dir" .
}

package_app codex-server src/CodexServer/CodexServer.csproj
package_app codex-worker src/CodexWorker/CodexWorker.csproj

(
  cd "$output_dir"
  sha256sum "codex-server-$version-linux-x64.tar.gz" "codex-worker-$version-linux-x64.tar.gz" > checksums.txt
)

printf 'Created release artifacts in %s\n' "$output_dir"
