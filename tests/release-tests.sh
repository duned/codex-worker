#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT
fake_bin="$temp_dir/bin"
mkdir -p "$fake_bin"
bash -n "$repo_root/packaging/release.sh"
bash -n "$repo_root/packaging/release-linux-x64.sh"

# Real tar and sha256sum exercise archives and checksums; only publishing is stubbed.
cat > "$fake_bin/dotnet" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == msbuild ]]; then echo 9.8.7; exit 0; fi
[[ "$1" == publish ]]
[[ ${RELEASE_CASE:-} != build-failure ]] || exit 1
version=""
output=""
project="$2"
while (($#)); do
  case "$1" in
    -p:Version=*) version="${1#-p:Version=}" ;;
    --output) output="$2"; shift ;;
  esac
  shift
done
[[ -n "$version" && -n "$output" ]]
mkdir -p "$output"
printf '%s\n' "$version" > "$output/assembly-version"
if [[ ${RELEASE_CASE:-} != missing-apphost ]]; then
  apphost="${project##*/}"
  apphost="${apphost%.csproj}"
  printf '#!/bin/sh\nexit 0\n' > "$output/$apphost"
  chmod +x "$output/$apphost"
fi
STUB
chmod +x "$fake_bin/dotnet"

before_status="$(git -C "$repo_root" status --porcelain --untracked-files=all)"
output="$(PATH="$fake_bin:$PATH" TMPDIR="$temp_dir" "$repo_root/packaging/release-linux-x64.sh")"
output_dir="${output#Created release artifacts in }"
[[ "$output_dir" == "$temp_dir"/codex-worker-artifacts.* ]]
(cd "$output_dir" && sha256sum --check checksums.txt)
[[ "$(tar -xOf "$output_dir/codex-server-9.8.7-linux-x64.tar.gz" ./VERSION)" == 9.8.7 ]]
[[ "$(git -C "$repo_root" status --porcelain --untracked-files=all)" == "$before_status" ]]
rm -rf -- "$output_dir"

explicit_output="$temp_dir/explicit"
PATH="$fake_bin:$PATH" "$repo_root/packaging/release-linux-x64.sh" --set-version 1.2.3 "$explicit_output" >/dev/null
for component in server worker; do
  [[ "$(tar -xOf "$explicit_output/codex-$component-1.2.3-linux-x64.tar.gz" ./VERSION)" == 1.2.3 ]]
  [[ "$(tar -xOf "$explicit_output/codex-$component-1.2.3-linux-x64.tar.gz" ./assembly-version)" == 1.2.3 ]]
done
(cd "$explicit_output" && sha256sum --check checksums.txt)
mkdir "$temp_dir/worker-layout"
tar -xzf "$explicit_output/codex-worker-1.2.3-linux-x64.tar.gz" -C "$temp_dir/worker-layout"
[[ -x $temp_dir/worker-layout/CodexWorker ]]
grep -Fxq 'ExecStart=/opt/codex-worker/CodexWorker /etc/codex-worker/worker.yml' "$repo_root/packaging/linux/codex-worker.service"
if PATH="$fake_bin:$PATH" TMPDIR="$temp_dir" RELEASE_CASE=missing-apphost \
  "$repo_root/packaging/release-linux-x64.sh" >"$temp_dir/layout.out" 2>&1; then exit 1; fi
grep -q 'missing the executable' "$temp_dir/layout.out"

if PATH="$fake_bin:$PATH" TMPDIR="$temp_dir" RELEASE_CASE=build-failure \
  "$repo_root/packaging/release-linux-x64.sh" >"$temp_dir/build.out" 2>&1; then exit 1; fi
[[ -z "$(find "$temp_dir" -maxdepth 1 -name 'codex-worker-artifacts.*' -print)" ]]

# An isolated source tree and stubs exercise orchestration without any Git writes
# or external-service access. Its entire file inventory must survive unchanged.
fixture="$temp_dir/source"
mkdir -p "$fixture/packaging" "$fixture/tmp"
cp "$repo_root/packaging/release.sh" "$repo_root/packaging/release-linux-x64.sh" "$fixture/packaging/"
cat > "$fake_bin/git" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == -C ]]
shift 2
case "$1" in
  rev-parse)
    [[ ${RELEASE_CASE:-} != no-commit ]] || exit 1
    if [[ ${RELEASE_CASE:-} == changed-commit && -f "$RELEASE_TEST_LOG.commit" ]]; then
      echo 9876543210987654321098765432109876543210
    else
      touch "$RELEASE_TEST_LOG.commit"
      echo 0123456789012345678901234567890123456789
    fi
    ;;
  status)
    if [[ -f "$RELEASE_TEST_LOG.status" ]]; then
      [[ ${RELEASE_CASE:-} != post-status-failure ]] || exit 1
      if [[ ${RELEASE_CASE:-} == changed-checkout ]]; then echo '?? generated'; fi
    fi
    touch "$RELEASE_TEST_LOG.status"
    case ${RELEASE_CASE:-} in
      status-failure) exit 1 ;;
      dirty-tracked) echo ' M tracked' ;;
      dirty-untracked) echo '?? untracked' ;;
    esac
    ;;
  show-ref)
    [[ ${RELEASE_CASE:-} != local-tag-error ]] || exit 128
    [[ ${RELEASE_CASE:-} == local-tag ]]
    ;;
  remote) [[ ${RELEASE_CASE:-} != no-origin ]] || exit 1; echo git@github.com:example/worker.git ;;
  ls-remote)
    case ${RELEASE_CASE:-} in remote-tag) exit 0 ;; remote-error) exit 128 ;; *) exit 2 ;; esac
    ;;
  *) echo "Unexpected Git command: $*" >&2; exit 99 ;;
esac
STUB
cat > "$fake_bin/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$RELEASE_TEST_LOG"
if [[ "$1" == auth ]]; then [[ ${RELEASE_CASE:-} != auth-failure ]]; exit; fi
[[ "$GH_REPO" == https://github.com/example/worker ]]
if [[ "$1" == api ]]; then
  if [[ "$2" == repos/\{owner\}/\{repo\}/commits/* ]]; then
    [[ ${RELEASE_CASE:-} != missing-remote-commit ]]
    exit
  fi
  [[ ${RELEASE_CASE:-} != list-failure ]] || exit 1
  if [[ ${RELEASE_CASE:-} == existing-release ]]; then echo v1.2.3; fi
  exit 0
fi
[[ "$1" == release ]]
case "$2" in
  create)
    [[ "$3" == v1.2.3 ]]
    assets=("$4" "$5" "$6")
    [[ "$*" == *'--target 0123456789012345678901234567890123456789'* ]]
    [[ "$*" == *'--draft'* ]]
    [[ "${assets[2]}" == */checksums.txt ]]
    for component in server worker; do
      archive="$(dirname "${assets[0]}")/codex-$component-1.2.3-linux-x64.tar.gz"
      [[ "$(tar -xOf "$archive" ./VERSION)" == 1.2.3 ]]
      [[ "$(tar -xOf "$archive" ./assembly-version)" == 1.2.3 ]]
    done
    (cd "$(dirname "${assets[0]}")" && sha256sum --check checksums.txt)
    [[ ${RELEASE_CASE:-} != create-failure ]]
    ;;
  edit)
    [[ "$3" == v1.2.3 && "$4" == --draft=false ]]
    [[ ${RELEASE_CASE:-} != edit-failure ]]
    ;;
  *) exit 99 ;;
esac
STUB
chmod +x "$fake_bin/git" "$fake_bin/gh"
export RELEASE_TEST_LOG="$temp_dir/gh.log"
fixture_snapshot() {
  (cd "$fixture" && find . -type f -print | sort | xargs sha256sum)
}
before_fixture="$(fixture_snapshot)"
for scenario in success dirty-tracked dirty-untracked status-failure no-commit no-origin \
  local-tag local-tag-error remote-tag remote-error auth-failure list-failure existing-release missing-remote-commit \
  build-failure create-failure edit-failure internal-temp changed-commit changed-checkout post-status-failure; do
  : > "$RELEASE_TEST_LOG"
  rm -f -- "$RELEASE_TEST_LOG.commit" "$RELEASE_TEST_LOG.status"
  release_tmp="$temp_dir"
  if [[ "$scenario" == internal-temp ]]; then release_tmp="$fixture/tmp"; fi
  if PATH="$fake_bin:$PATH" TMPDIR="$release_tmp" RELEASE_CASE="$scenario" GH_REPO=wrong/repo \
    "$fixture/packaging/release.sh" 1.2.3 >"$temp_dir/$scenario.out" 2>&1; then
    [[ "$scenario" == success ]] || { echo "Unexpected success: $scenario" >&2; exit 1; }
  else
    [[ "$scenario" != success ]] || { cat "$temp_dir/$scenario.out" >&2; exit 1; }
  fi
  [[ "$(fixture_snapshot)" == "$before_fixture" ]]
  [[ -z "$(find "$temp_dir" -name 'codex-worker-release-*' -print)" ]]
  case "$scenario" in
    success|edit-failure) rg -q '^release edit v1.2.3 --draft=false$' "$RELEASE_TEST_LOG" ;;
    create-failure) rg -q '^release create ' "$RELEASE_TEST_LOG"; ! rg -q '^release edit ' "$RELEASE_TEST_LOG" ;;
    *) ! rg -q '^release (create|edit) ' "$RELEASE_TEST_LOG" ;;
  esac
done
rg -q 'partial release before retrying' "$temp_dir/create-failure.out"
rg -q 'Publication could not be confirmed' "$temp_dir/edit-failure.out"
rg -q 'Set TMPDIR to an external directory' "$temp_dir/internal-temp.out"
for invalid in 1.2 01.2.3 v1.2.3 1.2.3-beta; do
  if "$fixture/packaging/release.sh" "$invalid" >"$temp_dir/invalid.out" 2>&1; then exit 1; fi
  rg -q 'Version must be a stable semantic version' "$temp_dir/invalid.out"
done
printf 'Release tests passed.\n'
