#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
for component in worker server; do
  sed -n '/^write_docker_provisioning_helper() {/,/^install_provisioning_sudoers() {/p' \
    "$repo_root/packaging/linux/install-$component.sh" | sed '$d' > "$test_dir/writer"
  source "$test_dir/writer"
  write_docker_provisioning_helper > "$test_dir/$component.helper"
  write_provisioning_sudoers > "$test_dir/$component.policy"
  bash -n "$test_dir/$component.helper"
  grep -Fxq '    /usr/local/libexec/codex-provisioning-docker configure' "$test_dir/$component.policy"
  ! grep -Eq 'usermod|groupadd|NOPASSWD: ALL|\*' "$test_dir/$component.policy"
done
cmp "$test_dir/worker.helper" "$test_dir/server.helper"
mkdir "$test_dir/bin" "$test_dir/proc"
cat > "$test_dir/bin/stub" <<'STUB'
#!/bin/bash
set -euo pipefail
state=$(dirname "$(dirname "$0")")
case $(basename "$0") in
  id) [[ $* == codex-worker || $* == '-nG codex-worker' ]]; cat "$state/membership" ;;
  groupadd) [[ $* == '-f docker' ]] ;;
  usermod)
    [[ $* == '-a -G docker codex-worker' ]]
    printf ' docker' >> "$state/membership"
    echo mutation >> "$state/mutations" ;;
  runuser)
    [[ $* == '-u codex-worker -- /usr/bin/env -i PATH=/usr/bin:/bin HOME=/var/lib/codex-worker /usr/bin/docker --host unix:///var/run/docker.sock info --format {{.ServerVersion}}' ]]
    if [[ -e $state/daemon-down ]]; then echo 'Cannot connect to the Docker daemon' >&2; exit 1; fi ;;
  systemctl) [[ $* == 'show --property MainPID --value codex-worker.service' ]]; cat "$state/pid" ;;
  getent) [[ $* == 'group docker' ]]; echo 'docker:x:777:' ;;
  awk) exec /usr/bin/awk "$@" ;;
  *) exit 2 ;;
esac
STUB
chmod +x "$test_dir/bin/stub"
for name in id groupadd usermod runuser systemctl getent awk; do ln -s stub "$test_dir/bin/$name"; done
sed -e 's/$EUID == 0/1 == 1/' \
  -e "s|/usr/bin/id|$test_dir/bin/id|g" -e "s|/usr/sbin/groupadd|$test_dir/bin/groupadd|g" \
  -e "s|/usr/sbin/usermod|$test_dir/bin/usermod|g" -e "s|/usr/sbin/runuser|$test_dir/bin/runuser|g" \
  -e "s|/usr/bin/systemctl|$test_dir/bin/systemctl|g" -e "s|/usr/bin/getent|$test_dir/bin/getent|g" \
  -e "s|/usr/bin/awk|$test_dir/bin/awk|g" -e "s|/proc/|$test_dir/proc/|g" \
  "$test_dir/worker.helper" > "$test_dir/helper"
echo 'codex-worker operator' > "$test_dir/membership"
echo 0 > "$test_dir/pid"
bash "$test_dir/helper" configure
bash "$test_dir/helper" configure
[[ $(wc -l < "$test_dir/mutations") == 1 ]]
[[ $(cat "$test_dir/membership") == $'codex-worker operator\n docker' ]]
# Existing membership causes no mutation.
rm "$test_dir/mutations"
bash "$test_dir/helper" configure
[[ ! -e $test_dir/mutations ]]
# Caller cannot select another operation, user or group.
for args in 'configure other-user' 'configure docker' 'uninstall' ''; do
  read -r -a arguments <<< "$args"
  if bash "$test_dir/helper" "${arguments[@]}"; then exit 1; fi
done
# Live process still lacks the new group: report refresh instead of ready or restarting it.
echo 123 > "$test_dir/pid"
mkdir "$test_dir/proc/123"
echo 'Groups: 555' > "$test_dir/proc/123/status"
result=0
bash "$test_dir/helper" configure || result=$?
[[ $result == 10 ]]
echo 'Groups: 555 777' > "$test_dir/proc/123/status"
bash "$test_dir/helper" configure
# Daemon failure takes precedence over group/service-context success.
touch "$test_dir/daemon-down"
if bash "$test_dir/helper" configure 2> "$test_dir/error"; then exit 1; fi
grep -Fxq 'Cannot connect to the Docker daemon' "$test_dir/error"
echo 'Docker provisioning helper tests passed.'
