#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
installer="$repo_root/packaging/linux/install-server.sh"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT

bash -n "$installer"
bash "$installer" --help >"$temp_dir/help.txt"
grep -q -- '--version VERSION' "$temp_dir/help.txt"
grep -q 'latest by default' "$temp_dir/help.txt"

if bash "$installer" --version invalid >"$temp_dir/invalid.txt" 2>&1; then
  echo 'Installer accepted an invalid version.' >&2
  exit 1
fi
grep -q 'Version must be a release version' "$temp_dir/invalid.txt"

if bash "$installer" --unknown >"$temp_dir/unknown.txt" 2>&1; then
  echo 'Installer accepted an unknown option.' >&2
  exit 1
fi
grep -q 'Unknown option' "$temp_dir/unknown.txt"

echo 'Installer argument and syntax checks passed.'
