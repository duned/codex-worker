#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
bin="$test_root/bin"
mkdir -p "$bin" "$test_root/projects" "$test_root/apps"
export PATH="$bin:$PATH"
export CW_DEPLOY_DIR="$test_root/apps/worker"
export CW_SERVICE_NAME=cw-test
export CW_TEST_ROOT="$test_root"
cat > "$test_root/projects/a.yml" <<'YAML'
project:
  name: Alpha
  repository: a/alpha
YAML
cat > "$test_root/projects/b.yaml" <<'YAML'
project:
  name: Beta
  repository: b/beta
YAML
printf '[{"name":"Alpha","configurationPath":"%s/projects/a.yml","projectDirectory":"%s/repos/alpha","repository":"a/alpha"},{"name":"Beta","configurationPath":"%s/projects/b.yaml","projectDirectory":"%s/repos/beta","repository":"b/beta"}]\n' "$test_root" "$test_root" "$test_root" "$test_root" > "$test_root/projects.json"
cat > "$bin/journalctl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$CW_TEST_ROOT/journal.args"
MOCK
cat > "$bin/curl" <<'MOCK'
#!/usr/bin/env bash
cat "$CW_TEST_ROOT/projects.json"
MOCK
cat > "$bin/systemctl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CW_TEST_ROOT/systemctl.args"
case "$1" in
  is-active)
    if [[ ${2:-} == --quiet ]]; then shift; fi
    [[ -f $CW_TEST_ROOT/active ]] && exit 0 || exit 3
    ;;
  stop) [[ ! -f $CW_TEST_ROOT/fail-stop ]] || { echo 'Failed to stop service: Access denied' >&2; exit 1; }; rm -f "$CW_TEST_ROOT/active" ;;
  start) [[ ! -f $CW_TEST_ROOT/fail-start ]] || { echo 'Failed to start service: Access denied' >&2; exit 1; }; touch "$CW_TEST_ROOT/active" ;;
  show) printf '1\n' ;;
  *) exit 0 ;;
esac
MOCK
cat > "$bin/sudo" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CW_TEST_ROOT/sudo.args"
[[ ${1:-} == -n ]] || { echo 'interactive sudo invocation' >&2; exit 90; }
shift
if [[ -f $CW_TEST_ROOT/fail-sudo-stop && ${1:-} == systemctl && ${2:-} == stop ]]; then
  echo 'sudo: a password is required' >&2
  exit 1
fi
exec "$@"
MOCK
cat > "$bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ ! -f $CW_TEST_ROOT/fail-publish ]] || exit 1
printf '%s\n' "$*" > "$CW_TEST_ROOT/dotnet.args"
while (($#)); do
  if [[ $1 == -o ]]; then out=$2; shift 2; else shift; fi
done
mkdir -p "$out"
printf '#!/bin/sh\nexit 0\n' > "$out/CodexWorker"
chmod +x "$out/CodexWorker"
MOCK
chmod +x "$bin"/*

# Exercise repository resolution independently of the checkout running this test.
fixture_repo="$test_root/repository with spaces"
fixture_bin="$test_root/path bin"
caller_dir="$test_root/unrelated caller directory"
mkdir -p "$fixture_repo/src/CodexWorker" "$fixture_repo/projects" "$fixture_bin" "$caller_dir"
cp "$repo_root/cw" "$fixture_repo/cw"
cat > "$fixture_repo/Directory.Build.props" <<'XML'
<Project>
  <PropertyGroup>
    <Version>9.8.7</Version>
  </PropertyGroup>
</Project>
XML
cat > "$fixture_repo/src/CodexWorker/CodexWorker.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
XML
cat > "$fixture_repo/projects/fixture.yml" <<'YAML'
project:
  name: Fixture
YAML
git -C "$fixture_repo" init -q
git -C "$fixture_repo" config user.email cw-tests@example.invalid
git -C "$fixture_repo" config user.name cw-tests
git -C "$fixture_repo" add cw Directory.Build.props src projects
git -C "$fixture_repo" commit -qm 'fixture repository'
git -C "$fixture_repo" branch -M main
feature_branch='feature/16-1-model-node-capabilities-and-provisioning-state-110'
feature_worktree="$test_root/worktree with spaces"
detached_worktree="$test_root/detached worktree"
git -C "$fixture_repo" worktree add -q -b "$feature_branch" "$feature_worktree"
git -C "$fixture_repo" worktree add -q --detach "$detached_worktree" HEAD
printf '[{"name":"Fixture","configurationPath":"%s/projects/fixture.yml","projectDirectory":"%s/repo","repository":"owner/fixture","token":"secret-value"},{"name":"Finance","configurationPath":"/srv/worker projects/finance.yml","projectDirectory":"/srv/repos/finance","repository":"owner/finance"}]\n' "$fixture_repo" "$fixture_repo" > "$test_root/projects.json"
ln -s "$fixture_repo/cw" "$fixture_bin/cw"
export CW_WORKER_CONFIG="$test_root/unreadable-or-missing-worker.yml"
export CW_WORKER_API_URL=http://127.0.0.1:5080
export CW_DEPLOY_DIR="$test_root/apps/fixture worker"
(
  cd "$caller_dir"
  "$fixture_bin/cw" --help | grep -Fq 'status, s'
  "$fixture_bin/cw" status > "$test_root/symlink-status.out"
  grep -Fq "  root     $fixture_repo" "$test_root/symlink-status.out"
  grep -Fq "  - main" "$test_root/symlink-status.out"
  grep -Fq "$feature_branch" "$test_root/symlink-status.out"
  grep -Fq "$(git -C "$fixture_repo" rev-parse --short HEAD)  $fixture_repo" "$test_root/symlink-status.out"
  grep -Fq "$(git -C "$feature_worktree" rev-parse --short HEAD)  $feature_worktree" "$test_root/symlink-status.out"
  grep -Fq "(detached)" "$test_root/symlink-status.out"
  grep -Fq "$(git -C "$detached_worktree" rev-parse --short HEAD)  $detached_worktree" "$test_root/symlink-status.out"
  "$fixture_bin/cw" s > "$test_root/symlink-short-status.out"
  grep -Fq "  root     $fixture_repo" "$test_root/symlink-short-status.out"
  "$fixture_repo/cw" status > "$test_root/direct-status.out"
  grep -Fq "  root     $fixture_repo" "$test_root/direct-status.out"
  "$fixture_bin/cw" projects > "$test_root/symlink-projects.out"
  grep -Fq 'Fixture' "$test_root/symlink-projects.out"
  grep -Fq "$fixture_repo/projects/fixture.yml" "$test_root/symlink-projects.out"
  grep -Fq "$fixture_repo/repo" "$test_root/symlink-projects.out"
  grep -Fq 'Finance' "$test_root/symlink-projects.out"
  grep -Fq '/srv/worker projects/finance.yml' "$test_root/symlink-projects.out"
  ! grep -Fq 'unreadable-or-missing-worker.yml' "$test_root/symlink-projects.out"
  ! grep -Fq 'secret-value' "$test_root/symlink-projects.out"
  "$fixture_bin/cw" p >/dev/null
  "$fixture_bin/cw" log -n 23
  grep -Fxq -- '-u cw-test -n 23 --no-pager' "$test_root/journal.args"
  "$fixture_bin/cw" l >/dev/null
  grep -Fxq -- '-u cw-test -n 100 --no-pager' "$test_root/journal.args"
  "$fixture_bin/cw" deploy > "$test_root/symlink-deploy.out"
  grep -Fq "$fixture_repo/src/CodexWorker/CodexWorker.csproj" "$test_root/dotnet.args"
  grep -Fq '9.8.7' "$CW_DEPLOY_DIR/VERSION"
  "$fixture_bin/cw" d >/dev/null
)

mkdir -p "$test_root/invalid target"
cp "$repo_root/cw" "$test_root/invalid target/cw"
ln -s "$test_root/invalid target/cw" "$fixture_bin/cw-invalid"
if "$fixture_bin/cw-invalid" status > "$test_root/invalid.out" 2>&1; then
  echo 'invalid repository unexpectedly succeeded' >&2; exit 1
fi
grep -Fq "not a Codex Worker repository: $test_root/invalid target" "$test_root/invalid.out"
grep -Fq 'expected Directory.Build.props and src/CodexWorker/CodexWorker.csproj' "$test_root/invalid.out"

# Restore defaults for the original-checkout command coverage below.
export CW_DEPLOY_DIR="$test_root/apps/worker"
printf '[{"name":"Alpha","configurationPath":"%s/projects/a.yml","projectDirectory":"%s/repos/alpha","repository":"a/alpha"},{"name":"Beta","configurationPath":"%s/projects/b.yaml","projectDirectory":"%s/repos/beta","repository":"b/beta"}]\n' "$test_root" "$test_root" "$test_root" "$test_root" > "$test_root/projects.json"

"$repo_root/cw" --help | grep -Fq 'status, s'
"$repo_root/cw" -h >/dev/null
"$repo_root/cw" help >/dev/null
: > "$test_root/sudo.args"
"$repo_root/cw" s > "$test_root/status.out"
grep -Fq 'Repository' "$test_root/status.out"
grep -Fq 'Worker service (cw-test)' "$test_root/status.out"
[[ ! -s $test_root/sudo.args ]]
"$repo_root/cw" p > "$test_root/projects.out"
grep -Fq 'Alpha' "$test_root/projects.out"
grep -Fq "$test_root/projects/a.yml" "$test_root/projects.out"
grep -Fq 'Beta' "$test_root/projects.out"
"$repo_root/cw" l -n 300 -f
grep -Fxq -- '-u cw-test -n 300 -f --no-pager' "$test_root/journal.args"
"$repo_root/cw" log
grep -Fxq -- '-u cw-test -n 100 --no-pager' "$test_root/journal.args"
if "$repo_root/cw" nonsense >"$test_root/unknown.out" 2>&1; then
  echo 'unknown command unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'Run ./cw --help' "$test_root/unknown.out"

mkdir -p "$CW_DEPLOY_DIR"
printf 'old deployment\n' > "$CW_DEPLOY_DIR/old-file"
touch "$test_root/fail-publish"
if "$repo_root/cw" deploy >"$test_root/deploy-fail.out" 2>&1; then
  echo 'failed publish unexpectedly succeeded' >&2; exit 1
fi
grep -Fxq 'old deployment' "$CW_DEPLOY_DIR/old-file"
rm "$test_root/fail-publish"
: > "$test_root/systemctl.args"
: > "$test_root/sudo.args"
touch "$test_root/active" "$test_root/fail-sudo-stop"
if "$repo_root/cw" deploy >"$test_root/deploy-sudo-fail.out" 2>&1; then
  echo 'sudo authorization failure unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'sudo authorization failed' "$test_root/deploy-sudo-fail.out"
grep -Fxq 'old deployment' "$CW_DEPLOY_DIR/old-file"
[[ -f $test_root/active ]]
! grep -Fq 'start cw-test' "$test_root/systemctl.args"
rm "$test_root/fail-sudo-stop"
touch "$test_root/fail-stop"
if "$repo_root/cw" deploy >"$test_root/deploy-stop-fail.out" 2>&1; then
  echo 'failed service stop unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'systemd stop failed' "$test_root/deploy-stop-fail.out"
grep -Fxq 'old deployment' "$CW_DEPLOY_DIR/old-file"
[[ -f $test_root/active ]]
! grep -Fq 'start cw-test' "$test_root/systemctl.args"
rm "$test_root/fail-stop"
"$repo_root/cw" d > "$test_root/deploy.out"
[[ -x $CW_DEPLOY_DIR/CodexWorker ]]
[[ -f $CW_DEPLOY_DIR/VERSION ]]
[[ -f $test_root/active ]]
grep -Fq 'service is active' "$test_root/deploy.out"
grep -Fxq -- '-n systemctl stop cw-test' "$test_root/sudo.args"
grep -Fxq -- '-n systemctl start cw-test' "$test_root/sudo.args"
! grep -Fq 'interactive sudo invocation' "$test_root/deploy.out"
printf 'rollback marker\n' > "$CW_DEPLOY_DIR/rollback-marker"
touch "$test_root/fail-start"
if "$repo_root/cw" deploy >"$test_root/deploy-start-fail.out" 2>&1; then
  echo 'failed service start unexpectedly succeeded' >&2; exit 1
fi
grep -Fxq 'rollback marker' "$CW_DEPLOY_DIR/rollback-marker"
grep -Fq 'systemd start failed' "$test_root/deploy-start-fail.out"
grep -Fq 'previous deployment restored' "$test_root/deploy-start-fail.out"
rm "$test_root/fail-start"

echo 'cw tests passed'
