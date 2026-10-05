#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT
# Extract product writers, without running either installer against the host.
for component in worker server; do
  sed -n '/^write_codex_provisioning_helper() {/,/^install_provisioning_sudoers() {/p' \
    "$repo_root/packaging/linux/install-$component.sh" | sed '$d' > "$test_dir/writer"
  source "$test_dir/writer"
  write_codex_provisioning_helper > "$test_dir/$component.helper"
  write_provisioning_sudoers > "$test_dir/$component.policy"
  bash -n "$test_dir/$component.helper"
  chmod +x "$test_dir/$component.helper"
  if command -v visudo >/dev/null; then visudo -cf "$test_dir/$component.policy" >/dev/null; fi
  ! grep -Eq '/usr/bin/(npm|chmod)|/bin/(bash|sh)|NOPASSWD: ALL|\*' "$test_dir/$component.policy"
done
cmp "$test_dir/worker.helper" "$test_dir/server.helper"
# Relocate only fixed paths/root ownership checks. Real install prepares modes;
# fake npm records the isolated environment and immutable argument contract.
mkdir "$test_dir/bin"
cat > "$test_dir/bin/npm" <<EOF_NPM
#!/bin/bash
/usr/bin/env > '$test_dir/environment'
printf '%s\\n' "\$@" > '$test_dir/arguments'
# Simulate npm creating package files under the inherited helper umask.
mkdir -p '$test_dir/package/bin'
touch '$test_dir/package/package.json'
EOF_NPM
chmod +x "$test_dir/bin/npm"
sed -e "s|/var/cache/codex-provisioning|$test_dir/cache|g" \
    -e "s|/usr/bin/npm|$test_dir/bin/npm|g" \
    -e 's/$EUID == 0/1 == 1/' \
    -e 's/%u "$path") != 0/%u "$path") != $EUID/' \
    -e 's/-o root -g root //g' "$test_dir/worker.helper" > "$test_dir/helper"
chmod +x "$test_dir/helper"
printf 'touch "%s"\n' "$test_dir/shell-injected" > "$test_dir/untrusted-bash-env"
if [[ $EUID != 0 ]]; then
  if "$test_dir/worker.helper" install 2>/dev/null; then echo 'Helper accepted non-root execution'; exit 1; fi
fi
for operation in install uninstall; do
  (umask 0077; PRIVATE_TEST_SECRET=unexported npm_config_prefix=/bad HOME=/bad BASH_ENV="$test_dir/untrusted-bash-env" "$test_dir/helper" "$operation")
  [[ ! -e $test_dir/shell-injected ]]
  [[ $(stat -c %a "$test_dir/package/bin") == 755 && $(stat -c %a "$test_dir/package/package.json") == 644 ]]
  [[ $(stat -c %a "$test_dir/cache") == 700 && $(stat -c %a "$test_dir/cache/npm") == 700 &&
     $(stat -c %a "$test_dir/cache/npmrc") == 600 ]]
  ! grep -qE 'PRIVATE_TEST_SECRET|npm_config_prefix|/bad' "$test_dir/environment"
  grep -Fxq "$operation" "$test_dir/arguments"
  grep -Fxq '/usr/local' "$test_dir/arguments"
  grep -Fxq 'https://registry.npmjs.org' "$test_dir/arguments"
  grep -Fxq "$test_dir/cache/npmrc" "$test_dir/arguments"
  [[ $(grep -Fxc '/dev/null' "$test_dir/arguments") == 1 ]]
  if [[ $operation == install ]]; then grep -Fxq '@openai/codex@latest' "$test_dir/arguments";
  else grep -Fxq '@openai/codex' "$test_dir/arguments"; fi
done
rm "$test_dir/arguments"
for args in 'install extra' 'update' '/bin/sh' ''; do
  read -r -a arguments <<< "$args"
  if bash "$test_dir/helper" "${arguments[@]}"; then echo 'Helper accepted arbitrary operation'; exit 1; fi
  [[ ! -e $test_dir/arguments ]]
done
chmod 0777 "$test_dir/cache/npm"
if bash "$test_dir/helper" install 2> "$test_dir/failure"; then exit 1; fi
grep -Fxq 'CODEX_CACHE_PREPARATION_FAILED' "$test_dir/failure"
[[ ! -e $test_dir/arguments ]]
chmod 0700 "$test_dir/cache/npm"
mv "$test_dir/cache/npm" "$test_dir/npm.saved"
ln -s "$test_dir/npm.saved" "$test_dir/cache/npm"
if bash "$test_dir/helper" install 2> "$test_dir/failure"; then exit 1; fi
grep -Fxq 'CODEX_CACHE_PREPARATION_FAILED' "$test_dir/failure"
[[ ! -e $test_dir/arguments ]]
# A real npm config load, without registry access or mutation. This also runs on
# stock distro npm in CI/acceptance; it catches the original double-load error.
if command -v npm >/dev/null; then
  npm_executable=$(command -v npm)
  [[ ! -x /usr/bin/npm ]] || npm_executable=/usr/bin/npm
  [[ $("$npm_executable" config get registry --global --registry https://registry.npmjs.org --userconfig /dev/null --globalconfig "$test_dir/cache/npmrc") == https://registry.npmjs.org/ ]]
  [[ $("$npm_executable" config get registry --global --registry https://registry.npmjs.org --userconfig /dev/null) == https://registry.npmjs.org/ ]]
fi
echo 'Codex provisioning helper contracts passed.'
