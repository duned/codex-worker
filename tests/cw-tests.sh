#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
bin="$test_root/bin"
mkdir -p "$bin" "$test_root/projects" "$test_root/apps"
export PATH="$bin:$PATH"
export CW_WORKER_CONFIG="$test_root/worker.yml"
export CW_DEPLOY_DIR="$test_root/apps/worker"
export CW_SERVICE_NAME=cw-test
export CW_TEST_ROOT="$test_root"
cat > "$CW_WORKER_CONFIG" <<'YAML'
projects:
  directory: projects
YAML
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
cat > "$bin/journalctl" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$CW_TEST_ROOT/journal.args"
MOCK
cat > "$bin/systemctl" <<'MOCK'
#!/usr/bin/env bash
case "$1" in
  is-active)
    if [[ ${2:-} == --quiet ]]; then shift; fi
    [[ -f $CW_TEST_ROOT/active ]] && exit 0 || exit 3
    ;;
  stop) rm -f "$CW_TEST_ROOT/active" ;;
  start) [[ ! -f $CW_TEST_ROOT/fail-start ]] || exit 1; touch "$CW_TEST_ROOT/active" ;;
  show) printf '1\n' ;;
  *) exit 0 ;;
esac
MOCK
cat > "$bin/dotnet" <<'MOCK'
#!/usr/bin/env bash
[[ ! -f $CW_TEST_ROOT/fail-publish ]] || exit 1
while (($#)); do
  if [[ $1 == -o ]]; then out=$2; shift 2; else shift; fi
done
mkdir -p "$out"
printf '#!/bin/sh\nexit 0\n' > "$out/CodexWorker"
chmod +x "$out/CodexWorker"
MOCK
chmod +x "$bin"/*

"$repo_root/cw" --help | grep -Fq 'status, s'
"$repo_root/cw" -h >/dev/null
"$repo_root/cw" help >/dev/null
"$repo_root/cw" s > "$test_root/status.out"
grep -Fq 'Repository' "$test_root/status.out"
grep -Fq 'Worker service (cw-test)' "$test_root/status.out"
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
"$repo_root/cw" d > "$test_root/deploy.out"
[[ -x $CW_DEPLOY_DIR/CodexWorker ]]
[[ -f $CW_DEPLOY_DIR/VERSION ]]
[[ -f $test_root/active ]]
grep -Fq 'service is active' "$test_root/deploy.out"
printf 'rollback marker\n' > "$CW_DEPLOY_DIR/rollback-marker"
touch "$test_root/fail-start"
if "$repo_root/cw" deploy >"$test_root/deploy-start-fail.out" 2>&1; then
  echo 'failed service start unexpectedly succeeded' >&2; exit 1
fi
grep -Fxq 'rollback marker' "$CW_DEPLOY_DIR/rollback-marker"
rm "$test_root/fail-start"

echo 'cw tests passed'
