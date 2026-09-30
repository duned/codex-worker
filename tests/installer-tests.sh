#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
installer="$repo_root/packaging/linux/install-server.sh"
worker_installer="$repo_root/packaging/linux/install-worker.sh"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT

bash -n "$installer"
bash -n "$worker_installer"
bash "$installer" --help >"$temp_dir/help.txt"
grep -q -- '--version VERSION' "$temp_dir/help.txt"
grep -q 'latest by default' "$temp_dir/help.txt"

# Match curl | sudo bash argument handling. Resolving the installer location at
# startup must work even though Bash has no BASH_SOURCE entry for stdin scripts.
bash -s -- --help < "$worker_installer" > "$temp_dir/worker-pipe-help.txt"
grep -q -- '--version VERSION' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--server URL' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--capacity 1..8' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--token-file PATH' "$temp_dir/worker-pipe-help.txt"
bash "$worker_installer" --help > "$temp_dir/worker-direct-help.txt"
grep -q -- '--version VERSION' "$temp_dir/worker-direct-help.txt"

if bash "$worker_installer" --capacity 9 >"$temp_dir/invalid-capacity.txt" 2>&1; then
  echo 'Worker installer accepted an out-of-range capacity.' >&2
  exit 1
fi
grep -q 'capacity must be an integer from 1 to 8' "$temp_dir/invalid-capacity.txt"

if bash "$worker_installer" --token secret >"$temp_dir/unsafe-token-option.txt" 2>&1; then
  echo 'Worker installer accepted a bootstrap token command-line option.' >&2
  exit 1
fi
grep -q 'Unknown argument: --token' "$temp_dir/unsafe-token-option.txt"

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

source "$installer"
environment_file="$temp_dir/server.env"
touch "$environment_file"
credential_setup_output="$(ensure_management_token "$environment_file")"
[[ -z "$credential_setup_output" ]] || {
  echo 'Installer printed output while generating the management token.' >&2
  exit 1
}
generated_token="$(sed -n 's/^CODEX_SERVER_MANAGEMENT_TOKEN=//p' "$environment_file")"
[[ "$generated_token" =~ ^[[:xdigit:]]{64}$ ]] || {
  echo 'Installer did not generate a 256-bit management token.' >&2
  exit 1
}
ensure_management_token "$environment_file"
[[ "$(sed -n 's/^CODEX_SERVER_MANAGEMENT_TOKEN=//p' "$environment_file")" == "$generated_token" ]] || {
  echo 'Installer changed the management token on rerun.' >&2
  exit 1
}

operator_environment_file="$temp_dir/operator-server.env"
printf 'SERVER__ListenUrl=http://127.0.0.1:5090\nCODEX_SERVER_MANAGEMENT_TOKEN=operator-token\n' > "$operator_environment_file"
ensure_management_token "$operator_environment_file"
[[ "$(sed -n 's/^CODEX_SERVER_MANAGEMENT_TOKEN=//p' "$operator_environment_file")" == 'operator-token' ]] || {
  echo 'Installer changed an operator-configured management token.' >&2
  exit 1
}

grep -Fq 'chmod 0640 /etc/codex-server/server.env' "$installer"
grep -Fq "sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN" "$installer"

echo 'Installer argument and syntax checks passed.'
