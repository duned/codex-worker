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

# Exercise uninstall and purge in an isolated root with stubbed system tools.
# The explicit test root bypasses the root-only guard and redirects every
# filesystem operation away from the host.
mkdir -p "$temp_dir/stubs"
cat > "$temp_dir/stubs/systemctl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CODEX_UNINSTALL_TEST_ROOT/systemctl.log"
[[ ${CODEX_UNINSTALL_FAIL_ACTION:-} != "${1:-}" ]]
EOF
cat > "$temp_dir/stubs/id" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
cat > "$temp_dir/stubs/getent" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod +x "$temp_dir/stubs/systemctl" "$temp_dir/stubs/id" "$temp_dir/stubs/getent"
export PATH="$temp_dir/stubs:$PATH"

# Keep direct invocation coverage and repeat the same checks with a real pipe.
run_uninstaller() {
  local script=$1
  shift
  if [[ $invocation == stdin ]]; then
    cat "$script" | bash -s -- "$@"
  else
    bash "$script" "$@"
  fi
}

for invocation in file stdin; do
  for component in server worker; do
    component_root="$temp_dir/$invocation-$component"
    test_root="$component_root/root"
    export CODEX_UNINSTALL_TEST_ROOT="$test_root"
    mkdir -p "$test_root/opt/codex-$component" "$test_root/etc/codex-$component" \
      "$test_root/var/lib/codex-$component" "$test_root/var/log/codex-$component" \
      "$test_root/run/codex-$component" "$test_root/etc/systemd/system"
    touch "$test_root/etc/codex-$component/secret.env" "$test_root/var/lib/codex-$component/state.db" \
      "$test_root/opt/codex-$component/binary" "$test_root/etc/systemd/system/codex-$component.service"
    if [[ $component == worker ]]; then
      mkdir -p "$test_root/opt/codex-worker.previous.test" "$test_root/opt/codex-worker.failed.test"
    fi
    run_uninstaller "$repo_root/packaging/linux/uninstall-$component.sh" > "$component_root/default.out"
    [[ ! -e $test_root/opt/codex-$component && ! -e $test_root/etc/systemd/system/codex-$component.service ]] || exit 1
    [[ -e $test_root/etc/codex-$component/secret.env && -e $test_root/var/lib/codex-$component/state.db ]] || {
      echo "$component default uninstall removed retained state." >&2; exit 1;
    }
    grep -q 'Retained .*configuration and credentials' "$component_root/default.out"
    # Purge a complete installation, including binaries and an active unit.
    mkdir -p "$test_root/opt/codex-$component" "$test_root/var/log/codex-$component" "$test_root/run/codex-$component"
    touch "$test_root/opt/codex-$component/binary" "$test_root/etc/systemd/system/codex-$component.service"
    if [[ $component == worker ]]; then
      mkdir -p "$test_root/opt/codex-worker.previous.test" "$test_root/opt/codex-worker.failed.test" "$test_root/opt/codex-worker.next.test"
    fi
    run_uninstaller "$repo_root/packaging/linux/uninstall-$component.sh" --purge > "$component_root/purge.out"
    [[ ! -e $test_root/opt/codex-$component && ! -e $test_root/etc/systemd/system/codex-$component.service &&
       ! -e $test_root/var/log/codex-$component && ! -e $test_root/run/codex-$component ]] || exit 1
    [[ ! -e $test_root/etc/codex-$component && ! -e $test_root/var/lib/codex-$component ]] || {
      echo "$component purge left configuration or persistent state." >&2; exit 1;
    }
    if [[ $component == worker ]]; then
      [[ ! -e $test_root/opt/codex-worker.previous.test && ! -e $test_root/opt/codex-worker.failed.test && ! -e $test_root/opt/codex-worker.next.test ]] || {
        echo 'Worker purge left an installer-created upgrade directory.' >&2; exit 1;
      }
    fi
    grep -q '^stop codex-' "$test_root/systemctl.log"
    grep -q '^disable codex-' "$test_root/systemctl.log"
    grep -q '^daemon-reload$' "$test_root/systemctl.log"
    grep -q 'PURGE: permanently deleting' "$component_root/purge.out"
    run_uninstaller "$repo_root/packaging/linux/uninstall-$component.sh" --purge > "$component_root/repeat.out"
    grep -q 'Already absent' "$component_root/repeat.out"
  done
done

# A service operation failure is reported and leaves installer data untouched.
export CODEX_UNINSTALL_TEST_ROOT="$temp_dir/failure-root"
mkdir -p "$CODEX_UNINSTALL_TEST_ROOT/etc/systemd/system" "$CODEX_UNINSTALL_TEST_ROOT/opt/codex-server"
touch "$CODEX_UNINSTALL_TEST_ROOT/etc/systemd/system/codex-server.service"
if CODEX_UNINSTALL_FAIL_ACTION=stop bash "$server_uninstaller" > "$temp_dir/stop-failure.out" 2>&1; then
  echo 'Server uninstaller ignored a systemd stop failure.' >&2; exit 1
fi
grep -q 'could not stop codex-server.service' "$temp_dir/stop-failure.out"
[[ -e $CODEX_UNINSTALL_TEST_ROOT/etc/systemd/system/codex-server.service && -e $CODEX_UNINSTALL_TEST_ROOT/opt/codex-server ]] || exit 1

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

echo 'Installer and uninstaller argument, preservation, purge, idempotency, and failure checks passed.'
