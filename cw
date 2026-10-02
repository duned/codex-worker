#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_PATH="$(readlink -f -- "${BASH_SOURCE[0]}")"
readonly SCRIPT_DIR="$(dirname -- "$SCRIPT_PATH")"
readonly REPO_ROOT="$SCRIPT_DIR"
readonly CALLER_DIR="$PWD"
readonly service_name=${CW_SERVICE_NAME:-codex-worker}
readonly worker_api_url=${CW_WORKER_API_URL:-http://127.0.0.1:5080}
readonly deploy_dir=${CW_DEPLOY_DIR:-$HOME/apps/codex-worker}

usage() {
  cat <<'HELP'
Usage: ./cw <command> [options]

Developer and local Worker operations:
  status, s       Show repository and local Worker service status
  deploy, d       Publish this checkout to ~/apps/codex-worker and restart the service
  restart, rs     Restart the installed Worker service
  log, l          Show Worker journal (default: last 100 lines)
    -f            Follow the journal
    -n COUNT      Show COUNT lines
  projects, p     List configured projects and their YAML paths
  version, v      Show or safely bump the product version
  release, r      Publish the current product version or inspect releases
  help, -h, --help

Environment overrides:
  CW_WORKER_API_URL Worker read API (default: http://127.0.0.1:5080)
  CW_DEPLOY_DIR     Local deployment directory (default: ~/apps/codex-worker)
  CW_SERVICE_NAME   systemd service name (default: codex-worker)

cw deploy manages a system-level Worker service and requires suitable sudo
permission for non-interactive systemctl stop/start operations. It does not
create a product release or change the repository product version.

cw restart (or cw rs) restarts the installed Worker service and verifies that
it is active before reporting success.

cw v <version> (or cw version <version>) updates Directory.Build.props,
commits the version change, and pushes it to origin/main. It does not publish
a release or create a tag.

cw release <version> (or cw r <version>) publishes the already-selected
product version. Use cw v <version> first to bump and push the product version.
cw release and cw release list inspect releases without changing repository state.
HELP
}

error() { printf 'cw: %s\n' "$*" >&2; }
help_hint() { error 'Run ./cw --help for usage.'; }

need_command() {
  command -v "$1" >/dev/null 2>&1 || { error "required command is unavailable: $1"; return 1; }
}

service_mutation() {
  local action=$1 output
  if output=$(sudo -n systemctl "$action" "$service_name" 2>&1); then
    return 0
  else
    local result=$?
    if [[ $output == sudo:* || $output == *"a password is required"* || $output == *"a terminal is required"* || $output == *"not allowed to execute"* ]]; then
      error "sudo authorization failed for systemctl $action $service_name; allow this non-interactive operation"
    else
      output=${output//$'\n'/ }
      error "systemd $action failed for $service_name${output:+: $output}"
    fi
    return "$result"
  fi
}

git_value() {
  git -C "$REPO_ROOT" "$@"
}

validate_repository() {
  if [[ ${1:-} == v || ${1:-} == version || ${1:-} == r || ${1:-} == release ]]; then
    [[ -f $REPO_ROOT/src/CodexWorker/CodexWorker.csproj ]] || {
      error "resolved script directory is not a Codex Worker repository: $REPO_ROOT"
      error 'expected src/CodexWorker/CodexWorker.csproj beside the cw script'
      return 1
    }
    return 0
  fi
  [[ -r $REPO_ROOT/Directory.Build.props && -f $REPO_ROOT/src/CodexWorker/CodexWorker.csproj ]] || {
    error "resolved script directory is not a Codex Worker repository: $REPO_ROOT"
    error 'expected Directory.Build.props and src/CodexWorker/CodexWorker.csproj beside the cw script'
    return 1
  }
}

status_command() {
  need_command git || return 1
  printf 'Repository\n'
  printf '  root     %s\n' "$REPO_ROOT"
  local branch commit dirty
  branch=$(git_value branch --show-current 2>/dev/null || true)
  [[ -n $branch ]] || branch='(detached or unavailable)'
  printf '  branch   %s\n' "$branch"
  commit=$(git_value log -1 --format='%h %s' 2>/dev/null) || { error 'cannot read latest commit'; return 1; }
  printf '  commit   %s\n' "$commit"
  if [[ -n $(git_value status --porcelain 2>/dev/null) ]]; then dirty=dirty; else dirty=clean; fi
  printf '  worktree %s\n' "$dirty"
  printf '  worktrees\n'
  git_value worktree list --porcelain 2>/dev/null | awk '
    function emit() {
      if (path == "") return
      label = branch != "" ? branch : detached ? "(detached)" : "(unknown)"
      sha = substr(commit, 1, 7)
      if (length(label) > width) width = length(label)
      paths[++count] = path
      labels[count] = label
      commits[count] = sha
    }
    BEGIN { width = 0 }
    /^worktree / { emit(); path = substr($0, 10); commit = ""; branch = ""; detached = 0; next }
    /^HEAD / { commit = substr($0, 6); next }
    /^branch / { branch = substr($0, 8); sub(/^refs\/heads\//, "", branch); next }
    /^detached$/ { detached = 1; next }
    /^$/ { emit(); path = ""; next }
    END {
      emit()
      for (i = 1; i <= count; i++) printf "  - %-*s  %s  %s\n", width, labels[i], commits[i], paths[i]
    }
  '

  printf '\nWorker service (%s)\n' "$service_name"
  if ! command -v systemctl >/dev/null 2>&1; then
    printf '  state    unavailable (systemctl not found)\n'
    return 0
  fi
  local state pid version
  state=$(systemctl is-active "$service_name" 2>/dev/null || true)
  [[ -n $state ]] || state=unavailable
  printf '  state    %s\n' "$state"
  if [[ $state == active ]]; then
    pid=$(systemctl show "$service_name" --property MainPID --value 2>/dev/null || true)
    version=''
    if [[ $pid =~ ^[1-9][0-9]*$ && -r /proc/$pid/cmdline ]]; then
      local executable
      executable=$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)
      if [[ -n $executable && -r $(dirname -- "$executable")/VERSION ]]; then
        version=$(<"$(dirname -- "$executable")/VERSION")
      fi
    fi
    [[ -z $version ]] || printf '  version  %s\n' "$version"
  fi
}

read_version() {
  python3 - "$REPO_ROOT/Directory.Build.props" <<'PY'
import re
import sys
import xml.etree.ElementTree as ET
try:
    content = open(sys.argv[1], encoding="utf-8").read()
    root = ET.fromstring(content)
    nodes = [element for element in root.iter() if element.tag.split("}")[-1] == "Version"]
    matches = list(re.finditer(r"<Version>([^<>]*)</Version>", content))
    if len(nodes) != 1 or len(matches) != 1 or (nodes[0].text or "").strip() != matches[0].group(1).strip():
        raise ValueError()
    print((nodes[0].text or "").strip())
except (OSError, UnicodeError, ET.ParseError, ValueError):
    sys.exit(1)
PY
}

valid_product_version() { [[ $1 =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; }

validate_version_git_root() {
  local git_root
  git_root=$(git_value rev-parse --show-toplevel 2>/dev/null) || { error 'resolved script directory is not a Git repository'; return 1; }
  [[ $(cd -- "$git_root" && pwd -P) == $(cd -- "$REPO_ROOT" && pwd -P) ]] || {
    error 'cw script directory does not match the Git repository root'
    return 1
  }
}

version_status() {
  need_command git || return 1
  need_command python3 || return 1
  validate_version_git_root || return 1
  [[ -f $REPO_ROOT/Directory.Build.props && ! -L $REPO_ROOT/Directory.Build.props ]] || { error 'Directory.Build.props must be a regular file'; return 1; }
  local version branch dirty
  version=$(read_version) || { error 'Directory.Build.props is missing, malformed, or has an ambiguous <Version> property'; return 1; }
  valid_product_version "$version" || { error "unsupported product version in Directory.Build.props: $version"; return 1; }
  branch=$(git_value branch --show-current 2>/dev/null || true)
  [[ -n $branch ]] || branch='(detached or unavailable)'
  if [[ -n $(git_value status --porcelain 2>/dev/null) ]]; then dirty=dirty; else dirty=clean; fi
  printf 'Product version %s\n' "$version"
  printf '  repository %s\n' "$REPO_ROOT"
  printf '  branch     %s\n' "$branch"
  printf '  worktree   %s\n' "$dirty"
}

version_command() {
  (($# <= 1)) || { error 'version accepts at most one version'; help_hint; return 2; }
  (($# == 1)) || { version_status; return $?; }
  need_command git || return 1
  need_command python3 || return 1
  local requested=$1 current branch status remote_head local_head original_file next_file message
  [[ -f $REPO_ROOT/Directory.Build.props && ! -L $REPO_ROOT/Directory.Build.props ]] || { error 'Directory.Build.props must be a regular file'; return 1; }
  valid_product_version "$requested" || { error "invalid version '$requested'; expected stable MAJOR.MINOR.PATCH (for example 1.2.3)"; return 2; }
  current=$(read_version) || { error 'Directory.Build.props is missing, malformed, or has an ambiguous <Version> property'; return 1; }
  valid_product_version "$current" || { error "unsupported product version in Directory.Build.props: $current"; return 1; }
  [[ $requested != "$current" ]] || { error "version is already $current"; return 1; }
  python3 - "$current" "$requested" <<'PY'
import sys
if tuple(map(int, sys.argv[2].split("."))) <= tuple(map(int, sys.argv[1].split("."))):
    print("cw: requested version must be greater than the current version; downgrades are not supported", file=sys.stderr)
    sys.exit(1)
PY
  branch=$(git_value branch --show-current 2>/dev/null || true)
  [[ $branch == main ]] || { error "version bumps require branch main (current: ${branch:-detached})"; return 1; }
  status=$(git_value status --porcelain 2>/dev/null) || { error 'cannot inspect Git working tree'; return 1; }
  [[ -z $status ]] || { error 'version bumps require a clean working tree'; return 1; }
  validate_version_git_root || return 1
  git_value remote get-url origin >/dev/null 2>&1 || { error 'required Git remote origin is unavailable'; return 1; }

  printf 'Checking origin/main before changing the version file...\n'
  git_value fetch --quiet --no-tags origin main || { error 'fetch from origin failed; no version change was made'; return 1; }
  remote_head=$(git_value rev-parse --verify FETCH_HEAD 2>/dev/null) || { error 'origin/main was not returned by fetch; no version change was made'; return 1; }
  local_head=$(git_value rev-parse HEAD) || { error 'cannot determine current commit'; return 1; }
  if ! git_value merge-base --is-ancestor "$remote_head" "$local_head" && ! git_value merge-base --is-ancestor "$local_head" "$remote_head"; then
    error 'local main and origin/main have diverged; resolve synchronization manually; no version change was made'
    return 1
  fi
  git_value merge --ff-only "$remote_head" || { error 'main cannot be synchronized by fast-forward; no version change was made'; return 1; }
  current=$(read_version) || { error 'synchronized Directory.Build.props is missing, malformed, or ambiguous'; return 1; }
  valid_product_version "$current" || { error "unsupported product version in synchronized Directory.Build.props: $current"; return 1; }
  [[ $requested != "$current" ]] || { error "version is already $current after synchronizing origin/main"; return 1; }
  python3 - "$current" "$requested" <<'PY'
import sys
if tuple(map(int, sys.argv[2].split("."))) <= tuple(map(int, sys.argv[1].split("."))):
    print("cw: requested version must be greater than the current synchronized version; downgrades are not supported", file=sys.stderr)
    sys.exit(1)
PY

  original_file=$(mktemp) || { error 'cannot create a temporary validation file'; return 1; }
  next_file=$(mktemp "$REPO_ROOT/.Directory.Build.props.cw.XXXXXX") || { rm -f -- "$original_file"; error 'cannot create a temporary version file'; return 1; }
  cp -p -- "$REPO_ROOT/Directory.Build.props" "$original_file" || { rm -f -- "$original_file" "$next_file"; error 'cannot read Directory.Build.props'; return 1; }
  if ! python3 - "$original_file" "$next_file" "$requested" <<'PY'
import re
import os
import stat
import sys
import xml.etree.ElementTree as ET
source, destination, version = sys.argv[1:]
try:
    content = open(source, encoding="utf-8", newline="").read()
    root = ET.fromstring(content)
    nodes = [element for element in root.iter() if element.tag.split("}")[-1] == "Version"]
    matches = list(re.finditer(r"<Version>([^<>]*)</Version>", content))
    if len(nodes) != 1 or len(matches) != 1 or (nodes[0].text or "").strip() != matches[0].group(1).strip():
        raise ValueError("expected exactly one plain <Version> element")
    match = matches[0]
    old = match.group(1)
    leading, trailing = old[:len(old)-len(old.lstrip())], old[len(old.rstrip()):]
    updated = content[:match.start(1)] + leading + version + trailing + content[match.end(1):]
    ET.fromstring(updated)
    with open(destination, "w", encoding="utf-8", newline="") as output:
        output.write(updated)
    os.chmod(destination, stat.S_IMODE(os.stat(source).st_mode))
except (OSError, UnicodeError, ET.ParseError, ValueError) as error:
    print(f"cw: cannot safely update Directory.Build.props: {error}", file=sys.stderr)
    sys.exit(1)
PY
  then
    rm -f -- "$original_file" "$next_file"
    error 'version file was not changed'
    return 1
  fi
  if ! cmp -s "$REPO_ROOT/Directory.Build.props" "$next_file"; then
    mv -- "$next_file" "$REPO_ROOT/Directory.Build.props" || { rm -f -- "$original_file" "$next_file"; error 'could not install validated version file'; return 1; }
  else
    rm -f -- "$original_file" "$next_file"
    error 'version file update produced no change'
    return 1
  fi
  local changed
  changed=$(git_value diff --name-only)
  if [[ $changed != Directory.Build.props ]] || ! python3 - "$original_file" "$REPO_ROOT/Directory.Build.props" "$current" "$requested" <<'PY'
import re
import sys
import xml.etree.ElementTree as ET
original, updated, previous, requested = sys.argv[1:]
try:
    before = open(original, encoding="utf-8", newline="").read()
    after = open(updated, encoding="utf-8", newline="").read()
    before_matches = list(re.finditer(r"<Version>([^<>]*)</Version>", before))
    root = ET.fromstring(after)
    nodes = [element for element in root.iter() if element.tag.split("}")[-1] == "Version"]
    matches = list(re.finditer(r"<Version>([^<>]*)</Version>", after))
    if len(before_matches) != 1 or len(nodes) != 1 or len(matches) != 1 or (nodes[0].text or "").strip() != requested or matches[0].group(1).strip() != requested:
        raise ValueError()
    match, before_match = matches[0], before_matches[0]
    original_value = before_match.group(1)
    leading = original_value[:len(original_value)-len(original_value.lstrip())]
    trailing = original_value[len(original_value.rstrip()):]
    restored = after[:match.start(1)] + leading + previous + trailing + after[match.end(1):]
    if restored != before:
        raise ValueError()
except (OSError, UnicodeError, ET.ParseError, ValueError):
    sys.exit(1)
PY
  then
    cp -p -- "$original_file" "$REPO_ROOT/Directory.Build.props" || error 'could not restore Directory.Build.props after validation failed'
    rm -f -- "$original_file"
    error 'unexpected version file change; the file is preserved and no commit was created'
    return 1
  fi
  rm -f -- "$original_file"
  message="[V] $requested"
  printf 'Version %s -> %s; committing only Directory.Build.props...\n' "$current" "$requested"
  local pre_commit_head post_commit_head
  pre_commit_head=$(git_value rev-parse HEAD) || { error 'cannot verify current commit before version commit'; return 1; }
  if ! git_value commit --only -m "$message" -- Directory.Build.props; then
    post_commit_head=$(git_value rev-parse HEAD 2>/dev/null || true)
    if [[ -n $post_commit_head && $post_commit_head != "$pre_commit_head" ]]; then
      error "git reported a commit failure after HEAD changed to ${post_commit_head:0:12}; inspect the commit and push state manually; no push was attempted"
    else
      error 'commit failed before creating a commit; the version file change is preserved locally; inspect it and retry manually'
    fi
    return 1
  fi
  local commit actual_message committed_files
  commit=$(git_value rev-parse --short HEAD) || { error 'commit was created but its identifier could not be read'; return 1; }
  actual_message=$(git_value log -1 --format=%s)
  committed_files=$(git_value diff-tree --no-commit-id --name-only -r HEAD)
  if [[ $actual_message != "$message" || $committed_files != Directory.Build.props ]]; then
    error "commit $commit was created with unexpected contents; inspect it before pushing"
    return 1
  fi
  printf 'Committed %s (%s).\n' "$message" "$commit"
  if ! git_value push origin HEAD:main; then
    error "push failed; commit $commit is preserved locally on main; verify origin/main, then push it with 'git push origin main'"
    return 1
  fi
  printf 'Pushed %s to origin/main.\n' "$commit"
}

release_list() {
  need_command gh || return 1
  local response
  printf 'Querying repository releases...\n'
  if ! response=$(gh release list --limit 20 --json tagName,name,isDraft,isPrerelease,publishedAt 2>&1); then
    response=${response//$'\n'/ }
    error "could not query repository releases${response:+: $response}"
    return 1
  fi
  if ! python3 - "$1" "$response" <<'PY'
import json
import sys
try:
    releases = json.loads(sys.argv[2])
    if not isinstance(releases, list):
        raise ValueError()
    if not releases:
        print("No releases found.")
        sys.exit(0)
    mode = sys.argv[1]
    bounded = releases[:20]
    if mode == "latest":
        bounded = bounded[:1]
    for release in bounded:
        if not isinstance(release, dict) or not isinstance(release.get("tagName"), str):
            raise ValueError()
        tag = release["tagName"]
        state = "draft" if release.get("isDraft") else ("pre-release" if release.get("isPrerelease") else "published")
        date = release.get("publishedAt") or "date unavailable"
        url = release.get("url")
        line = f"{tag}  {state}  {date}"
        if isinstance(url, str) and url:
            line += f"  {url}"
        print(line)
except (ValueError, TypeError, json.JSONDecodeError):
    print("cw: GitHub CLI returned invalid release metadata", file=sys.stderr)
    sys.exit(1)
PY
  then
    error 'could not read repository release metadata'
    return 1
  fi
}

release_command() {
  if (($# == 0)); then
    release_list latest
    return $?
  fi
  if [[ $1 == list ]]; then
    (($# == 1)) || { error 'release list does not accept options'; help_hint; return 2; }
    release_list list
    return $?
  fi
  (($# == 1)) || { error 'release accepts one version, or the list subcommand'; help_hint; return 2; }

  need_command git || return 1
  need_command python3 || return 1
  need_command bash || return 1
  local requested=$1 current branch status remote_head local_head
  valid_product_version "$requested" || { error "invalid version '$requested'; expected stable MAJOR.MINOR.PATCH (for example 1.2.3)"; return 2; }
  [[ -f $REPO_ROOT/Directory.Build.props && ! -L $REPO_ROOT/Directory.Build.props ]] || { error 'Directory.Build.props must be a regular file'; return 1; }
  current=$(read_version) || { error 'Directory.Build.props is missing, malformed, or has an ambiguous <Version> property'; return 1; }
  valid_product_version "$current" || { error "unsupported product version in Directory.Build.props: $current"; return 1; }
  [[ $requested == "$current" ]] || { error "requested release version $requested does not match product version $current; run 'cw v $requested' and retry"; return 1; }
  branch=$(git_value branch --show-current 2>/dev/null || true)
  [[ $branch == main ]] || { error "release requires branch main (current: ${branch:-detached})"; return 1; }
  status=$(git_value status --porcelain 2>/dev/null) || { error 'cannot inspect Git working tree'; return 1; }
  [[ -z $status ]] || { error 'release requires a clean working tree'; return 1; }
  validate_version_git_root || return 1
  git_value remote get-url origin >/dev/null 2>&1 || { error 'required Git remote origin is unavailable'; return 1; }

  printf 'Checking origin/main before publishing...\n'
  git_value fetch --quiet --no-tags origin main || { error 'fetch from origin failed; release was not started'; return 1; }
  remote_head=$(git_value rev-parse --verify FETCH_HEAD 2>/dev/null) || { error 'origin/main was not returned by fetch; release was not started'; return 1; }
  local_head=$(git_value rev-parse HEAD) || { error 'cannot determine current commit'; return 1; }
  if ! git_value merge-base --is-ancestor "$remote_head" "$local_head" || ! git_value merge-base --is-ancestor "$local_head" "$remote_head"; then
    error 'local main and origin/main are not synchronized; synchronize main safely and retry; release was not started'
    return 1
  fi
  current=$(read_version) || { error 'synchronized Directory.Build.props is missing, malformed, or ambiguous'; return 1; }
  valid_product_version "$current" || { error "unsupported product version in synchronized Directory.Build.props: $current"; return 1; }
  [[ $requested == "$current" ]] || { error "requested release version $requested does not match synchronized product version $current; run 'cw v $requested' and retry"; return 1; }
  [[ -f $REPO_ROOT/packaging/release.sh && -x $REPO_ROOT/packaging/release.sh ]] || { error 'packaging/release.sh is unavailable or not executable'; return 1; }

  printf 'Publishing Worker %s with packaging/release.sh...\n' "$requested"
  if bash "$REPO_ROOT/packaging/release.sh" "$requested"; then
    printf 'Release %s published successfully.\n' "$requested"
  else
    local result=$?
    error "release script failed with exit code $result; inspect GitHub release state before retrying"
    return "$result"
  fi
}

deploy_command() {
  need_command dotnet || return 1
  need_command git || return 1
  need_command sudo || return 1
  need_command systemctl || return 1
  local version commit parent
  stage=''
  backup=''
  failed=''
  was_active=false
  stopped=false
  swapped=false
  version=$(read_version)
  [[ -n $version ]] || { error 'could not read product version from Directory.Build.props'; return 1; }
  commit=$(git_value rev-parse --short HEAD) || { error 'cannot determine checkout commit'; return 1; }
  parent=$(dirname -- "$deploy_dir")
  mkdir -p -- "$parent" || { error "cannot create deployment parent: $parent"; return 1; }
  stage=$(mktemp -d "$parent/.cw-stage.XXXXXX") || { error "cannot stage deployment under $parent"; return 1; }
  backup="$parent/.cw-previous.$(date -u +%Y%m%dT%H%M%SZ).$$"
  cleanup_deploy() {
    local result=$?
    rm -rf -- "$stage"
    if ((result != 0)) && [[ $swapped == true ]]; then
      if [[ -d $deploy_dir ]]; then
        failed="$(dirname -- "$deploy_dir")/.cw-failed.$(date -u +%Y%m%dT%H%M%SZ).$$"
        mv -- "$deploy_dir" "$failed" || error "could not preserve failed deployment at $deploy_dir"
      fi
      if [[ -d $backup ]]; then
        mv -- "$backup" "$deploy_dir" || error "could not restore previous deployment from $backup"
      fi
      if [[ $was_active == true ]]; then
        service_mutation start || error "could not restart previous Worker service"
      fi
      error "deployment failed; previous deployment restored when possible${failed:+; failed files preserved at $failed}"
    fi
    if ((result != 0)) && [[ $swapped != true && $was_active == true && $stopped == true ]]; then
      service_mutation start || error 'could not restart Worker after the failed replacement attempt'
    fi
    return "$result"
  }
  trap cleanup_deploy EXIT

  printf 'Publishing Worker %s (%s)...\n' "$version" "$commit"
  dotnet publish "$REPO_ROOT/src/CodexWorker/CodexWorker.csproj" -c Release -o "$stage/publish" || {
    error 'publish failed; active deployment was not changed'; return 1;
  }
  [[ -x $stage/publish/CodexWorker || -f $stage/publish/CodexWorker.dll ]] || {
    error 'publish succeeded but produced no CodexWorker executable or DLL'; return 1;
  }
  printf '%s\n' "$version" > "$stage/publish/VERSION"
  systemctl is-active --quiet "$service_name" && was_active=true
  if [[ $was_active == true ]]; then
    service_mutation stop || return 1
    stopped=true
  fi
  if [[ -e $deploy_dir ]]; then mv -- "$deploy_dir" "$backup" || { error "could not preserve existing deployment at $deploy_dir"; return 1; }; fi
  if ! mv -- "$stage/publish" "$deploy_dir"; then
    [[ ! -d $backup ]] || mv -- "$backup" "$deploy_dir"
    error 'could not install staged publish'; return 1
  fi
  swapped=true
  service_mutation start || return 1
  local healthy=false
  for _ in {1..30}; do
    if systemctl is-active --quiet "$service_name"; then healthy=true; break; fi
    sleep 1
  done
  [[ $healthy == true ]] || { error 'Worker service started but failed active/running verification'; return 1; }
  swapped=false
  trap - EXIT
  rm -rf -- "$stage"
  printf 'Deployed Worker %s from %s; service is active.\n' "$version" "$commit"
  if [[ -d $backup ]]; then printf 'Previous deployment preserved at %s\n' "$backup"; fi
}

restart_command() {
  need_command sudo || return 1
  need_command systemctl || return 1
  service_mutation restart || return 1
  if ! systemctl is-active --quiet "$service_name"; then
    error "Worker service $service_name did not become active after restart"
    return 1
  fi
  printf 'Restarted Worker service %s; service is active.\n' "$service_name"
}

log_command() {
  need_command journalctl || return 1
  local follow=false lines=100
  while (($#)); do
    case $1 in
      -f|--follow) [[ $follow == false ]] || { error 'follow option was specified more than once'; help_hint; return 2; }; follow=true; shift ;;
      -n|--lines)
        (($# >= 2)) || { error "$1 requires a line count"; help_hint; return 2; }
        [[ $2 =~ ^[1-9][0-9]*$ ]] || { error "invalid line count: $2"; help_hint; return 2; }
        lines=$2; shift 2 ;;
      *) error "unknown log option: $1"; help_hint; return 2 ;;
    esac
  done
  if [[ $follow == true ]]; then journalctl -u "$service_name" -n "$lines" -f --no-pager
  else journalctl -u "$service_name" -n "$lines" --no-pager
  fi
}

projects_command() {
  need_command curl || return 1
  need_command python3 || return 1
  local response
  response=$(curl --fail --silent --show-error "${worker_api_url%/}/api/projects") || {
    error "cannot read the running Worker's project list from ${worker_api_url%/}/api/projects"
    return 1
  }
  printf 'Projects\n'
  if ! python3 -c 'import json, sys
try:
    projects = json.load(sys.stdin)
    if not isinstance(projects, list): raise ValueError()
    for project in projects:
        name, path = project["name"], project["configurationPath"]
        directory, repository = project.get("projectDirectory", ""), project.get("repository", "")
        if not isinstance(name, str) or not name or not isinstance(path, str) or not path: raise ValueError()
        print(f"\n{name}\n  config  {path}")
        if directory: print(f"  repo    {directory}")
        if repository: print(f"  origin  {repository}")
except (ValueError, KeyError, TypeError, json.JSONDecodeError):
    sys.exit(1)' <<<"$response"; then
    error 'Worker project API returned invalid project metadata'
    return 1
  fi
}

main() {
  (($#)) || { usage; return 0; }
  local command=$1; shift
  case $command in
    --help|-h|help|h) ;;
    *) validate_repository "$command" || return 1 ;;
  esac
  case $command in
    --help|-h|help|h) (($# == 0)) || { error 'help does not accept options'; help_hint; return 2; }; usage ;;
    status|s) (($# == 0)) || { error 'status does not accept options'; help_hint; return 2; }; status_command ;;
    deploy|d) (($# == 0)) || { error 'deploy does not accept options'; help_hint; return 2; }; deploy_command ;;
    restart|rs) (($# == 0)) || { error 'restart does not accept options'; help_hint; return 2; }; restart_command ;;
    log|l) log_command "$@" ;;
    projects|p) (($# == 0)) || { error 'projects does not accept options'; help_hint; return 2; }; projects_command ;;
    version|v) version_command "$@" ;;
    release|r) release_command "$@" ;;
    *) error "unknown command: $command"; help_hint; return 2 ;;
  esac
}

main "$@"
