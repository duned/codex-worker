#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf -- "$test_dir"' EXIT
mkdir -p "$test_dir/bin"
export PATH="$test_dir/bin:$PATH"
# All tools and identities are simulated; no host systemd or account changes.
cat > "$test_dir/bin/systemctl" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
root=$CODEX_UNINSTALL_TEST_ROOT
service=$TEST_SERVICE
printf '%s\n' "$*" >> "$root/events"
[[ ${FAIL_ACTION:-} != "$1" ]] || { echo 'Permission denied' >&2; exit 1; }
unit="$root/etc/systemd/system/$service.service"
link="$root/etc/systemd/system/multi-user.target.wants/$service.service"
case $1 in
  show)
    load=not-found active=inactive enabled=
    [[ ! -e $unit ]] || load=loaded
    [[ ! -e $root/cached ]] || load=loaded
    [[ ! -e $root/active ]] || active=$(cat "$root/active")
    [[ ! -L $link ]] || enabled=enabled
    printf 'LoadState=%s\nActiveState=%s\nUnitFileState=%s\n' "$load" "$active" "$enabled"
    ;;
  stop)
    [[ ! -e $root/active || $(cat "$root/active") != failed ]] || exit 0
    rm -f "$root/active"
    ;;
  reset-failed)
    if [[ ${RESET_UNLOAD:-false} == true ]]; then
      rm -f "$unit" "$root/active" "$root/cached"
      echo "Failed to reset failed state of unit $service.service: Unit $service.service not loaded." >&2
      exit 1
    fi
    [[ -e $unit || -e $root/cached ]] || {
      echo "Failed to reset failed state of unit $service.service: Unit $service.service not loaded." >&2; exit 1;
    }
    rm -f "$root/active"
    ;;
  disable)
    rm -f "$link"
    if [[ ! -e $unit ]]; then
      echo "Failed to disable unit: Unit file $service.service does not exist." >&2
      exit 1
    fi
    ;;
  daemon-reload)
    rm -f "$root/cached"
    [[ ${RELOAD_LEAVES_FAILED:-false} != true ]] || echo failed > "$root/active"
    ;;
  *) exit 99 ;;
esac
STUB
cat > "$test_dir/bin/getent" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[[ ${FAIL_ACTION:-} != getent ]] || exit 1
root=$CODEX_UNINSTALL_TEST_ROOT
service=$TEST_SERVICE
case $1 in
  passwd)
    [[ -e $root/user ]] || exit 2
    home=/var/lib/$service
    [[ ${CUSTOM_ACCOUNT:-false} != true ]] || home=/operator-home
    echo "$service:x:123:123::$home:/usr/sbin/nologin" ;;
  group)
    [[ -e $root/group ]] || exit 2
    echo "$service:x:123:" ;;
  *) exit 99 ;;
esac
STUB
cat > "$test_dir/bin/id" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[[ ${FAIL_ACTION:-} != id ]] || exit 1
[[ $1 == -gn && -e $CODEX_UNINSTALL_TEST_ROOT/user ]]
if [[ -e $CODEX_UNINSTALL_TEST_ROOT/group ]]; then echo "$TEST_SERVICE"; else echo 123; fi
STUB
for tool in userdel groupdel; do
  cat > "$test_dir/bin/$tool" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
command=${0##*/}
[[ ${FAIL_ACTION:-} != "$command" ]] || { echo 'Permission denied' >&2; exit 1; }
case $command in
  userdel) rm "$CODEX_UNINSTALL_TEST_ROOT/user" ;;
  groupdel) rm "$CODEX_UNINSTALL_TEST_ROOT/group" ;;
esac
STUB
done
real_rm=$(command -v rm)
export REAL_RM=$real_rm
cat > "$test_dir/bin/rm" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [[ ${FAIL_ACTION:-} == rm && $* == *"/opt/"* ]]; then echo 'I/O error' >&2; exit 1; fi
"$REAL_RM" "$@"
STUB
chmod +x "$test_dir/bin/"*

prepare() {
  local scenario=$1
  export CODEX_UNINSTALL_TEST_ROOT="$test_dir/$component-$scenario-$invocation-${mode:-test}"
  root=$CODEX_UNINSTALL_TEST_ROOT
  export TEST_SERVICE="codex-$component"
  mkdir -p "$root/etc/systemd/system/multi-user.target.wants" "$root/opt/$TEST_SERVICE" \
    "$root/etc/$TEST_SERVICE" "$root/var/lib/$TEST_SERVICE" "$root/var/log/$TEST_SERVICE" \
    "$root/run/$TEST_SERVICE" "$root/usr/local/bin"
  unit="$root/etc/systemd/system/$TEST_SERVICE.service"
  link="$root/etc/systemd/system/multi-user.target.wants/$TEST_SERVICE.service"
  touch "$unit" "$root/user" "$root/group" "$root/opt/$TEST_SERVICE/app" \
    "$root/etc/$TEST_SERVICE/config" "$root/var/lib/$TEST_SERVICE/state"
  ln -s "$unit" "$link"
  echo active > "$root/active"
  if [[ $component == worker ]]; then
    ln -s /opt/codex-worker/CodexWorker "$root/usr/local/bin/codex-worker"
    mkdir -p "$root/opt/codex-worker.previous.test" "$root/opt/codex-worker.next.test" "$root/opt/codex-worker.failed.test"
  else
    ln -s "$root/opt/$TEST_SERVICE/app" "$root/usr/local/bin/codex-server"
    mkdir -p "$root/etc/sudoers.d"
    touch "$root/etc/sudoers.d/codex-server-provisioning"
  fi
  mkdir -p "$root/etc/sudoers.d" "$root/usr/local/libexec" "$root/var/cache/codex-provisioning/npm"
  touch "$root/etc/sudoers.d/$TEST_SERVICE-provisioning" "$root/usr/local/libexec/codex-provisioning-codex" "$root/var/cache/codex-provisioning/npm/state"
  # Enable account cleanup in a relocated copy, with every account tool mocked.
  sed 's/if \[\[ -n $test_root \]\]; then/if false; then/' \
    "$repo_root/packaging/linux/uninstall-$component.sh" > "$root/uninstall.sh"
  case $scenario in
    inactive) rm "$root/active" ;;
    disabled|missing-link) rm "$link" ;;
    failed) echo failed > "$root/active" ;;
    restarting) echo activating > "$root/active" ;;
    cached) rm "$unit"; touch "$root/cached" ;;
    missing-failed) rm "$unit"; echo failed > "$root/active"; touch "$root/cached" ;;
    absent) rm "$unit" "$link" "$root/active" ;;
    partial)
      rm "$unit" "$root/active" "$root/user" "$root/group"
      rm -rf "$root/opt/$TEST_SERVICE" "$root/etc/$TEST_SERVICE" "$root/var/log/$TEST_SERVICE" ;;
    no-account) rm "$root/user" "$root/group" ;;
    no-user) rm "$root/user" ;;
    no-group) rm "$root/group" ;;
  esac
}
run_uninstall() {
  if [[ $invocation == stdin ]]; then
    cat "$root/uninstall.sh" | bash -s -- "$@"
  else
    bash "$root/uninstall.sh" "$@"
  fi
}
assert_final() {
  [[ ! -e $unit && ! -L $link && ! -e $root/active && ! -e $root/cached ]]
  [[ ! -e $root/etc/sudoers.d/$TEST_SERVICE-provisioning &&
     ! -e $root/usr/local/libexec/codex-provisioning-codex &&
     ! -e $root/var/cache/codex-provisioning ]]
  for path in "$root/opt/$TEST_SERVICE" "$root/var/log/$TEST_SERVICE" "$root/run/$TEST_SERVICE" \
    "$root/usr/local/bin/$TEST_SERVICE" "$root/opt/$TEST_SERVICE.previous."* \
    "$root/opt/$TEST_SERVICE.failed."* "$root/opt/$TEST_SERVICE.next."*; do
    [[ ! -e $path && ! -L $path ]]
  done
  if [[ $mode == purge ]]; then
    [[ ! -e $root/etc/$TEST_SERVICE && ! -e $root/var/lib/$TEST_SERVICE && ! -e $root/user && ! -e $root/group ]]
  elif [[ $scenario != partial ]]; then
    if [[ $scenario == no-account || $scenario == no-user ]]; then [[ ! -e $root/user ]]; else [[ -e $root/user ]]; fi
    if [[ $scenario == no-account || $scenario == no-group ]]; then [[ ! -e $root/group ]]; else [[ -e $root/group ]]; fi
    [[ -f $root/etc/$TEST_SERVICE/config && -f $root/var/lib/$TEST_SERVICE/state ]]
  else
    [[ -f $root/var/lib/$TEST_SERVICE/state ]]
  fi
}
for component in worker server; do
  for invocation in file stdin; do
    for mode in normal purge; do
      for scenario in active inactive disabled failed restarting missing-link cached missing-failed absent partial no-account no-user no-group; do
        prepare "$scenario"
        args=(); [[ $mode != purge ]] || args+=(--purge)
        run_uninstall "${args[@]}" > "$root/output" 2>&1 || { cat "$root/output"; exit 1; }
        assert_final
        run_uninstall "${args[@]}" > "$root/repeat" 2>&1 || { cat "$root/repeat"; exit 1; }
        assert_final
        grep -q 'Already absent' "$root/repeat"
      done
    done
  done
  invocation=file mode=purge
  # The v0.14.3 symptom: reset-failed races with an unloaded unit.
  prepare reset-race
  echo failed > "$root/active"
  RESET_UNLOAD=true run_uninstall --purge > "$root/output" 2>&1
  assert_final
  # Unexpected errors must abort; retrying the partial removal must converge.
  for action in show stop reset-failed disable daemon-reload rm getent id userdel groupdel; do
    prepare "error-$action"
    [[ $action != reset-failed ]] || echo failed > "$root/active"
    if FAIL_ACTION=$action run_uninstall --purge > "$root/output" 2>&1; then
      echo "$component ignored $action failure" >&2; exit 1
    fi
    grep -Eq 'could not|remains' "$root/output"
    run_uninstall --purge > "$root/retry" 2>&1
    assert_final
  done
  prepare verification
  if RELOAD_LEAVES_FAILED=true run_uninstall --purge > "$root/output" 2>&1; then
    echo 'Uninstaller accepted failed final systemd state' >&2; exit 1
  fi
  grep -q 'remains loaded' "$root/output"
  prepare shared-helper
  other=worker; [[ $component != worker ]] || other=server
  touch "$root/etc/sudoers.d/codex-$other-provisioning"
  run_uninstall --purge > "$root/output" 2>&1
  [[ ! -e $root/etc/sudoers.d/$TEST_SERVICE-provisioning &&
     -e $root/usr/local/libexec/codex-provisioning-codex &&
     -e $root/var/cache/codex-provisioning/npm/state ]]
  rm "$root/etc/sudoers.d/codex-$other-provisioning"
  run_uninstall --purge > "$root/repeat" 2>&1
  [[ ! -e $root/usr/local/libexec/codex-provisioning-codex && ! -e $root/var/cache/codex-provisioning ]]
  prepare custom-account
  CUSTOM_ACCOUNT=true run_uninstall --purge > "$root/output" 2>&1
  [[ -e $root/user && -e $root/group ]]
  grep -q 'not installer-standard' "$root/output"
done
printf 'Worker and Server uninstaller lifecycle checks passed.\n'
