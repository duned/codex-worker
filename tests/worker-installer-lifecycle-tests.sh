#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf -- "$test_dir"' EXIT
mkdir -p "$test_dir/bin" "$test_dir/artifact"
real_install=$(command -v install)
export REAL_INSTALL="$real_install"
# Run a relocated copy, never the root installer against the host. Production
# has no test-root override. Only paths, OS/root checks and terminal prompts move.
prepare_root() {
  local tty_rewrite='s|/dev/tty|/nonexistent-test-terminal|g'
  if [[ ${2:-} == tty ]]; then tty_rewrite=''; fi
  export LIFECYCLE_ROOT="$test_dir/$1"
  mkdir -p "$LIFECYCLE_ROOT/opt" "$LIFECYCLE_ROOT/etc/systemd/system" \
    "$LIFECYCLE_ROOT/var/log" "$LIFECYCLE_ROOT/scripts"
  printf 'ID=ubuntu\nVERSION_ID=24.04\n' > "$LIFECYCLE_ROOT/etc/os-release"
  sed -e "s|/opt/codex-worker|$LIFECYCLE_ROOT/opt/codex-worker|g" \
    -e "s|/etc/codex-worker|$LIFECYCLE_ROOT/etc/codex-worker|g" \
    -e "s|/var/lib/codex-worker|$LIFECYCLE_ROOT/var/lib/codex-worker|g" \
    -e "s|/var/log/codex-worker|$LIFECYCLE_ROOT/var/log/codex-worker|g" \
    -e "s|/etc/systemd/system|$LIFECYCLE_ROOT/etc/systemd/system|g" \
    -e "s|/usr/local/bin|$LIFECYCLE_ROOT/usr/local/bin|g" \
    -e "s|/etc/os-release|$LIFECYCLE_ROOT/etc/os-release|g" \
    -e 's/${EUID} -ne 0/1 -ne 1/g' -e "$tty_rewrite" \
    "$repo_root/packaging/linux/install-worker.sh" > "$LIFECYCLE_ROOT/scripts/install-worker.sh"
  sed -e "s|/opt/codex-worker|$LIFECYCLE_ROOT/opt/codex-worker|g" \
    -e "s|/etc/codex-worker|$LIFECYCLE_ROOT/etc/codex-worker|g" \
    "$repo_root/packaging/linux/codex-worker.service" > "$LIFECYCLE_ROOT/scripts/codex-worker.service"
}
cat > "$test_dir/artifact/CodexWorker" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[[ $1 != --help ]] || { [[ ${APPHOST_FAIL:-false} == false ]]; exit; }
[[ $1 == register ]]
printf 'register\n' >> "$LIFECYCLE_ROOT/events"
while (($#)); do
  if [[ $1 == --identity-file ]]; then identity=$2; shift; fi
  shift
done
IFS= read -r token
[[ $token == test-token ]]
printf 'stable-identity\n' > "$identity"
printf 'durable-recovery-credential\n' > "${identity}.credential"
[[ ${REGISTRATION_FAIL:-false} == false ]] || exit 1
touch "$LIFECYCLE_ROOT/registered"
STUB
chmod +x "$test_dir/artifact/CodexWorker"
printf '1.2.3\n' > "$test_dir/artifact/VERSION"
tar -czf "$test_dir/codex-worker-1.2.3-linux-x64.tar.gz" -C "$test_dir/artifact" .
(cd "$test_dir" && sha256sum codex-worker-1.2.3-linux-x64.tar.gz > checksums.txt)
export LIFECYCLE_ASSETS="$test_dir"
cat > "$test_dir/bin/curl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
for argument in "$@"; do
  if [[ $argument == https://* ]]; then url=$argument; fi
done
while (($#)); do
  if [[ $1 == --output ]]; then output=$2; shift; fi
  shift
done
case $url in
  */worker.managed.example.yml) cp "$LIFECYCLE_TEMPLATE" "$output" ;;
  *) cp "$LIFECYCLE_ASSETS/${url##*/}" "$output" ;;
esac
STUB
cat > "$test_dir/bin/install" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
args=()
while (($#)); do
  case $1 in -o|-g) shift 2 ;; *) args+=("$1"); shift ;; esac
done
"$REAL_INSTALL" "${args[@]}"
STUB
cat > "$test_dir/bin/runuser" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
shift 3
"$@"
STUB
cat > "$test_dir/bin/systemctl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$LIFECYCLE_ROOT/events"
case $1 in
  is-active) [[ -f $LIFECYCLE_ROOT/active || -f $LIFECYCLE_ROOT/restarting ]] ;;
  is-enabled) [[ -f $LIFECYCLE_ROOT/enabled ]] ;;
  cat) [[ -f $LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service ]] ;;
  stop)
    [[ -f $LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service ]] || exit 5
    rm -f "$LIFECYCLE_ROOT/active" "$LIFECYCLE_ROOT/restarting" ;;
  disable) rm -f "$LIFECYCLE_ROOT/enabled" ;;
  reset-failed) rm -f "$LIFECYCLE_ROOT/broken" ;;
  start|enable)
    [[ -x $LIFECYCLE_ROOT/opt/codex-worker/CodexWorker &&
       -f $LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service ]] || {
      touch "$LIFECYCLE_ROOT/broken" "$LIFECYCLE_ROOT/restarting"; exit 1;
    }
    [[ -f $LIFECYCLE_ROOT/registered || -f $LIFECYCLE_ROOT/previous-runtime ]] || exit 1
    [[ ${START_FAIL:-false} == false || $(cat "$LIFECYCLE_ROOT/opt/codex-worker/VERSION") == previous ]] || { touch "$LIFECYCLE_ROOT/restarting"; exit 1; }
    touch "$LIFECYCLE_ROOT/active"
    [[ $1 != enable ]] || touch "$LIFECYCLE_ROOT/enabled"
    ;;
  daemon-reload) ;;
  *) exit 99 ;;
esac
STUB
for command in chown getent id; do
  printf '#!/usr/bin/env bash\nexit 0\n' > "$test_dir/bin/$command"
done
printf '#!/usr/bin/env bash\necho x86_64\n' > "$test_dir/bin/uname"
# Execution dependencies must never be invoked by installation/bootstrap.
for execution_tool in dotnet gh codex node; do
  cat > "$test_dir/bin/$execution_tool" <<'STUB'
#!/usr/bin/env bash
echo "installer invoked an execution dependency" >&2
exit 99
STUB
done
chmod +x "$test_dir/bin/"*
export PATH="$test_dir/bin:$PATH"
export LIFECYCLE_TEMPLATE="$repo_root/packaging/linux/worker.managed.example.yml"
run_install() {
  CODEX_WORKER_BOOTSTRAP_TOKEN=test-token bash "$LIFECYCLE_ROOT/scripts/install-worker.sh" \
    --version 1.2.3 --server https://server.example --register --start > "$LIFECYCLE_ROOT/output" 2>&1
}
run_interactive_install() {
  python3 - "$LIFECYCLE_ROOT/scripts/install-worker.sh" > "$LIFECYCLE_ROOT/output" 2>&1 <<'PY'
import os
import pty
import select
import signal
import sys
import time

pid, terminal = pty.fork()
if pid == 0:
    os.execvpe("bash", ["bash", sys.argv[1], "--version", "1.2.3", "--server",
                         "https://server.example", "--capacity", "1", "--register", "--start"], os.environ)

output = bytearray()
token_sent = False
deadline = time.monotonic() + 30
status = None
while time.monotonic() < deadline:
    ready, _, _ = select.select([terminal], [], [], 0.1)
    if ready:
        try:
            chunk = os.read(terminal, 4096)
        except OSError:
            chunk = b""
        if not chunk:
            break
        output.extend(chunk)
        if not token_sent and b"Bootstrap token: " in output:
            os.write(terminal, b"test-token\n")
            token_sent = True
    waited, child_status = os.waitpid(pid, os.WNOHANG)
    if waited:
        status = child_status
        break
if status is None:
    os.kill(pid, signal.SIGKILL)
    _, status = os.waitpid(pid, 0)
sys.stdout.buffer.write(output)
if not token_sent or not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
    raise SystemExit(1)
PY
}
assert_clean_failure() {
  [[ ! -e $LIFECYCLE_ROOT/active && ! -e $LIFECYCLE_ROOT/restarting &&
     ! -e $LIFECYCLE_ROOT/broken && ! -e $LIFECYCLE_ROOT/enabled &&
     ! -e $LIFECYCLE_ROOT/opt/codex-worker &&
     ! -e $LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service ]]
  [[ -f $LIFECYCLE_ROOT/etc/codex-worker/worker.yml &&
     -f $LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id.credential ]]
  grep -q 'Retry: rerun install-worker.sh' "$LIFECYCLE_ROOT/output"
  ! grep -q test-token "$LIFECYCLE_ROOT/output"
}
prepare_root success
run_install || { cat "$LIFECYCLE_ROOT/output"; exit 1; }
[[ -f $LIFECYCLE_ROOT/active && -f $LIFECYCLE_ROOT/enabled ]]
[[ $(readlink "$LIFECYCLE_ROOT/usr/local/bin/codex-worker") == "$LIFECYCLE_ROOT/opt/codex-worker/CodexWorker" ]]
[[ $(grep -n '^register$' "$LIFECYCLE_ROOT/events" | cut -d: -f1) -lt \
   $(grep -n '^enable --now' "$LIFECYCLE_ROOT/events" | cut -d: -f1) ]]
grep -Fxq "ExecStart=$LIFECYCLE_ROOT/opt/codex-worker/CodexWorker run --config $LIFECYCLE_ROOT/etc/codex-worker/worker.yml" \
  "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service"
# The fake Worker accepts only --help and register: any Codex preflight gate
# would fail this clean installation instead of leaving registration coherent.
grep -q 'Worker registration completed' "$LIFECYCLE_ROOT/output"
grep -q 'do not imply execution readiness' "$LIFECYCLE_ROOT/output"
grep -q 'not installed or configured automatically' "$LIFECYCLE_ROOT/output"
[[ -f $LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id.credential ]]
for initial_state in clean broken; do
  prepare_root "$initial_state"
  if [[ $initial_state == broken ]]; then
    mkdir -p "$LIFECYCLE_ROOT/opt/codex-worker"
    cp "$LIFECYCLE_ROOT/scripts/codex-worker.service" "$LIFECYCLE_ROOT/etc/systemd/system/"
    touch "$LIFECYCLE_ROOT/restarting" "$LIFECYCLE_ROOT/broken" "$LIFECYCLE_ROOT/enabled"
  fi
  if REGISTRATION_FAIL=true run_install; then echo 'Registration failure accepted'; exit 1; fi
  assert_clean_failure
  ! grep -Eq '^(start|enable) ' "$LIFECYCLE_ROOT/events"
  identity=$(cat "$LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id")
  run_install || { cat "$LIFECYCLE_ROOT/output"; exit 1; }
  [[ -f $LIFECYCLE_ROOT/active && -f $LIFECYCLE_ROOT/enabled ]]
  [[ $(cat "$LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id") == "$identity" ]]
done
prepare_root noninteractive-token
if bash "$LIFECYCLE_ROOT/scripts/install-worker.sh" --version 1.2.3 --server https://server.example \
  --register --start > "$LIFECYCLE_ROOT/output" 2>&1; then
  echo 'Non-interactive registration without a token source was accepted.' >&2; exit 1
fi
grep -q 'CODEX_WORKER_BOOTSTRAP_TOKEN or --token-file PATH' "$LIFECYCLE_ROOT/output"
! grep -q test-token "$LIFECYCLE_ROOT/output"

prepare_root token-file
printf 'test-token\n' > "$LIFECYCLE_ROOT/bootstrap-token"
chmod 0600 "$LIFECYCLE_ROOT/bootstrap-token"
bash "$LIFECYCLE_ROOT/scripts/install-worker.sh" --version 1.2.3 --server https://server.example \
  --capacity 1 --register --start --token-file "$LIFECYCLE_ROOT/bootstrap-token" \
  > "$LIFECYCLE_ROOT/output" 2>&1 || { cat "$LIFECYCLE_ROOT/output"; exit 1; }
grep -q 'Worker registration completed' "$LIFECYCLE_ROOT/output"
! grep -q test-token "$LIFECYCLE_ROOT/output"

prepare_root interactive-token tty
run_interactive_install || { cat "$LIFECYCLE_ROOT/output"; exit 1; }
grep -q 'Bootstrap token:' "$LIFECYCLE_ROOT/output"
grep -q 'Worker registration completed' "$LIFECYCLE_ROOT/output"
! grep -q test-token "$LIFECYCLE_ROOT/output"

prepare_root start-failure
if START_FAIL=true run_install; then echo 'Start failure accepted'; exit 1; fi
assert_clean_failure
for prerequisite in apphost unit-path; do
  prepare_root "$prerequisite"
  if [[ $prerequisite == unit-path ]]; then
    sed -i 's|/CodexWorker |/missing-worker |' "$LIFECYCLE_ROOT/scripts/codex-worker.service"
    if run_install; then exit 1; fi
  else
    if APPHOST_FAIL=true run_install; then exit 1; fi
  fi
  [[ ! -e $LIFECYCLE_ROOT/opt/codex-worker &&
     ! -e $LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service &&
     ! -e $LIFECYCLE_ROOT/active && ! -e $LIFECYCLE_ROOT/restarting ]]
  ! grep -Eq '^(register|start|enable)' "$LIFECYCLE_ROOT/events"
done
# Failure on an upgrade restores a runnable previous installation and its YAML.
prepare_root upgrade
mkdir -p "$LIFECYCLE_ROOT/opt/codex-worker" "$LIFECYCLE_ROOT/etc/codex-worker"
cp "$test_dir/artifact/CodexWorker" "$LIFECYCLE_ROOT/opt/codex-worker/"
printf 'previous\n' > "$LIFECYCLE_ROOT/opt/codex-worker/VERSION"
cp "$LIFECYCLE_TEMPLATE" "$LIFECYCLE_ROOT/etc/codex-worker/worker.yml"
cp "$LIFECYCLE_ROOT/scripts/codex-worker.service" "$LIFECYCLE_ROOT/etc/systemd/system/"
# Start from the obsolete pre-17.x service command and verify upgrade migration.
sed -i 's| run --config||' "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service"
touch "$LIFECYCLE_ROOT/active" "$LIFECYCLE_ROOT/enabled" "$LIFECYCLE_ROOT/previous-runtime"
if REGISTRATION_FAIL=true run_install; then exit 1; fi
[[ -f $LIFECYCLE_ROOT/active && -f $LIFECYCLE_ROOT/enabled ]]
[[ $(cat "$LIFECYCLE_ROOT/opt/codex-worker/VERSION") == previous ]]
cmp "$LIFECYCLE_TEMPLATE" "$LIFECYCLE_ROOT/etc/codex-worker/worker.yml"
# Also exercise rollback after both the runtime and service unit were replaced.
sed -i 's/RestartSec=5/RestartSec=17/' "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service"
cp "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service" "$LIFECYCLE_ROOT/previous.service"
if START_FAIL=true run_install; then exit 1; fi
[[ -f $LIFECYCLE_ROOT/active && -f $LIFECYCLE_ROOT/enabled ]]
[[ $(cat "$LIFECYCLE_ROOT/opt/codex-worker/VERSION") == previous ]]
cmp "$LIFECYCLE_ROOT/previous.service" "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service"
cmp "$LIFECYCLE_TEMPLATE" "$LIFECYCLE_ROOT/etc/codex-worker/worker.yml"
# A successful upgrade replaces the legacy service command while preserving
# the operator's configuration and stable registered identity.
identity=$(cat "$LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id")
run_install || { cat "$LIFECYCLE_ROOT/output"; exit 1; }
cmp "$LIFECYCLE_TEMPLATE" "$LIFECYCLE_ROOT/etc/codex-worker/worker.yml"
[[ $(cat "$LIFECYCLE_ROOT/var/lib/codex-worker/.codex-worker/worker-id") == "$identity" ]]
grep -Fxq "ExecStart=$LIFECYCLE_ROOT/opt/codex-worker/CodexWorker run --config $LIFECYCLE_ROOT/etc/codex-worker/worker.yml" \
  "$LIFECYCLE_ROOT/etc/systemd/system/codex-worker.service"
echo 'Worker installer lifecycle checks passed.'
