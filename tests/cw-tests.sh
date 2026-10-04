#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
bin="$test_root/bin"
mkdir -p "$bin" "$test_root/projects" "$test_root/apps"
export CW_WET_TOOL_DIR="$test_root/development wet"
export PATH="$bin:$CW_WET_TOOL_DIR:$PATH"
export CW_DEPLOY_DIR="$test_root/apps/worker"
export CW_SERVICE_NAME=cw-test
export CW_TEST_ROOT="$test_root"
export CW_REPO_DIR="$repo_root"
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
printf '[{"tagName":"v9.8.7","name":"v9.8.7","isDraft":false,"isPrerelease":false,"publishedAt":"2026-09-01T00:00:00Z"}]\n' > "$test_root/releases.json"
printf '%s\n' '{"executionId":"00000000-0000-0000-0000-000000000001","decision":"keep","reasonCode":"recovery-changes-required","message":"Workspace contains useful changes."}' > "$test_root/inspection.json"
cat > "$bin/journalctl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$CW_TEST_ROOT/journal.args"
MOCK
cat > "$bin/curl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$CW_TEST_ROOT/curl.args"
if [[ $* == *maintenance/completed-branches* ]]; then
  cat "$CW_TEST_ROOT/maintenance.json"
elif [[ $* == *cleanup-inspection* ]]; then
  cat "$CW_TEST_ROOT/inspection.json"
else
  cat "$CW_TEST_ROOT/projects.json"
fi
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
  restart) [[ ! -f $CW_TEST_ROOT/fail-restart ]] || { echo 'Failed to restart service: Access denied' >&2; exit 1; }; touch "$CW_TEST_ROOT/active"; [[ ! -f $CW_TEST_ROOT/restart-inactive ]] || rm -f "$CW_TEST_ROOT/active" ;;
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
if [[ -f $CW_TEST_ROOT/fail-sudo-restart && ${1:-} == systemctl && ${2:-} == restart ]]; then
  echo 'sudo: a password is required' >&2
  exit 1
fi
exec "$@"
MOCK
cat > "$bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CW_TEST_ROOT/dotnet.args"
case "$1" in
  publish)
    [[ ! -f $CW_TEST_ROOT/fail-publish ]] || exit 1
    while (($#)); do
      if [[ $1 == -o ]]; then out=$2; shift 2; else shift; fi
    done
    mkdir -p "$out"
    printf '#!/bin/sh\nexit 0\n' > "$out/CodexWorker"
    chmod +x "$out/CodexWorker"
    ;;
  pack)
    [[ ! -f $CW_TEST_ROOT/fail-pack ]] || exit 1
    ;;
  tool)
    action=$2
    while (($#)); do
      if [[ $1 == --tool-path ]]; then tool_dir=$2; shift 2; else shift; fi
    done
    case "$action" in
      list) [[ ! -f $tool_dir/wet ]] || printf 'WorkExecutionToolbox.Cli 9.8.7 wet\n' ;;
      uninstall) rm -f "$tool_dir/wet" ;;
      install)
        [[ ! -f $CW_TEST_ROOT/fail-tool-install ]] || exit 1
        [[ $NUGET_PACKAGES == */wet/nuget ]] || exit 91
        mkdir -p "$tool_dir"
        printf '#!/bin/sh\necho wet development\n' > "$tool_dir/wet"
        chmod +x "$tool_dir/wet"
        ;;
      *) exit 92 ;;
    esac
    ;;
  *) exit 93 ;;
esac
MOCK
cat > "$bin/gh" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CW_TEST_ROOT/gh.args"
printf '%s\n' "${GH_REPO:-}" >> "$CW_TEST_ROOT/gh-repo.args"
[[ ! -f $CW_TEST_ROOT/fail-gh ]] || { echo 'provider unavailable' >&2; exit 17; }
cat "$CW_TEST_ROOT/releases.json"
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
git -C "$fixture_repo" remote add origin https://github.com/owner/fixture.git
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
export CW_REPO_DIR="$fixture_repo"
(
  cd "$caller_dir"
  "$fixture_bin/cw" --help | grep -Fq 'status, s'
  "$fixture_bin/cw" --help | grep -Fq 'CW_REPO_DIR'
  "$fixture_bin/cw" --help | grep -Fq 'restart, rs'
  "$fixture_bin/cw" h > "$test_root/fixture-help-h.out"
  "$fixture_bin/cw" --help > "$test_root/fixture-help-long.out"
  cmp "$test_root/fixture-help-h.out" "$test_root/fixture-help-long.out"
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
  "$fixture_bin/cw" r > "$test_root/symlink-release.out"
  grep -Fq 'v9.8.7' "$test_root/symlink-release.out"
  [[ $(tail -n 1 "$test_root/gh-repo.args") == github.com/owner/fixture ]]
  "$fixture_bin/cw" log -n 23
  grep -Fxq -- '-u cw-test -n 23 --no-pager' "$test_root/journal.args"
  "$fixture_bin/cw" l >/dev/null
  grep -Fxq -- '-u cw-test -n 100 --no-pager' "$test_root/journal.args"
  [[ ! -e $CW_WET_TOOL_DIR/wet ]]
  : > "$test_root/dotnet.args"
  gh_calls=$(wc -l < "$test_root/gh.args")
  "$fixture_bin/cw" deploy > "$test_root/symlink-deploy.out"
  grep -Fq "$fixture_repo/src/CodexWorker/CodexWorker.csproj" "$test_root/dotnet.args"
  grep -Fq '9.8.7' "$CW_DEPLOY_DIR/VERSION"
  [[ -x $CW_WET_TOOL_DIR/wet ]]
  ! grep -Fq 'tool uninstall' "$test_root/dotnet.args"
  grep -Fq 'tool install WorkExecutionToolbox.Cli' "$test_root/dotnet.args"
  grep -Fq -- '--version 9.8.7' "$test_root/dotnet.args"
  [[ $(wc -l < "$test_root/gh.args") == "$gh_calls" ]]
  : > "$test_root/dotnet.args"
  "$fixture_bin/cw" d >/dev/null
  grep -Fq 'tool uninstall WorkExecutionToolbox.Cli' "$test_root/dotnet.args"
  grep -Fq 'tool install WorkExecutionToolbox.Cli' "$test_root/dotnet.args"
  "$fixture_bin/cw" d >/dev/null
  [[ -x $CW_WET_TOOL_DIR/wet ]]
)

mkdir -p "$test_root/invalid target"
cp "$repo_root/cw" "$test_root/invalid target/cw"
ln -s "$test_root/invalid target/cw" "$fixture_bin/cw-invalid"
export CW_REPO_DIR="$test_root/missing configured repository"
if (cd "$caller_dir" && "$fixture_bin/cw-invalid" status) > "$test_root/invalid.out" 2>&1; then
  echo 'invalid repository unexpectedly succeeded' >&2; exit 1
fi
grep -Fq "does not exist: $test_root/missing configured repository" "$test_root/invalid.out"
grep -Fq 'CW_REPO_DIR' "$test_root/invalid.out"
export CW_REPO_DIR="$test_root/invalid target"
if "$fixture_bin/cw-invalid" v > "$test_root/invalid-existing.out" 2>&1; then
  echo 'non-repository configured path unexpectedly succeeded' >&2; exit 1
fi
grep -Fq "not a valid Codex Worker Git repository: $test_root/invalid target" "$test_root/invalid-existing.out"
grep -Fq 'CW_REPO_DIR' "$test_root/invalid-existing.out"

# The configured source repository wins from both a non-Git directory and an unrelated checkout.
export CW_REPO_DIR="$fixture_repo"
(
  cd "$caller_dir"
  "$fixture_bin/cw" v > "$test_root/outside-version.out"
  grep -Fq 'Product version 9.8.7' "$test_root/outside-version.out"
)
unrelated_repo="$test_root/unrelated-git"
mkdir -p "$unrelated_repo"
git -C "$unrelated_repo" init -q
(
  cd "$unrelated_repo"
  "$fixture_bin/cw" v > "$test_root/unrelated-version.out"
  grep -Fq "repository $fixture_repo" "$test_root/unrelated-version.out"
  "$fixture_bin/cw" r > "$test_root/unrelated-release.out"
  grep -Fq 'v9.8.7' "$test_root/unrelated-release.out"
  [[ $(tail -n 1 "$test_root/gh-repo.args") == github.com/owner/fixture ]]
)

# With no override, resolve the documented path below HOME, including a path containing spaces.
default_home="$test_root/default home"
mkdir -p "$default_home/projects"
cp -a "$fixture_repo" "$default_home/projects/codex-worker"
(
  cd "$caller_dir"
  env -u CW_REPO_DIR HOME="$default_home" "$fixture_bin/cw" v > "$test_root/default-version.out"
  grep -Fq "repository $default_home/projects/codex-worker" "$test_root/default-version.out"
  env CW_REPO_DIR='~/projects/codex-worker' HOME="$default_home" "$fixture_bin/cw" v > "$test_root/tilde-version.out"
  grep -Fq "repository $default_home/projects/codex-worker" "$test_root/tilde-version.out"
)

# WET failures occur before any service mutation and never report deployment success.
for failure in fail-pack fail-tool-install; do
  touch "$test_root/$failure"
  : > "$test_root/systemctl.args"
  if "$fixture_bin/cw" d > "$test_root/wet-failure.out" 2>&1; then
    echo 'WET refresh failure unexpectedly succeeded' >&2; exit 1
  fi
  grep -Fq 'rerun cw d' "$test_root/wet-failure.out"
  ! grep -Fq 'service is active' "$test_root/wet-failure.out"
  [[ ! -s $test_root/systemctl.args ]]
  rm "$test_root/$failure"
done
# A different PATH installation must not be overwritten or reported as synchronized.
printf '#!/bin/sh\necho unrelated wet\n' > "$bin/wet"
chmod +x "$bin/wet"
if "$fixture_bin/cw" d > "$test_root/wet-path.out" 2>&1; then
  echo 'shadowed WET unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'PATH resolves wet to' "$test_root/wet-path.out"
grep -Fq 'unrelated wet' "$bin/wet"
rm "$bin/wet"

# Restore defaults for the original-checkout command coverage below.
export CW_DEPLOY_DIR="$test_root/apps/worker"
export CW_REPO_DIR="$repo_root"
printf '[{"name":"Alpha","configurationPath":"%s/projects/a.yml","projectDirectory":"%s/repos/alpha","repository":"a/alpha"},{"name":"Beta","configurationPath":"%s/projects/b.yaml","projectDirectory":"%s/repos/beta","repository":"b/beta"}]\n' "$test_root" "$test_root" "$test_root" "$test_root" > "$test_root/projects.json"

"$repo_root/cw" --help | grep -Fq 'status, s'
"$repo_root/cw" --help | grep -Fq 'restart, rs'
"$repo_root/cw" --help > "$test_root/help-long.out"
"$repo_root/cw" h > "$test_root/help-h.out"
cmp "$test_root/help-h.out" "$test_root/help-long.out"
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
: > "$test_root/sudo.args"
: > "$test_root/systemctl.args"
rm -f "$test_root/active"
"$repo_root/cw" restart > "$test_root/restart.out"
grep -Fxq 'Restarted Worker service cw-test; service is active.' "$test_root/restart.out"
grep -Fxq -- '-n systemctl restart cw-test' "$test_root/sudo.args"
grep -Fxq -- 'restart cw-test' "$test_root/systemctl.args"
"$repo_root/cw" rs > "$test_root/restart-alias.out"
grep -Fq 'Restarted Worker service cw-test' "$test_root/restart-alias.out"
[[ $(grep -Fc -- '-n systemctl restart cw-test' "$test_root/sudo.args") == 2 ]]
touch "$test_root/fail-sudo-restart"
if "$repo_root/cw" restart > "$test_root/restart-sudo-fail.out" 2>&1; then
  echo 'restart unexpectedly succeeded without sudo authorization' >&2; exit 1
fi
grep -Fq 'sudo authorization failed' "$test_root/restart-sudo-fail.out"
rm "$test_root/fail-sudo-restart"
touch "$test_root/fail-restart"
if "$repo_root/cw" rs > "$test_root/restart-fail.out" 2>&1; then
  echo 'failed service restart unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'systemd restart failed' "$test_root/restart-fail.out"
! grep -Fq 'Restarted Worker service' "$test_root/restart-fail.out"
rm "$test_root/fail-restart"
touch "$test_root/restart-inactive"
if "$repo_root/cw" restart > "$test_root/restart-inactive.out" 2>&1; then
  echo 'inactive service restart unexpectedly reported success' >&2; exit 1
fi
grep -Fq 'did not become active after restart' "$test_root/restart-inactive.out"
! grep -Fq 'Restarted Worker service' "$test_root/restart-inactive.out"
rm "$test_root/restart-inactive"
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

# Version changes use isolated local repositories and a bare local origin only.
setup_version_repo() {
  local name=$1 version=${2:-9.8.7}
  local repo="$test_root/$name" remote="$test_root/$name-origin.git"
  mkdir -p "$repo/src/CodexWorker"
  cp "$repo_root/cw" "$repo/cw"
  cat > "$repo/Directory.Build.props" <<XML
<Project>
  <PropertyGroup>
    <Version>$version</Version>
  </PropertyGroup>
</Project>
XML
  printf '<Project />\n' > "$repo/src/CodexWorker/CodexWorker.csproj"
  mkdir -p "$repo/packaging"
  cat > "$repo/packaging/release.sh" <<'RELEASE'
#!/usr/bin/env bash
printf '%s\n' "$#" "$1" > "$CW_TEST_ROOT/release.args"
if [[ -f $CW_TEST_ROOT/fail-existing-tag ]]; then
  printf "Tag 'v%s' already exists on origin; refusing to overwrite it.\n" "$1" >&2
  exit 1
fi
[[ ! -f $CW_TEST_ROOT/fail-release ]] || exit "$(<"$CW_TEST_ROOT/fail-release")"
RELEASE
  chmod +x "$repo/packaging/release.sh"
  git init -q --bare "$remote"
  git -C "$repo" init -q -b main
  git -C "$repo" config user.email cw-tests@example.invalid
  git -C "$repo" config user.name cw-tests
  git -C "$repo" add cw Directory.Build.props src packaging
  git -C "$repo" commit -qm 'initial version fixture'
  git -C "$repo" remote add origin "$remote"
  git -C "$repo" push -q -u origin main
  export CW_REPO_DIR="$repo"
}

# Release publication validates prerequisites and delegates the exact version to
# the existing script. These fixtures never create tags or contact GitHub.
release_repo="$test_root/release-success"
setup_version_repo release-success
export CW_REPO_DIR="$release_repo"
printf '[{"tagName":"v9.8.7","name":"v9.8.7","isDraft":false,"isPrerelease":false,"publishedAt":"2026-09-01T00:00:00Z"},{"tagName":"v9.8.6","name":"v9.8.6","isDraft":false,"isPrerelease":false,"publishedAt":"2026-08-01T00:00:00Z"}]\n' > "$test_root/releases.json"
release_output=$("$release_repo/cw" r 9.8.7)
grep -Fq 'Publishing Worker 9.8.7' <<<"$release_output"
grep -Fq 'Release 9.8.7 published successfully' <<<"$release_output"
[[ $(sed -n '1p' "$test_root/release.args") == 1 && $(sed -n '2p' "$test_root/release.args") == 9.8.7 ]]
grep -Fq '<Version>9.8.7</Version>' "$release_repo/Directory.Build.props"
rm "$test_root/release.args"
"$release_repo/cw" release 9.8.7 >/dev/null
[[ $(sed -n '1p' "$test_root/release.args") == 1 && $(sed -n '2p' "$test_root/release.args") == 9.8.7 ]]
rm "$test_root/release.args"
if "$release_repo/cw" r 9.8.8 >"$test_root/release-mismatch.out" 2>&1; then
  echo 'release version mismatch unexpectedly succeeded' >&2; exit 1
fi
grep -Fq "run 'cw v 9.8.8'" "$test_root/release-mismatch.out"
[[ ! -f $test_root/release.args ]]
printf 'dirty\n' > "$release_repo/untracked"
if "$release_repo/cw" r 9.8.7 >"$test_root/release-dirty.out" 2>&1; then
  echo 'dirty release checkout unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'clean working tree' "$test_root/release-dirty.out"
rm "$release_repo/untracked"
git -C "$release_repo" checkout -qb feature/release-test
if "$release_repo/cw" release 9.8.7 >"$test_root/release-branch.out" 2>&1; then
  echo 'release from non-main branch unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'requires branch main' "$test_root/release-branch.out"
git -C "$release_repo" checkout -q main
git clone -q --branch main "$test_root/release-success-origin.git" "$test_root/release-other"
git -C "$test_root/release-other" config user.email cw-tests@example.invalid
git -C "$test_root/release-other" config user.name cw-tests
printf 'remote advancement\n' > "$test_root/release-other/remote-only"
git -C "$test_root/release-other" add remote-only
git -C "$test_root/release-other" commit -qm 'remote advancement'
git -C "$test_root/release-other" push -q origin main
if "$release_repo/cw" r 9.8.7 >"$test_root/release-unsynced.out" 2>&1; then
  echo 'unsynchronized release checkout unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'not synchronized' "$test_root/release-unsynced.out"
[[ ! -f $test_root/release.args ]]
git -C "$release_repo" fetch -q origin main
git -C "$release_repo" merge --ff-only -q FETCH_HEAD
printf '1\n' > "$test_root/fail-release"
release_script_code=0
if "$release_repo/cw" release 9.8.7 >"$test_root/release-script-failure.out" 2>&1; then
  echo 'release script failure unexpectedly succeeded' >&2; exit 1
else
  release_script_code=$?
fi
[[ $release_script_code == 1 ]]
grep -Fq 'release script failed with exit code 1' "$test_root/release-script-failure.out"
rm "$test_root/fail-release" "$test_root/release.args"
touch "$test_root/fail-existing-tag"
if "$release_repo/cw" r 9.8.7 >"$test_root/release-existing-tag.out" 2>&1; then
  echo 'existing release tag unexpectedly succeeded' >&2; exit 1
fi
grep -Fq "Tag 'v9.8.7' already exists on origin; refusing to overwrite it." "$test_root/release-existing-tag.out"
grep -Fq 'release script failed with exit code 1' "$test_root/release-existing-tag.out"
rm "$test_root/fail-existing-tag" "$test_root/release.args"

# Inspection uses structured provider data and does not alter repository state.
git -C "$release_repo" remote set-url origin https://github.com/owner/release-success.git
release_head=$(git -C "$release_repo" rev-parse HEAD)
release_status=$(git -C "$release_repo" status --porcelain)
: > "$test_root/gh.args"
: > "$test_root/gh-repo.args"
latest_output=$("$release_repo/cw" r)
grep -Fq 'v9.8.7  published  2026-09-01T00:00:00Z' <<<"$latest_output"
! grep -Fq 'https://' <<<"$latest_output"
long_latest_output=$("$release_repo/cw" release)
[[ "$long_latest_output" == "$latest_output" ]]
list_output=$("$release_repo/cw" r list)
[[ $(grep -c '^v9\.8\.' <<<"$list_output") == 2 ]]
[[ $(sed -n '2p' <<<"$list_output" | cut -d' ' -f1) == v9.8.7 ]]
[[ $(sed -n '3p' <<<"$list_output" | cut -d' ' -f1) == v9.8.6 ]]
long_list_output=$("$release_repo/cw" release list)
[[ "$long_list_output" == "$list_output" ]]
[[ $(wc -l < "$test_root/gh.args") == 4 ]]
while IFS= read -r query_args; do
  [[ $query_args == 'release list --limit 20 --json tagName,name,isDraft,isPrerelease,publishedAt' ]]
done < "$test_root/gh.args"
while IFS= read -r selected_repo; do
  [[ $selected_repo == github.com/owner/release-success ]]
done < "$test_root/gh-repo.args"
[[ $(git -C "$release_repo" rev-parse HEAD) == "$release_head" ]]
[[ $(git -C "$release_repo" status --porcelain) == "$release_status" ]]
printf '[]\n' > "$test_root/releases.json"
grep -Fq 'No releases found' <("$release_repo/cw" r)
grep -Fq 'No releases found' <("$release_repo/cw" release list)
printf '[{"tagName":"v9.8.7","isDraft":false,"isPrerelease":false}]\n' > "$test_root/releases.json"
touch "$test_root/fail-gh"
if "$release_repo/cw" r list >"$test_root/release-query-failure.out" 2>&1; then
  echo 'provider query failure unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'provider unavailable' "$test_root/release-query-failure.out"
rm "$test_root/fail-gh"

for malformed_kind in malformed missing; do
  malformed_release_repo="$test_root/release-$malformed_kind"
  setup_version_repo "release-$malformed_kind"
  if [[ $malformed_kind == missing ]]; then
    rm "$malformed_release_repo/Directory.Build.props"
  else
    printf '<Project><PropertyGroup><Version>bad' > "$malformed_release_repo/Directory.Build.props"
  fi
  if "$malformed_release_repo/cw" release 9.8.7 >"$test_root/release-$malformed_kind.out" 2>&1; then
    echo "$malformed_kind release version unexpectedly succeeded" >&2; exit 1
  fi
  if [[ $malformed_kind == missing ]]; then
    grep -Fq "not a valid Codex Worker Git repository: $malformed_release_repo" "$test_root/release-$malformed_kind.out"
    grep -Fq 'CW_REPO_DIR' "$test_root/release-$malformed_kind.out"
  else
    grep -Fq 'Directory.Build.props is missing, malformed, or has an ambiguous' "$test_root/release-$malformed_kind.out"
  fi
done

version_repo="$test_root/version-success"
setup_version_repo version-success
version_status_output=$("$version_repo/cw" v)
grep -Fq 'Product version 9.8.7' <<<"$version_status_output"
grep -Fq 'branch     main' <<<"$version_status_output"
grep -Fq 'worktree   clean' <<<"$version_status_output"
"$version_repo/cw" version > "$test_root/version-long-status.out"
grep -Fq 'Product version 9.8.7' "$test_root/version-long-status.out"
if "$version_repo/cw" v 9.8.7 >"$test_root/version-same.out" 2>&1; then
  echo 'same version unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'version is already 9.8.7' "$test_root/version-same.out"
if "$version_repo/cw" version 9.8.6 >"$test_root/version-downgrade.out" 2>&1; then
  echo 'version downgrade unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'downgrades are not supported' "$test_root/version-downgrade.out"
if "$version_repo/cw" v 9.8 >"$test_root/version-invalid.out" 2>&1; then
  echo 'invalid version unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'expected stable MAJOR.MINOR.PATCH' "$test_root/version-invalid.out"
touch "$version_repo/unrelated-untracked"
if "$version_repo/cw" v 9.8.8 >"$test_root/version-dirty.out" 2>&1; then
  echo 'dirty version bump unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'clean working tree' "$test_root/version-dirty.out"
rm "$version_repo/unrelated-untracked"
git -C "$version_repo" checkout -qb feature/test-version
if "$version_repo/cw" v 9.8.8 >"$test_root/version-branch.out" 2>&1; then
  echo 'wrong branch version bump unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'require branch main' "$test_root/version-branch.out"
git -C "$version_repo" checkout -q main
before_tags=$(git -C "$version_repo" tag --list)
version_success_output=$("$version_repo/cw" version 9.8.8)
grep -Fq 'Version 9.8.7 -> 9.8.8' <<<"$version_success_output"
grep -Fq 'Committed [V] 9.8.8' <<<"$version_success_output"
grep -Fq 'Pushed ' <<<"$version_success_output"
[[ $(git -C "$version_repo" log -1 --format=%s) == '[V] 9.8.8' ]]
[[ $(git -C "$version_repo" diff-tree --no-commit-id --name-only -r HEAD) == Directory.Build.props ]]
[[ $(git --git-dir="$test_root/version-success-origin.git" show main:Directory.Build.props | sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p') == 9.8.8 ]]
[[ $(git -C "$version_repo" tag --list) == "$before_tags" ]]
[[ -z $(git -C "$version_repo" status --porcelain) ]]

version_ff="$test_root/version-fast-forward"
setup_version_repo version-fast-forward
python3 - "$version_ff/Directory.Build.props" <<'PY'
import sys
path = sys.argv[1]
with open(path, "rb") as source:
    content = source.read().replace(b"<Version>9.8.7</Version>", b"<Version> 9.8.7 </Version>")
with open(path, "wb") as destination:
    destination.write(content.replace(b"\n", b"\r\n"))
PY
git -C "$version_ff" add Directory.Build.props
git -C "$version_ff" commit -qm 'format product version'
git -C "$version_ff" push -q origin main
git clone -q --branch main "$test_root/version-fast-forward-origin.git" "$test_root/version-fast-forward-other"
git -C "$test_root/version-fast-forward-other" config user.email cw-tests@example.invalid
git -C "$test_root/version-fast-forward-other" config user.name cw-tests
printf 'remote advancement\n' > "$test_root/version-fast-forward-other/remote-only"
git -C "$test_root/version-fast-forward-other" add remote-only
git -C "$test_root/version-fast-forward-other" commit -qm 'remote advancement'
git -C "$test_root/version-fast-forward-other" push -q origin main
"$version_ff/cw" v 9.8.8 > "$test_root/version-fast-forward.out"
grep -Fq 'Version 9.8.7 -> 9.8.8' "$test_root/version-fast-forward.out"
[[ -f $version_ff/remote-only ]]
[[ $(git -C "$version_ff" log -2 --format=%s | tail -1) == 'remote advancement' ]]

version_dirty_repo="$test_root/version-dirty"
setup_version_repo version-dirty
printf 'unrelated\n' > "$version_dirty_repo/local-change"
if "$version_dirty_repo/cw" v 9.8.8 >"$test_root/version-dirty2.out" 2>&1; then
  echo 'dirty working tree unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'clean working tree' "$test_root/version-dirty2.out"
[[ $(git -C "$version_dirty_repo" show HEAD:Directory.Build.props | sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p') == 9.8.7 ]]

for malformed_kind in malformed missing multiple; do
  malformed_repo="$test_root/version-$malformed_kind"
  setup_version_repo "version-$malformed_kind"
  case $malformed_kind in
    malformed) printf '<Project><PropertyGroup><Version>bad' > "$malformed_repo/Directory.Build.props" ;;
    missing) rm "$malformed_repo/Directory.Build.props" ;;
    multiple) sed -i 's#</Project>#<PropertyGroup><Version>9.8.7</Version></PropertyGroup></Project>#' "$malformed_repo/Directory.Build.props" ;;
  esac
  git -C "$malformed_repo" add -A
  git -C "$malformed_repo" commit -qm "fixture $malformed_kind props"
  git -C "$malformed_repo" push -q origin main
  if "$malformed_repo/cw" v 9.8.8 >"$test_root/version-$malformed_kind.out" 2>&1; then
    echo "$malformed_kind props unexpectedly succeeded" >&2; exit 1
  fi
  if [[ $malformed_kind == missing ]]; then
    grep -Fq "not a valid Codex Worker Git repository: $malformed_repo" "$test_root/version-$malformed_kind.out"
    grep -Fq 'CW_REPO_DIR' "$test_root/version-$malformed_kind.out"
  else
    grep -Fq 'Directory.Build.props is missing, malformed, or has an ambiguous' "$test_root/version-$malformed_kind.out"
  fi
done

version_commit_fail="$test_root/version-commit-fail"
setup_version_repo version-commit-fail
printf '#!/bin/sh\nexit 1\n' > "$version_commit_fail/.git/hooks/pre-commit"
chmod +x "$version_commit_fail/.git/hooks/pre-commit"
if "$version_commit_fail/cw" v 9.8.8 >"$test_root/version-commit-fail.out" 2>&1; then
  echo 'commit failure fixture unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'commit failed before creating a commit; the version file change is preserved locally' "$test_root/version-commit-fail.out"
grep -Fq '<Version>9.8.8</Version>' "$version_commit_fail/Directory.Build.props"
[[ $(git -C "$version_commit_fail" log -1 --format=%s) == 'initial version fixture' ]]

version_push_fail="$test_root/version-push-fail"
setup_version_repo version-push-fail
printf '#!/bin/sh\nexit 1\n' > "$test_root/version-push-fail-origin.git/hooks/pre-receive"
chmod +x "$test_root/version-push-fail-origin.git/hooks/pre-receive"
if "$version_push_fail/cw" v 9.8.8 >"$test_root/version-push-fail.out" 2>&1; then
  echo 'push failure fixture unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'push failed; commit ' "$test_root/version-push-fail.out"
[[ $(git -C "$version_push_fail" log -1 --format=%s) == '[V] 9.8.8' ]]
[[ $(git --git-dir="$test_root/version-push-fail-origin.git" log -1 --format=%s main) == 'initial version fixture' ]]

version_diverged="$test_root/version-diverged"
setup_version_repo version-diverged
git clone -q --branch main "$test_root/version-diverged-origin.git" "$test_root/version-diverged-other"
git -C "$test_root/version-diverged-other" config user.email cw-tests@example.invalid
git -C "$test_root/version-diverged-other" config user.name cw-tests
printf 'remote\n' > "$test_root/version-diverged-other/remote-only"
git -C "$test_root/version-diverged-other" add remote-only
git -C "$test_root/version-diverged-other" commit -qm 'remote advancement'
git -C "$test_root/version-diverged-other" push -q origin main
printf 'local\n' > "$version_diverged/local-only"
git -C "$version_diverged" add local-only
git -C "$version_diverged" commit -qm 'local divergence'
if "$version_diverged/cw" v 9.8.8 >"$test_root/version-diverged.out" 2>&1; then
  echo 'diverged main unexpectedly succeeded' >&2; exit 1
fi
grep -Fq 'have diverged' "$test_root/version-diverged.out"
[[ $(git -C "$version_diverged" show HEAD:Directory.Build.props | sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p') == 9.8.7 ]]

bash "$repo_root/tests/cw-executions-tests.sh"
inspection=$("$repo_root/cw" execution inspect 00000000-0000-0000-0000-000000000001)
[[ $inspection == *"keep (recovery-changes-required)"* && $inspection == *"Workspace contains useful changes."* ]]
[[ $(cat "$test_root/curl.args") == *"/api/executions/00000000-0000-0000-0000-000000000001/cleanup-inspection"* ]]
[[ $("$repo_root/cw" execution inspect 00000000-0000-0000-0000-000000000001 --json) == $(cat "$test_root/inspection.json") ]]
if "$repo_root/cw" execution inspect invalid > /dev/null 2>&1; then
  echo 'execution inspect accepted invalid ID' >&2; exit 1
fi

printf '%s\n' '{"applied":false,"deleted":0,"skipped":1,"reviewRequired":1,"eligible":1,"branches":[{"branch":"done/example","decision":"eligible","reason":"Integrated","localDeleted":false,"remoteDeleted":false}]}' > "$test_root/maintenance.json"
export CW_REPO_DIR="$repo_root"
preview=$("$repo_root/cw" maintenance completed-branches --older-than 30)
[[ $preview == *'Preview: 0 deleted, 1 skipped, 1 review required, 1 eligible'* ]]
[[ $(cat "$test_root/curl.args") == *'/api/maintenance/completed-branches'* ]]
[[ $(cat "$test_root/curl.args") == *'"apply": false'* ]]
"$repo_root/cw" maintenance completed-branches --older-than 30 --apply > /dev/null
[[ $(cat "$test_root/curl.args") == *'"apply": true'* ]]
[[ $(cat "$test_root/curl.args") == *"$repo_root"* ]]
for args in 'completed-branches' 'completed-branches --older-than 0' 'completed-branches --older-than -1' 'completed-branches --older-than 36501' 'completed-branches --older-than invalid' 'completed-branches --force' 'completed-branches --older-than 30 --limit 0' 'completed-branches --older-than 30 --limit 101' 'unknown'; do
  if "$repo_root/cw" maintenance $args > /dev/null 2>&1; then
    echo "maintenance accepted invalid arguments: $args" >&2; exit 1
  fi
done

echo 'cw tests passed'

bash "$repo_root/tests/cw-cleanup-tests.sh"

"$repo_root/cw" maintenance completed-branches --older-than 30 --limit 1 --json > "$test_root/maintenance-output.json"
python3 - "$test_root/maintenance-output.json" <<'PYTEST'
import json,sys
assert json.load(open(sys.argv[1]))["eligible"] == 1
PYTEST
[[ $(cat "$test_root/curl.args") == *'"limit": 1'* ]]

printf '%s\n' '{"applied":true,"deleted":0,"skipped":0,"reviewRequired":1,"eligible":0,"branches":[]}' > "$test_root/maintenance.json"
if "$repo_root/cw" maintenance completed-branches --older-than 30 --apply --json > "$test_root/maintenance-output.json" 2> "$test_root/maintenance-error"; then
  echo 'maintenance review result should fail apply' >&2; exit 1
fi
grep -Fq 'Some branches require manual review' "$test_root/maintenance-error"
python3 - "$test_root/maintenance-output.json" <<'PYTEST'
import json,sys
assert json.load(open(sys.argv[1]))["reviewRequired"] == 1
PYTEST
