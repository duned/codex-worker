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
trap 'rm -rf -- "$temp_dir"' EXIT
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
  grep -q 'local administration and token commands with sudo' "$temp_dir/helper-nonroot.out"
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
for admin_command in status diagnostics; do
  if [[ $EUID == 0 ]]; then
    bash "$temp_dir/codex-server" "$admin_command"
  else
    bash "$temp_dir/helper-command.sh" "$admin_command"
  fi
  grep -Fxq "$admin_command" "$CODEX_HELPER_ARGUMENTS"
done
for admin_command in projects executions; do
  if [[ $EUID == 0 ]]; then
    bash "$temp_dir/codex-server" "$admin_command" list
  else
    bash "$temp_dir/helper-command.sh" "$admin_command" list
  fi
  grep -Fxq "$admin_command" "$CODEX_HELPER_ARGUMENTS"
done
for admin_root in github credential backup; do
  case "$admin_root" in
    github) admin_args=(github access codex-worker-test) ;;
    credential) admin_args=(credential list) ;;
    backup) admin_args=(backup validate /tmp/codex-server-backup.tar.gz) ;;
  esac
  if [[ $EUID == 0 ]]; then
    bash "$temp_dir/codex-server" "${admin_args[@]}"
  else
    bash "$temp_dir/helper-command.sh" "${admin_args[@]}"
  fi
  grep -Fxq "$admin_root" "$CODEX_HELPER_ARGUMENTS"
  tail -n "${#admin_args[@]}" "$CODEX_HELPER_ARGUMENTS" | diff -u - <(printf '%s\n' "${admin_args[@]}")
done
if [[ $EUID == 0 ]]; then
  bash "$temp_dir/codex-server" config show
else
  bash "$temp_dir/helper-command.sh" config show
fi
tail -n 2 "$CODEX_HELPER_ARGUMENTS" | diff -u - <(printf 'config\nshow\n')
if [[ $EUID == 0 ]]; then
  bash "$temp_dir/codex-server" provision list
else
  sed -n '/^exec systemd-run/,$p' "$temp_dir/codex-server" > "$temp_dir/provision-command.sh"
  bash "$temp_dir/provision-command.sh" provision list
fi
tail -n 3 "$CODEX_HELPER_ARGUMENTS" | diff -u - <(printf '/opt/codex-server/current/CodexServer\nprovision\nlist\n')
sed 's/if \[\[ $EUID -ne 0 \]\]; then/if false; then/' "$temp_dir/codex-server" > "$temp_dir/config-helper"
bash "$temp_dir/config-helper" config set EnableLocalProvisioning true --json
grep -Fxq -- '--property=User=root' "$CODEX_HELPER_ARGUMENTS"
grep -Fxq -- '--property=Environment=CODEX_SERVER_CONFIGURATION_FILE=/etc/codex-server/server.env' "$CODEX_HELPER_ARGUMENTS"
grep -Fxq -- '--property=Environment=HOME=/var/lib/codex-server' "$CODEX_HELPER_ARGUMENTS"
tail -n 6 "$CODEX_HELPER_ARGUMENTS" | diff -u - <(printf '/opt/codex-server/current/CodexServer\nconfig\nset\nEnableLocalProvisioning\ntrue\n--json\n')
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
write_provisioning_sudoers > "$temp_dir/codex-server-provisioning.sudoers"
grep -Fq 'codex-server ALL=(root) NOPASSWD: CODEX_SERVER_PROVISIONING' "$temp_dir/codex-server-provisioning.sudoers"
grep -Fq '/usr/bin/apt-get install -y --no-install-recommends gh' "$temp_dir/codex-server-provisioning.sudoers"
grep -Fq '/usr/bin/apt-get install -y --no-install-recommends git openssh-client' "$temp_dir/codex-server-provisioning.sudoers"
grep -Fq '/usr/bin/npm install --global --prefix /usr/local' "$temp_dir/codex-server-provisioning.sudoers"
! grep -Eq 'NOPASSWD:[[:space:]]*ALL|/usr/bin/apt-get([[:space:]]|$)(,|$)' "$temp_dir/codex-server-provisioning.sudoers"
grep -Fq 'sudoers.d/codex-server-provisioning' "$installer"
grep -Fq 'chown root:root "$temporary_policy"' "$installer"
grep -Fq 'chmod 0440 "$temporary_policy"' "$installer"
grep -Fq 'visudo -cf "$temporary_policy"' "$installer"
if command -v visudo >/dev/null 2>&1; then visudo -cf "$temp_dir/codex-server-provisioning.sudoers" >/dev/null 2>&1; fi

cat > "$temp_dir/bootstrap-server" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state=$CODEX_BOOTSTRAP_STATE
log=$CODEX_BOOTSTRAP_LOG
case "$1 ${2:-}" in
  'config show')
    read -r enabled elevation < "$state"
    printf '{\n  "enableLocalProvisioning": %s,\n  "allowLocalProvisioningElevation": %s\n}\n' "$enabled" "$elevation"
    ;;
  'config set')
    read -r enabled elevation < "$state"
    if [[ $3 == EnableLocalProvisioning ]]; then enabled=$4; else elevation=$4; fi
    printf '%s %s\n' "$enabled" "$elevation" > "$state"
    printf 'config set %s %s\n' "$3" "$4" >> "$log"
    ;;
  'provision create')
    printf '%s\n' "$*" >> "$log"
    [[ $* == 'provision create server github-cli install --allow-elevation --timeout-seconds 600 --json' ]]
    printf '{\n  "id": "0123456789abcdef0123456789abcdef",\n  "status": "Pending"\n}\n'
    ;;
  'provision show')
    count=0; [[ ! -f $CODEX_BOOTSTRAP_POLLS ]] || read -r count < "$CODEX_BOOTSTRAP_POLLS"
    count=$((count + 1)); printf '%s\n' "$count" > "$CODEX_BOOTSTRAP_POLLS"
    status=Running; [[ $count -lt 2 ]] || status=Succeeded
    printf '{\n  "id": "%s",\n  "status": "%s"\n}\n' "$3" "$status"
    ;;
  *) exit 2 ;;
esac
EOF
chmod +x "$temp_dir/bootstrap-server"
cat > "$temp_dir/stubs/systemctl" <<'EOF'
#!/usr/bin/env bash
printf 'systemctl %s\n' "$*" >> "$CODEX_BOOTSTRAP_LOG"
EOF
cat > "$temp_dir/stubs/sleep" <<'EOF'
#!/usr/bin/env bash
:
EOF
chmod +x "$temp_dir/stubs/systemctl" "$temp_dir/stubs/sleep"
export CODEX_BOOTSTRAP_STATE="$temp_dir/bootstrap-state"
export CODEX_BOOTSTRAP_LOG="$temp_dir/bootstrap-log"
export CODEX_BOOTSTRAP_POLLS="$temp_dir/bootstrap-polls"
printf 'false false\n' > "$CODEX_BOOTSTRAP_STATE"
: > "$CODEX_BOOTSTRAP_LOG"
server_helper="$temp_dir/bootstrap-server"
bootstrap_github_cli=true
keep_bootstrap_provisioning_policy=false
bootstrap_restore_needed=false
bootstrap_github_cli_installation
grep -Fxq 'provision create server github-cli install --allow-elevation --timeout-seconds 600 --json' "$CODEX_BOOTSTRAP_LOG"
grep -Fxq 'config set EnableLocalProvisioning true' "$CODEX_BOOTSTRAP_LOG"
grep -Fxq 'config set AllowLocalProvisioningElevation true' "$CODEX_BOOTSTRAP_LOG"
read -r bootstrap_enabled bootstrap_elevation < "$CODEX_BOOTSTRAP_STATE"
[[ $bootstrap_enabled == false && $bootstrap_elevation == false ]]

printf 'false false\n' > "$CODEX_BOOTSTRAP_STATE"
rm -f "$CODEX_BOOTSTRAP_POLLS"
: > "$CODEX_BOOTSTRAP_LOG"
keep_bootstrap_provisioning_policy=true
bootstrap_github_cli_installation
read -r bootstrap_enabled bootstrap_elevation < "$CODEX_BOOTSTRAP_STATE"
[[ $bootstrap_enabled == true && $bootstrap_elevation == true ]]
! grep -Fxq 'config set EnableLocalProvisioning false' "$CODEX_BOOTSTRAP_LOG"

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
