#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT=''
readonly service_name=${CW_SERVICE_NAME:-codex-worker}
readonly worker_api_url=${CW_WORKER_API_URL:-http://127.0.0.1:5080}
readonly deploy_dir=${CW_DEPLOY_DIR:-$HOME/apps/codex-worker}
readonly configured_repo_dir=${CW_REPO_DIR:-$HOME/projects/codex-worker}
readonly wet_tool_dir=${CW_WET_TOOL_DIR:-$HOME/.local/share/wet}

usage() {
  cat <<'HELP'
Usage: ./cw <command> [options]

Developer and local Worker operations:
  status, s       Show repository and local Worker service status
  deploy, d       Refresh development WET, publish Worker and restart the service
  dashboard deploy, dd  Build/check and deploy the local development Server dashboard
  restart, rs     Restart the installed Worker service
  log, l          Show Worker journal (default: last 100 lines)
    -f            Follow the journal
    -n COUNT      Show COUNT lines
  projects, p     List configured projects and their YAML paths
  executions, e   Inspect Worker execution history
    list          List recent executions (default, at most 100)
    show ID       Show an exact execution
    show --issue NUMBER  Show all known attempts, grouped by project/repository
  execution inspect ID [--json]  Inspect retained resources through the Worker API
  maintenance completed-branches --older-than DAYS [--limit 1..100] [--apply] [--json]
                  Preview or prune integrated archives for this source checkout
  execution cleanup ID | --issue NUMBER | --stale [--limit 1..100] [--apply] [--json]
                  Dry-run by default; apply requires a completed Worker drain
  version, v      Show or safely bump the product version
  release, r      Publish the current product version or inspect releases
  help, -h, --help

Environment overrides:
  CW_WORKER_API_URL Worker read API (default: http://127.0.0.1:5080)
  CW_DEPLOY_DIR     Local deployment directory (default: ~/apps/codex-worker)
  CW_SERVICE_NAME   systemd service name (default: codex-worker)
  CW_REPO_DIR       Codex Worker source repository (default: ~/projects/codex-worker)
  CW_WET_TOOL_DIR   Development WET tool directory (default: ~/.local/share/wet)

cw deploy manages a system-level Worker service and requires suitable sudo
permission for non-interactive systemctl stop/start operations. It does not
create a product release or change the repository product version.
Deploy manages ~/.local/bin/wet; that directory must be on your login-shell PATH.
Shell profiles are never edited. Bash and Zsh login shells are supported.

cw dashboard deploy (or cw dd) requires an installed local codex-server with
explicit systemd Environment entries DOTNET_ENVIRONMENT=Development and
CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR pointing to a dedicated writable directory.
It probes installed override support before activation (upgrade Server once if absent),
runs npm ci/check, and restarts only codex-server. Success requires served HTML/assets
to match; verification failure restores previous assets. Loopback verification defaults
to http://127.0.0.1:5090; set CODEX_SERVER_DEVELOPMENT_DASHBOARD_VERIFY_URL in the
systemd drop-in for another loopback origin (normal TLS verification applies).
See docs/server-dashboard-react.md for VM1 setup and rollback instructions.

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

resolve_repository() {
  local path=$configured_repo_dir
  case $path in
    '~') path=$HOME ;;
    '~/'*) path="$HOME/${path:2}" ;;
  esac
  if [[ ! -d $path ]]; then
    error "Codex Worker repository directory does not exist: $path (set CW_REPO_DIR to the repository location)"
    return 1
  fi
  REPO_ROOT=$(cd -- "$path" 2>/dev/null && pwd -P) || {
    error "cannot resolve Codex Worker repository directory: $path (check CW_REPO_DIR)"
    return 1
  }
  local git_root
  git_root=$(git -C "$REPO_ROOT" rev-parse --show-toplevel 2>/dev/null) || git_root=''
  if [[ ! -r $REPO_ROOT/Directory.Build.props || ! -f $REPO_ROOT/src/CodexWorker/CodexWorker.csproj ||
        -z $git_root || $(cd -- "$git_root" 2>/dev/null && pwd -P) != "$REPO_ROOT" ]]; then
    error "configured path is not a valid Codex Worker Git repository: $REPO_ROOT (set CW_REPO_DIR to the repository location)"
    return 1
  fi
}

validate_repository() {
  resolve_repository
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
  git_root=$(git_value rev-parse --show-toplevel 2>/dev/null) || { error "configured path is not a Git repository: $REPO_ROOT (check CW_REPO_DIR)"; return 1; }
  [[ $(cd -- "$git_root" && pwd -P) == $(cd -- "$REPO_ROOT" && pwd -P) ]] || {
    error 'CW_REPO_DIR must name the Git repository root'
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
  local response origin selector
  origin=$(git_value remote get-url origin 2>/dev/null) || { error "Git remote origin is unavailable in $REPO_ROOT; cannot select repository releases"; return 1; }
  selector=$(github_repository_selector "$origin") || { error "cannot determine GitHub repository from origin in $REPO_ROOT"; return 1; }
  printf 'Querying repository releases...\n'
  if ! response=$(GH_REPO="$selector" gh release list --limit 20 --json tagName,name,isDraft,isPrerelease,publishedAt 2>&1); then
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

github_repository_selector() {
  local origin=$1 host path
  case $origin in
    git@*:* ) host=${origin#git@}; host=${host%%:*}; path=${origin#*:}; path="$host/$path" ;;
    ssh://git@*/* ) host=${origin#ssh://git@}; host=${host%%/*}; path=${origin#ssh://git@*/}; path="$host/$path" ;;
    https://*/* ) path=${origin#https://} ;;
    http://*/* ) path=${origin#http://} ;;
    * ) return 1 ;;
  esac
  path=${path%.git}
  [[ $path =~ ^[^/]+/[^/]+/[^/]+$ ]] || return 1
  printf '%s\n' "$path"
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

# Exact launcher content identifies entries owned by cw, including an old tool path.
wet_launcher() {
  printf '#!/usr/bin/env bash\n# cw managed development WET\n# target: %s\nexec %q "$@"\n' "$1" "$1"
}

install_development_wet_command() {
  local target=$1 command_dir=$HOME/.local/bin entry=$HOME/.local/bin/wet previous='' temporary
  mkdir -p -- "$command_dir" || { error "cannot create user command directory: $command_dir"; return 1; }
  if [[ -e $entry || -L $entry ]]; then
    if [[ -f $entry && ! -L $entry ]]; then
      previous=$(sed -n '3s/^# target: //p' "$entry")
    fi
    if [[ $previous != /* ]] || ! cmp -s -- "$entry" <(wet_launcher "$previous"); then
      error "refusing to overwrite unrelated command or symlink at $entry; move it aside yourself or choose which WET installation to keep, then rerun cw d"
      return 1
    fi
  fi
  temporary=$(mktemp "$command_dir/.cw-wet.XXXXXX") || return 1
  if ! wet_launcher "$target" > "$temporary" || ! chmod 755 "$temporary"; then
    rm -f -- "$temporary"
    error "cannot stage development WET command at $entry"; return 1
  fi
  if [[ -n ${previous:-} ]]; then
    mv -T -- "$temporary" "$entry" || { rm -f -- "$temporary"; error "cannot update managed WET command at $entry"; return 1; }
  else
    # Do not overwrite a command created after the initial existence check.
    ln -- "$temporary" "$entry" || { rm -f -- "$temporary"; error "cannot create WET command at $entry; inspect the existing entry and rerun cw d"; return 1; }
    rm -f -- "$temporary"
  fi
}

verify_development_wet_command() {
  local entry=$HOME/.local/bin/wet resolved login_shell=${SHELL:-/bin/bash}
  case ":$PATH:" in
    *":$HOME/.local/bin:"*) ;;
    *) error "user command directory $HOME/.local/bin is missing from PATH; configure your current and login-shell PATH explicitly and rerun cw d; shell profiles were not edited"; return 1 ;;
  esac
  resolved=$(command -v wet || true)
  if [[ -z $resolved || $(readlink -f -- "$resolved") != $(readlink -f -- "$entry") ]]; then
    error "development WET command is $entry, but PATH resolves wet to ${resolved:-nothing}; put $HOME/.local/bin before other WET installations in PATH and rerun cw d (use 'type -a wet' to inspect installations)"
    return 1
  fi
  case ${login_shell##*/} in
    bash|zsh) ;;
    *) error "cannot verify unsupported login shell $login_shell; use Bash or Zsh with $HOME/.local/bin on its login PATH and rerun cw d"; return 1 ;;
  esac
  "$login_shell" -lc '
    case ":$PATH:" in
      *":$1:"*) ;;
      *) printf "cw: user command directory %s is missing from login-shell PATH; configure your login PATH explicitly and rerun cw d; shell profiles were not edited\n" "$1" >&2; exit 1 ;;
    esac
    resolved=$(command -v wet || true)
    if [ "$resolved" != "$2" ]; then
      printf "cw: login-shell PATH resolves wet to %s instead of %s; fix PATH precedence and rerun cw d (use type -a wet)\n" "${resolved:-nothing}" "$2" >&2
      exit 1
    fi
  ' cw-wet-login "$HOME/.local/bin" "$entry" || return 1
}

refresh_development_wet() {
  local version=$1 staging=$2 installed target reported
  printf 'Packaging development WET %s...\n' "$version"
  # Worker publish builds the library, but does not build the independent CLI.
  dotnet pack "$REPO_ROOT/src/WorkExecutionToolbox.Cli/WorkExecutionToolbox.Cli.csproj" -c Release -o "$staging/packages" || {
    error 'WET packaging failed; development wet may be stale; fix the pack error and rerun cw d'; return 1;
  }
  installed=$(dotnet tool list --tool-path "$wet_tool_dir") || {
    error "cannot inspect development WET installation at $wet_tool_dir; rerun cw d after fixing tool access"; return 1;
  }
  if awk 'tolower($1) == "workexecutiontoolbox.cli" { found=1 } END { exit !found }' <<< "$installed"; then
    # Reinstall even when the source package version has not changed.
    dotnet tool uninstall WorkExecutionToolbox.Cli --tool-path "$wet_tool_dir" || {
      error "cannot replace development WET at $wet_tool_dir; fix tool access and rerun cw d"; return 1;
    }
  fi
  # An isolated package cache prevents an older package with the same version being reused.
  NUGET_PACKAGES="$staging/nuget" dotnet tool install WorkExecutionToolbox.Cli \
    --source "$staging/packages" --version "$version" --tool-path "$wet_tool_dir" || {
    error "WET installation failed at $wet_tool_dir; development wet is not synchronized; fix the install error and rerun cw d"; return 1;
  }
  installed=$(dotnet tool list --tool-path "$wet_tool_dir") || { error 'cannot verify installed WET package'; return 1; }
  awk -v version="$version" 'tolower($1) == "workexecutiontoolbox.cli" && $2 == version { found=1 } END { exit !found }' <<< "$installed" || {
    error "installed WET package does not match repository version $version; rerun cw d"; return 1;
  }
  target=$(readlink -f -- "$wet_tool_dir/wet")
  [[ -x $target && $target != *$'\n'* ]] || { error "installed WET executable is unavailable or has an unsupported path: $wet_tool_dir/wet"; return 1; }
  reported=$("$target" --version) || { error 'installed WET version check failed; rerun cw d'; return 1; }
  [[ $reported == "wet $version" ]] || { error "installed WET executable does not report repository version $version; rerun cw d"; return 1; }
  install_development_wet_command "$target" || return 1
  verify_development_wet_command || return 1
  reported=$("$HOME/.local/bin/wet" --version) || { error 'managed WET command version check failed; rerun cw d'; return 1; }
  [[ $reported == "wet $version" ]] || { error "managed WET command does not report repository version $version; rerun cw d"; return 1; }
  printf 'Development WET %s synchronized at %s/wet.\n' "$version" "$wet_tool_dir"
}

deploy_command() {
  need_command dotnet || return 1
  need_command git || return 1
  need_command awk || return 1
  need_command readlink || return 1
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
  refresh_development_wet "$version" "$stage/wet" || return 1
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

executions_command() {
  need_command curl || return 1
  need_command python3 || return 1
  local mode=${1:-list} path='/api/executions' response status
  case $mode in
    list) (($# <= 1)) || { error 'usage: cw executions list'; return 2; } ;;
    show)
      if (($# == 3)) && [[ $2 == --issue && $3 =~ ^[1-9][0-9]*$ && ${#3} -le 10 ]] && ((10#$3 <= 2147483647)); then
        path="/api/executions/issue/$3"
        mode=issue
      elif (($# == 2)) && [[ $2 =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]]; then
        path="/api/executions/$2"
      else
        error 'usage: cw executions show <execution-id> | cw executions show --issue <positive-number>'
        return 2
      fi ;;
    *) error 'usage: cw executions list | cw executions show <execution-id> | cw executions show --issue <number>'; return 2 ;;
  esac
  response=$(curl --silent --show-error --connect-timeout 5 --max-time 30 --write-out $'\n%{http_code}' "${worker_api_url%/}$path" 2>/dev/null) || {
    error 'cannot read Worker execution history: Management API unavailable; check the Worker service and CW_WORKER_API_URL'
    return 1
  }
  status=${response##*$'\n'}
  response=${response%$'\n'*}
  if [[ $status == 404 ]]; then
    error 'Worker execution history not found for the requested execution or Issue'
    return 1
  elif [[ $status != 200 ]]; then
    error "Worker execution history API request failed (HTTP $status)"
    return 1
  fi
  if ! python3 -c 'import json, sys, uuid
mode = sys.argv[1]
def text(value, limit=200):
    return "".join(c if c.isprintable() else " " for c in str(value))[:limit]
try:
    data = json.load(sys.stdin)
    entries = [data] if mode == "show" else data
    if not isinstance(entries, list): raise ValueError()
    lines = ["Executions" if mode == "list" else "Execution history"]
    if not entries: lines.append("  No executions found.")
    group = None
    for e in entries:
        if not isinstance(e, dict): raise ValueError()
        for key in ("executionId", "project", "repository", "issueTitle", "state", "startedAtUtc"):
            if not isinstance(e.get(key), str) or not e[key]: raise ValueError()
        uuid.UUID(e["executionId"])
        for key in ("issueNumber", "attemptNumber", "repairCount"):
            if type(e.get(key)) is not int or e[key] < (0 if key == "repairCount" else 1): raise ValueError()
        if type(e.get("resumed")) is not bool: raise ValueError()
        for key in ("result", "completedAtUtc", "validationOutcome", "retryOfExecutionId", "featureBranch", "baseBranch", "completedBranch", "commitSha", "integrationBranch", "recoveryState", "recoveryBaseCommit", "recoveryStatus", "recoveryExpiresAtUtc", "failureReason", "reportingFailure"):
            if e.get(key) is not None and not isinstance(e[key], str): raise ValueError()
        if e.get("retryOfExecutionId") is not None: uuid.UUID(e["retryOfExecutionId"])
        duration = e.get("durationMilliseconds")
        if duration is not None and (type(duration) is not int or duration < 0): raise ValueError()
        repairs = e.get("repairs")
        if not isinstance(repairs, list): raise ValueError()
        for r in repairs:
            if not isinstance(r, dict) or type(r.get("attempt")) is not int or type(r.get("maximumAttempts")) is not int or type(r.get("passedAfterRepair")) is not bool: raise ValueError()
        identity = (e["project"], e["repository"], e["issueNumber"])
        if mode != "list" and group != identity:
            lines.append("\n{} · {} · #{} {}".format(text(e["project"]), text(e["repository"]), e["issueNumber"], text(e["issueTitle"])))
            group = identity
        state = text(e["state"])
        if e.get("result"): state += " / " + text(e["result"])
        lines.append("\n  Attempt {} · {} · {}".format(e["attemptNumber"], e["executionId"], state))
        if mode == "list":
            lines.append("    {} · {} · #{} {}".format(text(e["project"]), text(e["repository"]), e["issueNumber"], text(e["issueTitle"])))
        if e.get("retryOfExecutionId"):
            lines.append("    {} {}".format("resumed from" if e["resumed"] else "retry of", e["retryOfExecutionId"]))
        elif e["resumed"]: lines.append("    resumed")
        timing = "    started " + text(e["startedAtUtc"])
        timing += " · completed " + text(e["completedAtUtc"]) if e.get("completedAtUtc") else " · in progress"
        if duration is not None: timing += " · {:.1f}s".format(duration / 1000)
        lines.append(timing)
        for label, key in (("feature", "featureBranch"), ("base", "baseBranch"), ("completed branch", "completedBranch"), ("commit", "commitSha"), ("integration", "integrationBranch"), ("recovery", "recoveryState"), ("recovery base", "recoveryBaseCommit"), ("recovery status", "recoveryStatus"), ("expires", "recoveryExpiresAtUtc")):
            if mode == "list" and key not in ("recoveryState", "recoveryExpiresAtUtc"): continue
            if e.get(key): lines.append("    {}  {}".format(label, text(e[key])))
        lines.append("    validation {} · repairs {}".format(text(e.get("validationOutcome") or "not recorded"), e["repairCount"]))
        if mode != "list":
            for r in repairs:
                lines.append("      repair {}/{} · {}".format(r["attempt"], r["maximumAttempts"], "passed" if r["passedAfterRepair"] else "did not pass"))
        for label, key in (("failure", "failureReason"), ("reporting", "reportingFailure")):
            if e.get(key): lines.append("    {}  {}".format(label, text(e[key], 1000)))
    print("\n".join(lines))
except (ValueError, KeyError, TypeError, AttributeError):
    sys.exit(1)' "$mode" <<<"$response"; then
    error 'Worker execution history API returned invalid execution metadata'
    return 1
  fi
}

execution_cleanup_command() {
  local selector='' value='' limit=20 apply=false json=false response body
  while (($#)); do
    case $1 in
      --issue) [[ $# -ge 2 && $2 =~ ^[1-9][0-9]*$ && -z $selector ]] || { error 'Invalid cleanup Issue selector'; return 2; }; selector=issueNumber; value=$2; shift 2 ;;
      --stale) [[ -z $selector ]] || { error 'Select exactly one cleanup target'; return 2; }; selector=stale; value=true; shift ;;
      --limit) [[ $# -ge 2 && $2 =~ ^[1-9][0-9]?$|^100$ ]] || { error 'Cleanup limit must be 1 through 100'; return 2; }; limit=$2; shift 2 ;;
      --apply) apply=true; shift ;;
      --json) json=true; shift ;;
      *) [[ -z $selector && $1 =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]] || { error 'Usage: cw execution cleanup ID | --issue NUMBER | --stale [--limit 1..100] [--apply] [--json]'; return 2; }; selector=executionId; value=$1; shift ;;
    esac
  done
  [[ -n $selector ]] || { error 'Select an execution ID, --issue NUMBER, or --stale'; return 2; }
  need_command curl || return 1
  need_command python3 || return 1
  body=$(python3 -c 'import json,sys
key,value,limit,apply=sys.argv[1:]
print(json.dumps({key: value if key == "executionId" else int(value) if key == "issueNumber" else True, "limit": int(limit), "apply": apply == "true"}))' "$selector" "$value" "$limit" "$apply") || return 1
  response=$(curl --fail-with-body --silent --show-error --max-time 300 -X POST -H 'Content-Type: application/json' --data "$body" "${worker_api_url%/}/api/executions/cleanup") || {
    printf '%s\n' "$response" >&2
    error 'Worker cleanup request failed; inspect Worker drain and resource state'; return 1
  }
  if [[ $json == true ]]; then printf '%s\n' "$response"; fi
  python3 -c 'import json,sys
try:
    results=json.load(sys.stdin)
    if sys.argv[1] != "true":
        if not results: print("No executions matched the bounded selection.")
        for r in results:
            i=r["inspection"]
            print("{}: {} ({}) — {}".format(i["executionId"],r["outcome"],i["reasonCode"],i["message"]))
    sys.exit(1 if any(r["outcome"] in ("refused", "failed") for r in results) else 0)
except (ValueError,KeyError,TypeError): sys.exit(1)' "$json" <<<"$response"
}

execution_command() {
  if [[ ${1:-} == cleanup ]]; then shift; execution_cleanup_command "$@"; return $?; fi
  if [[ $# -lt 2 || $# -gt 3 || $1 != inspect || ! $2 =~ ^[[:xdigit:]]{8}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{4}-[[:xdigit:]]{12}$ ]]; then
    error 'Usage: cw execution inspect EXECUTION_ID [--json]'; return 2
  fi
  if [[ $# == 3 && $3 != --json ]]; then
    error 'Usage: cw execution inspect EXECUTION_ID [--json]'; return 2
  fi
  need_command curl || return 1
  need_command python3 || return 1
  local response
  response=$(curl --fail --silent --show-error --max-time 60 "${worker_api_url%/}/api/executions/$2/cleanup-inspection") || {
    error 'cannot inspect execution through the running Worker API'; return 1
  }
  if [[ $# == 3 ]]; then printf '%s\n' "$response"; return 0; fi
  python3 -c 'import json, sys
try:
    result = json.load(sys.stdin)
    print("{}: {} ({})".format(result["executionId"], result["decision"], result["reasonCode"]))
    print(result["message"])
    if result.get("newerExecutionId"): print("Newer attempt: " + result["newerExecutionId"])
    if result.get("authoritativeBaseCommit"): print("Base: " + result["authoritativeBase"] + " @ " + result["authoritativeBaseCommit"])
except (ValueError, KeyError, TypeError):
    sys.exit(1)' <<<"$response" || { error 'Worker returned invalid inspection metadata'; return 1; }
}

maintenance_command() {
  [[ ${1:-} == completed-branches ]] || { error 'usage: cw maintenance completed-branches --older-than DAYS [--limit 1..100] [--apply] [--json]'; return 2; }
  shift
  local days='' apply=false limit=20 json=false response payload result
  while (($#)); do
    case $1 in
      --older-than) [[ $# -ge 2 && $2 =~ ^[0-9]+$ ]] || { error '--older-than requires days (1-36500)'; return 2; }; days=$2; shift 2 ;;
      --limit) [[ $# -ge 2 && $2 =~ ^[1-9][0-9]{0,2}$ ]] && ((10#$2 <= 100)) || { error '--limit requires 1..100'; return 2; }; limit=$2; shift 2 ;;
      --json) json=true; shift ;;
      --apply) apply=true; shift ;;
      *) error "unknown maintenance option: $1"; return 2 ;;
    esac
  done
  [[ -n $days && ${#days} -le 5 ]] && ((10#$days >= 1 && 10#$days <= 36500)) || { error '--older-than requires days (1-36500)'; return 2; }
  need_command curl || return 1
  need_command python3 || return 1
  payload=$(python3 -c 'import json,sys; print(json.dumps({"repositoryDirectory":sys.argv[1],"olderThanDays":int(sys.argv[2]),"apply":sys.argv[3]=="true","limit":int(sys.argv[4])}))' "$REPO_ROOT" "$days" "$apply" "$limit")
  response=$(curl --fail-with-body --silent --show-error --max-time 600 -H 'Content-Type: application/json' --data "$payload" "${worker_api_url%/}/api/maintenance/completed-branches") || {
    [[ -z ${response:-} ]] || printf '%s\n' "$response" >&2
    error 'Worker maintenance request failed; inspect Worker/repository state before retrying'; return 1
  }
  python3 -c 'import json,sys
try:
    r=json.load(sys.stdin)
    if sys.argv[1] == "true":
        print(json.dumps(r,indent=2))
    else:
        print("{}: {} deleted, {} skipped, {} review required, {} eligible".format("Apply" if r["applied"] else "Preview",r["deleted"],r["skipped"],r["reviewRequired"],r["eligible"]))
        for b in r["branches"]:
            print("{}: {} — {} (local deleted: {}, remote deleted: {})".format(b["branch"],b["decision"],b["reason"],b["localDeleted"],b["remoteDeleted"]))
    if r["applied"] and r["reviewRequired"]: sys.exit(2)
except (ValueError,KeyError,TypeError):
    sys.exit(1)' "$json" <<<"$response" || {
    result=$?
    if [[ $result == 2 ]]; then error 'Some branches require manual review; inspect the reported reasons'; else error 'Worker returned invalid maintenance metadata'; fi
    return "$result"
  }
}

dashboard_deploy() {
  local prerequisite
  for prerequisite in node npm python3 systemctl sudo; do need_command "$prerequisite" || return 1; done
  python3 "$REPO_ROOT/tools/dashboard-deploy.py" "$REPO_ROOT"
}

main() {
  (($#)) || { usage; return 0; }
  local command=$1; shift
  case $command in
    --help|-h|help|h) ;;
    status|s|deploy|d|dashboard|dd|version|v|release|r|maintenance) validate_repository "$command" || return 1 ;;
  esac
  case $command in
    --help|-h|help|h) (($# == 0)) || { error 'help does not accept options'; help_hint; return 2; }; usage ;;
    status|s) (($# == 0)) || { error 'status does not accept options'; help_hint; return 2; }; status_command ;;
    deploy|d) (($# == 0)) || { error 'deploy does not accept options'; help_hint; return 2; }; deploy_command ;;
    dashboard) [[ $# == 1 && $1 == deploy ]] || { error 'Usage: cw dashboard deploy'; return 2; }; dashboard_deploy ;;
    dd) (($# == 0)) || { error 'Usage: cw dd'; return 2; }; dashboard_deploy ;;
    restart|rs) (($# == 0)) || { error 'restart does not accept options'; help_hint; return 2; }; restart_command ;;
    log|l) log_command "$@" ;;
    projects|p) (($# == 0)) || { error 'projects does not accept options'; help_hint; return 2; }; projects_command ;;
    executions|e) executions_command "$@" ;;
    execution) execution_command "$@" ;;
    maintenance) maintenance_command "$@" ;;
    version|v) version_command "$@" ;;
    release|r) release_command "$@" ;;
    *) error "unknown command: $command"; help_hint; return 2 ;;
  esac
}

main "$@"
