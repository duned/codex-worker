#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source "$repo_root/packaging/linux/install-server.sh"
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT
systemctl() { printf '%s\n' "${test_environment:-}"; }
prepare_development_dashboard
[[ -z $dashboard_backup ]]
test_environment="DOTNET_ENVIRONMENT=Development CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR=$root"
make_generation() {
  mkdir -p "$root/$1/assets"
  printf 'asset' > "$root/$1/assets/app.js"
  printf 'shell' > "$root/$1/index.html"
  printf '[{"path":"assets/app.js","sha256":"%s"}]' "$(sha256sum "$root/$1/assets/app.js" | cut -d' ' -f1)" > "$root/$1/assets.json"
}
make_generation current
make_generation previous
printf 'keep' > "$root/unrelated"
prepare_development_dashboard
[[ ! -e $root/current && ! -e $root/previous && -f $root/unrelated ]]
restore_development_dashboard
exec {dashboard_lock}>&-
[[ -f $root/current/index.html && -f $root/previous/index.html ]]
# Exercise the installer's actual failed-activation branch with command stubs.
(
  prepare_development_dashboard
  previous_target=releases/old
  start_command=restart
  systemctl() {
    if [[ $1 == restart ]]; then
      [[ -f $root/current/index.html && -f $root/previous/index.html ]]
    fi
  }
  ln() { :; }
  mv() {
    if [[ ${1:-} == -Tf ]]; then return 0; fi
    command mv "$@"
  }
  fail() { [[ -f $root/current/index.html && -f $root/unrelated ]]; exit 23; }
  # Force the first restart to fail while requiring restored assets on rollback.
  start_command=failed-start
  systemctl() {
    [[ $1 != failed-start ]] || return 1
    if [[ $1 == restart ]]; then [[ -f $root/current/index.html ]]; fi
  }
  source <(sed -n '/^if ! systemctl "\$start_command"/,/^if \[ \$bootstrap_github_cli/{ /^if \[ \$bootstrap_github_cli/d; p; }' "$repo_root/packaging/linux/install-server.sh")
) && exit 1 || [[ $? == 23 ]]
prepare_development_dashboard
commit_development_dashboard
exec {dashboard_lock}>&-
[[ ! -e $root/current && ! -e $root/previous && -f $root/unrelated && -d $root ]]
prepare_development_dashboard
commit_development_dashboard
exec {dashboard_lock}>&-
make_generation current
printf keep > "$root/current/unrelated"
if prepare_development_dashboard 2>/dev/null; then echo 'Unmanaged content accepted' >&2; exit 1; fi
[[ -f $root/current/unrelated && -f $root/current/index.html ]]
echo 'Server dashboard update transaction tests passed.'
