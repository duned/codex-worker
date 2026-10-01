#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_PATH="$(readlink -f -- "${BASH_SOURCE[0]}")"
readonly SCRIPT_DIR="$(dirname -- "$SCRIPT_PATH")"
readonly REPO_ROOT="$SCRIPT_DIR"
readonly CALLER_DIR="$PWD"
readonly service_name=${CW_SERVICE_NAME:-codex-worker}
readonly worker_config=${CW_WORKER_CONFIG:-/etc/codex-worker/worker.yml}
readonly deploy_dir=${CW_DEPLOY_DIR:-$HOME/apps/codex-worker}

usage() {
  cat <<'HELP'
Usage: ./cw <command> [options]

Developer and local Worker operations:
  status, s       Show repository and local Worker service status
  deploy, d       Publish this checkout to ~/apps/codex-worker and restart the service
  log, l          Show Worker journal (default: last 100 lines)
    -f            Follow the journal
    -n COUNT      Show COUNT lines
  projects, p     List configured projects and their YAML paths
  help, -h, --help

Environment overrides:
  CW_WORKER_CONFIG  Global Worker YAML (default: /etc/codex-worker/worker.yml)
  CW_DEPLOY_DIR     Local deployment directory (default: ~/apps/codex-worker)
  CW_SERVICE_NAME   systemd service name (default: codex-worker)

cw deploy is a local development deployment. It does not create a product
release or change the repository product version.
HELP
}

error() { printf 'cw: %s\n' "$*" >&2; }
help_hint() { error 'Run ./cw --help for usage.'; }

need_command() {
  command -v "$1" >/dev/null 2>&1 || { error "required command is unavailable: $1"; return 1; }
}

git_value() {
  git -C "$REPO_ROOT" "$@"
}

validate_repository() {
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
  git_value worktree list --porcelain 2>/dev/null | awk '/^worktree / { sub(/^worktree /, "  - "); print }'

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
  awk -F'[<>]' '/<Version>[[:space:]]*[^<]+[[:space:]]*<\/Version>/ { gsub(/[[:space:]]/, "", $3); print $3; exit }' "$REPO_ROOT/Directory.Build.props"
}

deploy_command() {
  need_command dotnet || return 1
  need_command git || return 1
  need_command systemctl || return 1
  local version commit parent
  stage=''
  backup=''
  failed=''
  was_active=false
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
        systemctl start "$service_name" || error "could not restart previous Worker service"
      fi
      error "deployment failed; previous deployment restored when possible${failed:+; failed files preserved at $failed}"
    fi
    if ((result != 0)) && [[ $swapped != true && $was_active == true ]]; then
      systemctl start "$service_name" || error 'could not restart Worker after the failed replacement attempt'
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
  if [[ $was_active == true ]]; then systemctl stop "$service_name" || { error 'could not stop active Worker service'; return 1; }; fi
  if [[ -e $deploy_dir ]]; then mv -- "$deploy_dir" "$backup" || { error "could not preserve existing deployment at $deploy_dir"; return 1; }; fi
  if ! mv -- "$stage/publish" "$deploy_dir"; then
    [[ ! -d $backup ]] || mv -- "$backup" "$deploy_dir"
    if [[ $was_active == true ]]; then systemctl start "$service_name" || true; fi
    error 'could not install staged publish'; return 1
  fi
  swapped=true
  systemctl start "$service_name" || { error 'could not start Worker service'; return 1; }
  local healthy=false
  for _ in {1..30}; do
    if systemctl is-active --quiet "$service_name"; then healthy=true; break; fi
    sleep 1
  done
  [[ $healthy == true ]] || { error 'Worker service did not become active'; return 1; }
  swapped=false
  trap - EXIT
  rm -rf -- "$stage"
  printf 'Deployed Worker %s from %s; service is active.\n' "$version" "$commit"
  if [[ -d $backup ]]; then printf 'Previous deployment preserved at %s\n' "$backup"; fi
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

yaml_scalar() {
  local file=$1 key=$2
  awk -v key="$key" '
    /^[[:space:]]*#/ { next }
    {
      line=$0
      if (match(line, "^[[:space:]]*" key "[[:space:]]*:")) {
        sub("^[[:space:]]*" key "[[:space:]]*:[[:space:]]*", "", line)
        sub(/[[:space:]]+#.*$/, "", line)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", line)
        if (line ~ /^".*"$/ || line ~ /^\047.*\047$/) line=substr(line, 2, length(line)-2)
        print line
        exit
      }
    }
  ' "$file"
}

projects_command() {
  [[ -r $worker_config ]] || { error "Worker configuration is not readable: $worker_config"; return 1; }
  local config_dir
  config_dir=$(awk '
    /^[[:space:]]*#/ { next }
    /^[[:space:]]*projects:[[:space:]]*(#.*)?$/ { in_projects=1; next }
    in_projects && /^[^[:space:]#]/ { in_projects=0 }
    in_projects && /^[[:space:]]+directory[[:space:]]*:/ {
      sub(/^[[:space:]]+directory[[:space:]]*:[[:space:]]*/, "")
      sub(/[[:space:]]+#.*$/, "")
      gsub(/^[[:space:]]+|[[:space:]]+$/, "")
      if ($0 ~ /^".*"$/ || $0 ~ /^\047.*\047$/) $0=substr($0, 2, length($0)-2)
      print; exit
    }
  ' "$worker_config")
  [[ -n $config_dir ]] || config_dir=./projects
  if [[ $config_dir != /* ]]; then config_dir="$(dirname -- "$(realpath -m -- "$worker_config")")/$config_dir"; fi
  config_dir=$(realpath -m -- "$config_dir")
  [[ -d $config_dir ]] || { error "project configuration directory does not exist: $config_dir"; return 1; }
  printf 'Projects\n'
  local found=false file name
  while IFS= read -r -d '' file; do
    found=true
    name=$(yaml_scalar "$file" name)
    [[ -n $name ]] || name=$(basename -- "$file")
    printf '\n%s\n  config  %s\n' "$name" "$file"
  done < <(find "$config_dir" -maxdepth 1 -type f \( -iname '*.yml' -o -iname '*.yaml' \) -print0 | sort -z -f)
  [[ $found == true ]] || printf '\n(no project YAML files found)\n'
}

main() {
  (($#)) || { usage; return 0; }
  local command=$1; shift
  case $command in
    --help|-h|help) ;;
    *) validate_repository || return 1 ;;
  esac
  case $command in
    --help|-h|help) (($# == 0)) || { error 'help does not accept options'; help_hint; return 2; }; usage ;;
    status|s) (($# == 0)) || { error 'status does not accept options'; help_hint; return 2; }; status_command ;;
    deploy|d) (($# == 0)) || { error 'deploy does not accept options'; help_hint; return 2; }; deploy_command ;;
    log|l) log_command "$@" ;;
    projects|p) (($# == 0)) || { error 'projects does not accept options'; help_hint; return 2; }; projects_command ;;
    *) error "unknown command: $command"; help_hint; return 2 ;;
  esac
}

main "$@"
