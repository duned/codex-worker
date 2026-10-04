#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
version=""
output_dir=""

while (($#)); do
  case "$1" in
    --set-version)
      if (($# < 2)) || [[ -n "$version" ]]; then
        echo "Usage: $0 [--set-version VERSION] [output-directory]" >&2
        exit 2
      fi
      version="$2"
      shift 2
      ;;
    --version)
      if (($# != 1)); then
        echo "Usage: $0 [--set-version VERSION] [output-directory]" >&2
        exit 2
      fi
      version="$(dotnet msbuild "$repo_root/src/CodexWorker/CodexWorker.csproj" -nologo -m:1 -getProperty:Version)"
      printf '%s\n' "$version"
      exit 0
      ;;
    --help)
      echo "Usage: $0 [--set-version VERSION] [output-directory]"
      exit 0
      ;;
    -*)
      echo "Unknown option '$1'. Use --help for usage." >&2
      exit 2
      ;;
    *)
      if [[ -n "$output_dir" ]]; then
        echo "Usage: $0 [--set-version VERSION] [output-directory]" >&2
        exit 2
      fi
      output_dir="$1"
      shift
      ;;
  esac
done

if [[ -z "$version" ]]; then
  version="$(dotnet msbuild "$repo_root/src/CodexWorker/CodexWorker.csproj" -nologo -m:1 -getProperty:Version)"
fi

if [[ -z "$version" ]]; then
  echo "Could not read the application version from MSBuild." >&2
  exit 1
fi
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Version must be a stable semantic version in MAJOR.MINOR.PATCH form (for example 1.2.3)." >&2
  exit 2
fi

temporary_output=false
if [[ -z "$output_dir" ]]; then
  output_dir="$(mktemp -d "${TMPDIR:-/tmp}/codex-worker-artifacts.XXXXXX")"
  temporary_output=true
fi

case "$output_dir" in
  /*) ;;
  *) output_dir="$repo_root/$output_dir" ;;
esac

mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd -P)"
if $temporary_output; then
  case "$output_dir/" in
    "$repo_root/"*)
      rmdir -- "$output_dir"
      echo "Temporary output must be outside the checkout. Set TMPDIR to an external directory." >&2
      exit 1
      ;;
  esac
fi
work_dir=""
cleanup() {
  local status=$?
  if [[ -n "$work_dir" ]]; then rm -rf -- "$work_dir"; fi
  if ((status != 0)); then
    if $temporary_output; then
      rm -rf -- "$output_dir"
      echo "Packaging failed; temporary artifacts were removed." >&2
    else
      echo "Packaging failed; inspect partial output in $output_dir before retrying." >&2
    fi
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
work_dir="$(mktemp -d)"

package_app() {
  local name="$1"
  local project="$2"
  local package_dir="$work_dir/$name"
  local archive="$output_dir/$name-$version-linux-x64.tar.gz"

  mkdir -p "$package_dir"
  dotnet publish "$repo_root/$project" \
    --configuration Release \
    --maxcpucount:1 \
    --runtime linux-x64 \
    --self-contained true \
    "-p:Version=$version" \
    --output "$package_dir"
  local apphost
  case "$name" in
    codex-worker) apphost=CodexWorker ;;
    codex-server) apphost=CodexServer ;;
    wet) apphost=wet ;;
    *) echo "Unknown packaged application: $name" >&2; return 1 ;;
  esac
  if [[ ! -f $package_dir/$apphost || ! -x $package_dir/$apphost ]]; then
    echo "Published $name is missing the executable $apphost apphost at the archive root." >&2
    return 1
  fi
  printf '%s\n' "$version" > "$package_dir/VERSION"
  tar -czf "$archive" -C "$package_dir" .
}

package_app codex-server src/CodexServer/CodexServer.csproj
package_app codex-worker src/CodexWorker/CodexWorker.csproj
package_app wet src/WorkExecutionToolbox.Cli/WorkExecutionToolbox.Cli.csproj

(
  cd "$output_dir"
  sha256sum "codex-server-$version-linux-x64.tar.gz" "codex-worker-$version-linux-x64.tar.gz" "wet-$version-linux-x64.tar.gz" > checksums.txt
)

printf 'Created release artifacts in %s\n' "$output_dir"
