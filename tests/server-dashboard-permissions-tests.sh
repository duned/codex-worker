#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/../packaging/linux/install-server.sh"
test_directory=$(mktemp -d)
trap 'rm -rf "$test_directory"' EXIT
# Exercise the real mode/ACL interaction without requiring a service account/root.
install() { chmod 0700 "${@: -1}"; }
mkdir "$test_directory/private" "$test_directory/production"
developer_uid=$(id -u)
setfacl -m user:"$developer_uid":--x "$test_directory/private"
chmod 0700 "$test_directory/private"
getfacl -cpn "$test_directory/private" | grep -q 'mask::---'
for iteration in 1 2; do
  ensure_private_server_directory "$test_directory/private"
  acl=$(getfacl -cpn "$test_directory/private")
  grep -q "^user:$developer_uid:--x$" <<< "$acl"
  grep -q '^mask::--x$' <<< "$acl"
  grep -q '^group::---$' <<< "$acl"
  grep -q '^other::---$' <<< "$acl"
done
ensure_private_server_directory "$test_directory/production"
[[ $(stat -c %a "$test_directory/production") == 700 ]]
echo 'Server dashboard ACL update regression passed.'
