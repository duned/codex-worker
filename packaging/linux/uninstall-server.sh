#!/usr/bin/env bash
set -euo pipefail

service=codex-server
test_root=${CODEX_UNINSTALL_TEST_ROOT:-}
install_root=${test_root}/opt/codex-server
config_root=${test_root}/etc/codex-server
data_root=${test_root}/var/lib/codex-server
log_root=${test_root}/var/log/codex-server
runtime_root=${test_root}/run/codex-server
unit_file=${test_root}/etc/systemd/system/codex-server.service
purge=false

usage() {
  cat <<'EOF'
Usage: uninstall-server.sh [--purge]

Remove the Codex Server service and installed releases. Configuration and
persistent state are retained unless --purge is specified.

Options:
  --purge       Permanently remove configuration, credentials, and Server state
  -h, --help    Show this help
EOF
}

fail() { printf 'Codex Server uninstaller: %s\n' "$*" >&2; exit 1; }

remove_path() {
  local path=$1 description=$2
  if [[ -e $path || -L $path ]]; then
    rm -rf -- "$path" || fail "could not remove $description ($path)"
    printf 'Removed %s (%s).\n' "$description" "$path"
  else
    printf 'Already absent: %s (%s).\n' "$description" "$path"
  fi
}

uninstall_server() {
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
  remove_path "$install_root" 'installed Server releases'
  remove_path "${test_root}/usr/local/bin/codex-server" 'Server operator helper'
  remove_path "$log_root" 'Server logs'
  remove_path "$runtime_root" 'Server runtime files'

  if [[ $full_purge == true ]]; then
    printf 'PURGE: permanently deleting Codex Server configuration, credentials, and persistent state.\n'
    remove_path "$config_root" 'Server configuration and credentials'
    remove_path "$data_root" 'Server persistent data'
    if [[ -n $test_root ]]; then
      printf 'Test root set; host service account and group were not changed.\n'
    else
      if id codex-server >/dev/null 2>&1; then
        local account_home account_group
        account_home=$(getent passwd codex-server | cut -d: -f6) || fail 'could not inspect codex-server account'
        account_group=$(id -gn codex-server) || fail 'could not inspect codex-server primary group'
        if [[ $account_home == /var/lib/codex-server && $account_group == codex-server ]]; then
          userdel codex-server || fail 'could not remove codex-server service account'
          printf 'Removed dedicated codex-server service account.\n'
        else
          printf 'Retained codex-server account because its home or primary group is not installer-standard.\n'
        fi
      else
        printf 'Already absent: codex-server service account.\n'
      fi
      if getent group codex-server >/dev/null 2>&1; then
        groupdel codex-server || fail 'could not remove codex-server group (it may still be in use)'
        printf 'Removed codex-server group.\n'
      else
        printf 'Already absent: codex-server group.\n'
      fi
    fi
  else
    printf 'Retained Server configuration and credentials: %s\n' "$config_root"
    printf 'Retained Server database and persistent state: %s\n' "$data_root"
    printf 'Retained codex-server service account and group for reinstall.\n'
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
uninstall_server "$purge"
