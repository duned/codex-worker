#!/usr/bin/env bash
set -euo pipefail

service=codex-worker
test_root=${CODEX_UNINSTALL_TEST_ROOT:-}
install_root=${test_root}/opt/codex-worker
config_root=${test_root}/etc/codex-worker
data_root=${test_root}/var/lib/codex-worker
log_root=${test_root}/var/log/codex-worker
runtime_root=${test_root}/run/codex-worker
unit_file=${test_root}/etc/systemd/system/codex-worker.service
purge=false

usage() {
  cat <<'EOF'
Usage: uninstall-worker.sh [--purge]

Remove the Codex Worker service and installed releases. Configuration and
identity/execution state are retained unless --purge is specified.

Options:
  --purge       Permanently remove configuration, credentials, and Worker state
  -h, --help    Show this help
EOF
}

fail() { printf 'Codex Worker uninstaller: %s\n' "$*" >&2; exit 1; }

remove_path() {
  local path=$1 description=$2
  if [[ -e $path || -L $path ]]; then
    rm -rf -- "$path" || fail "could not remove $description ($path)"
    [[ ! -e $path && ! -L $path ]] || fail "$description remains after removal ($path)"
    printf 'Removed %s (%s).\n' "$description" "$path"
  else
    printf 'Already absent: %s (%s).\n' "$description" "$path"
  fi
}

remove_worker_releases() {
  local path
  remove_path "$install_root" 'installed Worker releases'
  for path in "${install_root}.previous."* "${install_root}.failed."* "${install_root}.next."*; do
    [[ -e $path || -L $path ]] || continue
    remove_path "$path" 'obsolete Worker release directory'
  done
}

# Query the manager even when the on-disk unit has already disappeared. A
# cached unit can still be running (or waiting to restart).
read_service_state() {
  local state key value
  state=$(systemctl show "$service.service" --property=LoadState --property=ActiveState --property=UnitFileState) ||
    fail "could not inspect $service.service"
  load_state= active_state= unit_state=
  while IFS='=' read -r key value; do
    case $key in
      LoadState) load_state=$value ;;
      ActiveState) active_state=$value ;;
      UnitFileState) unit_state=$value ;;
      *) fail "unexpected systemd state for $service.service: $key" ;;
    esac
  done <<< "$state"
  [[ -n $load_state && -n $active_state ]] || fail "incomplete systemd state for $service.service"
}

service_operation() {
  local action=$1 description=$2 output
  if output=$(LC_ALL=C systemctl "$action" "$service.service" 2>&1); then
    [[ -z $output ]] || printf '%s\n' "$output"
    return 0
  fi
  # Accept only systemd's specific missing-unit diagnostics, never arbitrary
  # command failures. Recheck the manager so an active cached unit cannot hide.
  if [[ $output == "Failed to stop $service.service: Unit $service.service not loaded." ||
        $output == "Failed to stop $service.service: Unit $service.service not found." ||
        $output == "Failed to reset failed state of unit $service.service: Unit $service.service not loaded." ||
        $output == "Failed to disable unit: Unit file $service.service does not exist." ]]; then
    read_service_state
    if [[ ( $load_state == not-found || $action == disable ) && $active_state == inactive &&
          ( $action != disable || -z $unit_state || $unit_state == disabled || $unit_state == not-found ) ]]; then
      printf 'Already absent: %s.service (%s unnecessary).\n' "$service" "$action"
      return 0
    fi
  fi
  fail "could not $description $service.service: $output"
}

remove_service() {
  read_service_state
  if [[ $load_state != not-found || $active_state != inactive ]]; then
    # A failed unit is already stopped; its file may be gone while systemd
    # still retains the failure record. Clear it without requiring stop.
    if [[ $active_state != failed ]]; then
      service_operation stop stop
      read_service_state
    fi
    # Clear failure while the unit still exists, before disable's implicit
    # reload or removal can unload it. stop also cancels automatic restarts.
    if [[ $active_state == failed ]]; then
      service_operation reset-failed 'clear failed state for'
    fi
  else
    printf 'Already absent: %s.service loaded state.\n' "$service"
  fi
  # Even a missing unit may have dangling enablement links; disable removes
  # those links and is safe for an already-disabled unit.
  service_operation disable disable
  remove_path "$unit_file" 'systemd unit'
  systemctl daemon-reload || fail 'systemd could not reload unit files'
  read_service_state
  [[ $load_state == not-found && $active_state == inactive &&
     ( -z $unit_state || $unit_state == disabled || $unit_state == not-found ) ]] ||
    fail "$service.service remains loaded, active, failed, or enabled after removal ($load_state/$active_state/$unit_state)"
  printf 'Verified %s.service is absent and inactive.\n' "$service"
}

# getent uses status 2 for a missing key; status 1/3 or other errors are not
# absence. Keep a nonstandard account and its group together for safety.
remove_service_identity() {
  local account status home primary_group group
  local passwd_pattern='^[^:]+:[^:]*:[0-9]+:[0-9]+:[^:]*:[^:]*:[^:]*$'
  local group_pattern='^[^:]+:[^:]*:[0-9]+:[^:]*$'
  if account=$(getent passwd "$service"); then
    [[ -n $account && $account != *$'\n'* && $account == "$service:"* && $account =~ $passwd_pattern ]] || fail "malformed $service account lookup"
    IFS=: read -r _ _ _ _ _ home _ <<< "$account"
    primary_group=$(id -gn "$service") || fail "could not inspect $service primary group"
    # A previous interrupted purge may have removed the primary group first.
    # id then reports its numeric GID. Confirm it is genuinely orphaned rather
    # than treating another existing primary group as installer-owned.
    if [[ $primary_group =~ ^[0-9]+$ ]]; then
      if getent group "$primary_group" >/dev/null; then
        : # An existing numeric primary group is nonstandard; retain the user.
      else
        status=$?
        [[ $status == 2 ]] || fail "could not inspect $service primary group"
        primary_group=$service
      fi
    fi
    if [[ $home != /var/lib/$service || $primary_group != "$service" ]]; then
      printf 'Retained %s account and group because its identity is not installer-standard.\n' "$service"
      return 0
    fi
    userdel "$service" || fail "could not remove $service service account"
    if getent passwd "$service" >/dev/null; then fail "$service service account remains after removal";
    else status=$?; [[ $status == 2 ]] || fail "could not verify $service account removal"; fi
    printf 'Removed dedicated %s service account.\n' "$service"
  else
    status=$?
    [[ $status == 2 ]] || fail "could not inspect $service service account (getent status $status)"
    printf 'Already absent: %s service account.\n' "$service"
  fi
  if group=$(getent group "$service"); then
    [[ -n $group && $group != *$'\n'* && $group == "$service:"* && $group =~ $group_pattern ]] || fail "malformed $service group lookup"
    groupdel "$service" || fail "could not remove $service group (it may still be in use)"
    if getent group "$service" >/dev/null; then fail "$service group remains after removal";
    else status=$?; [[ $status == 2 ]] || fail "could not verify $service group removal"; fi
    printf 'Removed %s group.\n' "$service"
  else
    status=$?
    [[ $status == 2 ]] || fail "could not inspect $service group (getent status $status)"
    printf 'Already absent: %s group.\n' "$service"
  fi
}

uninstall_worker() {
  local full_purge=$1
  if [[ ${EUID} -ne 0 && -z $test_root ]]; then fail 'run as root.'; fi
  command -v systemctl >/dev/null 2>&1 || fail 'required command not found: systemctl'
  remove_service
  remove_worker_releases
  remove_path "$log_root" 'Worker logs'
  remove_path "$runtime_root" 'Worker runtime files'

  if [[ $full_purge == true ]]; then
    printf 'PURGE: permanently deleting Worker configuration, credentials, identity, execution history, and worktrees.\n'
    remove_path "$config_root" 'Worker configuration and credentials'
    remove_path "$data_root" 'Worker identity and persistent state'
    if [[ -n $test_root ]]; then
      printf 'Test root set; host service account and group were not changed.\n'
    else
      remove_service_identity
    fi
  else
    printf 'Retained Worker configuration and credentials: %s\n' "$config_root"
    printf 'Retained Worker identity, execution history, and worktrees: %s\n' "$data_root"
    printf 'Retained codex-worker service account and group for reinstall.\n'
  fi
}

# As in install-worker.sh, BASH_SOURCE may be empty when executing from stdin.
if [[ "${BASH_SOURCE[0]:-$0}" != "$0" ]]; then return 0; fi
while (($#)); do
  case "$1" in
    --purge) [[ $purge == false ]] || fail '--purge may only be specified once.'; purge=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1" ;;
  esac
done
uninstall_worker "$purge"
