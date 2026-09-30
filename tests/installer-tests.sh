#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
installer="$repo_root/packaging/linux/install-server.sh"
worker_installer="$repo_root/packaging/linux/install-worker.sh"
server_uninstaller="$repo_root/packaging/linux/uninstall-server.sh"
worker_uninstaller="$repo_root/packaging/linux/uninstall-worker.sh"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT

for script in "$installer" "$worker_installer" "$server_uninstaller" "$worker_uninstaller"; do
  bash -n "$script"
  # Do not hide stdin regressions by weakening the scripts' strict mode.
  grep -Fxq 'set -euo pipefail' "$script"
done
bash "$installer" --help >"$temp_dir/help.txt"
grep -q -- '--version VERSION' "$temp_dir/help.txt"
grep -q 'latest by default' "$temp_dir/help.txt"

# Match curl | sudo bash argument handling. Resolving the installer location at
# startup must work even though Bash has no BASH_SOURCE entry for stdin scripts.
cat "$installer" | bash -s -- --help > "$temp_dir/server-pipe-help.txt"
grep -q -- '--version VERSION' "$temp_dir/server-pipe-help.txt"
grep -q 'latest by default' "$temp_dir/server-pipe-help.txt"
cat "$worker_installer" | bash -s -- --help > "$temp_dir/worker-pipe-help.txt"
grep -q -- '--version VERSION' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--server URL' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--capacity 1..8' "$temp_dir/worker-pipe-help.txt"
grep -q -- '--token-file PATH' "$temp_dir/worker-pipe-help.txt"
bash "$worker_installer" --help > "$temp_dir/worker-direct-help.txt"
grep -q -- '--version VERSION' "$temp_dir/worker-direct-help.txt"

bash "$server_uninstaller" --help > "$temp_dir/server-uninstall-help.txt"
grep -q -- '--purge' "$temp_dir/server-uninstall-help.txt"
bash "$worker_uninstaller" --help > "$temp_dir/worker-uninstall-help.txt"
grep -q -- '--purge' "$temp_dir/worker-uninstall-help.txt"
if bash "$server_uninstaller" --unknown > "$temp_dir/unknown-uninstall-option.txt" 2>&1; then
  echo 'Server uninstaller accepted an unknown option.' >&2; exit 1
fi
grep -q 'unknown option' "$temp_dir/unknown-uninstall-option.txt"

bash "$repo_root/tests/uninstaller-lifecycle-tests.sh"

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
# Exercise the exact helper emitted by the installer, with systemd isolated.
mkdir -p "$temp_dir/stubs"
export PATH="$temp_dir/stubs:$PATH"
write_operator_helper > "$temp_dir/codex-server"
bash -n "$temp_dir/codex-server"
cat > "$temp_dir/stubs/systemd-run" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$CODEX_HELPER_ARGUMENTS"
EOF
chmod +x "$temp_dir/stubs/systemd-run"
export CODEX_HELPER_ARGUMENTS="$temp_dir/helper-arguments"
# Installer/helper require root; the isolated invocation performs no host writes.
if [[ $EUID == 0 ]]; then
  bash "$temp_dir/codex-server" worker-token create
else
  if bash "$temp_dir/codex-server" worker-token create > "$temp_dir/helper-nonroot.out" 2>&1; then
    echo 'Operator helper accepted execution without root.' >&2; exit 1
  fi
  grep -q 'sudo codex-server worker-token create' "$temp_dir/helper-nonroot.out"
  # Exercise the emitted exec command independently of its root-only guard.
  sed -n '/^exec systemd-run/,$p' "$temp_dir/codex-server" > "$temp_dir/helper-command.sh"
  bash "$temp_dir/helper-command.sh" worker-token create
fi
cat > "$temp_dir/expected-helper-arguments" <<'EOF'
--quiet
--wait
--pipe
--collect
--property=User=codex-server
--property=Group=codex-server
--property=Environment=CODEX_SERVER_OPERATOR_SERVICE_CONTEXT=1
--property=EnvironmentFile=/etc/codex-server/server.env
--property=WorkingDirectory=/opt/codex-server/current
--property=UMask=0077
/opt/codex-server/current/CodexServer
worker-token
create
EOF
diff -u "$temp_dir/expected-helper-arguments" "$CODEX_HELPER_ARGUMENTS"
# Help/version bypass sudo and systemd and dispatch directly to the executable.
cat > "$temp_dir/server-cli" <<'EOF'
#!/usr/bin/env bash
printf 'CLI %s\n' "$@"
EOF
chmod +x "$temp_dir/server-cli"
sed "s|/opt/codex-server/current/CodexServer|$temp_dir/server-cli|g" "$temp_dir/codex-server" > "$temp_dir/help-helper"
rm "$CODEX_HELPER_ARGUMENTS"
for cli_option in --help -h --version; do
  [[ $(bash "$temp_dir/help-helper" "$cli_option") == "CLI $cli_option" ]]
  [[ ! -e $CODEX_HELPER_ARGUMENTS ]]
done
# The helper never sources the secret-bearing systemd environment as shell code.
! grep -Eq '(^|[[:space:]])(source|\.) /etc/codex-server/server.env' "$temp_dir/codex-server"
grep -Fq 'Server__DataDirectory=/var/lib/codex-server' "$installer"

fresh_environment="$temp_dir/fresh-server.env"
ensure_server_environment "$fresh_environment"
grep -Fxq 'Server__DataDirectory=/var/lib/codex-server' "$fresh_environment"
[[ $(stat -c '%a' "$fresh_environment") == 600 ]] || exit 1
cp "$fresh_environment" "$temp_dir/fresh-original.env"
ensure_server_environment "$fresh_environment"
cmp "$fresh_environment" "$temp_dir/fresh-original.env"
legacy_environment="$temp_dir/legacy-server.env"
printf 'CODEX_SERVER_MANAGEMENT_TOKEN=existing\n' > "$legacy_environment"
ensure_server_environment "$legacy_environment"
! grep -q 'Server__DataDirectory' "$legacy_environment"
custom_environment="$temp_dir/custom-server.env"
printf 'Server__DataDirectory=/srv/custom\nServer__DatabasePath=custom.db\nCODEX_SERVER_MANAGEMENT_TOKEN=existing\n' > "$custom_environment"
cp "$custom_environment" "$temp_dir/custom-original.env"
ensure_server_environment "$custom_environment"
cmp "$custom_environment" "$temp_dir/custom-original.env"

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

echo 'Installer and uninstaller argument, preservation, purge, idempotency, and failure checks passed.'

bash "$repo_root/tests/worker-installer-lifecycle-tests.sh"
