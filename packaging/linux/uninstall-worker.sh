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

uninstall_worker() {
  local full_purge=$1
  if [[ ${EUID} -ne 0 && -z $test_root ]]; then fail 'run as root.'; fi
  command -v systemctl >/dev/null 2>&1 || fail 'required command not found: systemctl'
  if [[ -e $unit_file || -L $unit_file ]]; then
    systemctl stop "$service" || fail "could not stop $service.service"
    systemctl disable "$service" || fail "could not disable $service.service"
    systemctl reset-failed "$service" || fail "could not clear failed state for $service.service"
  else
    printf 'Already absent: %s.service unit.\n' "$service"
  fi
  remove_path "$unit_file" 'systemd unit'
  systemctl daemon-reload || fail 'systemd could not reload unit files'
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
      if id codex-worker >/dev/null 2>&1; then
        local account_home account_group
        account_home=$(getent passwd codex-worker | cut -d: -f6) || fail 'could not inspect codex-worker account'
        account_group=$(id -gn codex-worker) || fail 'could not inspect codex-worker primary group'
        if [[ $account_home == /var/lib/codex-worker && $account_group == codex-worker ]]; then
          userdel codex-worker || fail 'could not remove codex-worker service account'
          printf 'Removed dedicated codex-worker service account.\n'
        else
          printf 'Retained codex-worker account because its home or primary group is not installer-standard.\n'
        fi
      else
        printf 'Already absent: codex-worker service account.\n'
      fi
      if getent group codex-worker >/dev/null 2>&1; then
        groupdel codex-worker || fail 'could not remove codex-worker group (it may still be in use)'
        printf 'Removed codex-worker group.\n'
      else
        printf 'Already absent: codex-worker group.\n'
      fi
    fi
  else
    printf 'Retained Worker configuration and credentials: %s\n' "$config_root"
    printf 'Retained Worker identity, execution history, and worktrees: %s\n' "$data_root"
    printf 'Retained codex-worker service account and group for reinstall.\n'
  fi
}

if [[ "${BASH_SOURCE[0]}" != "$0" ]]; then return 0; fi
while (($#)); do
  case "$1" in
    --purge) [[ $purge == false ]] || fail '--purge may only be specified once.'; purge=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1" ;;
  esac
done
uninstall_worker "$purge"
