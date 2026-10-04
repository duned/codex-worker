#!/usr/bin/env bash
set -euo pipefail

# Real SDK smoke coverage; all home/tool/package state is confined to a temporary directory.
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
export DOTNET_CLI_HOME="$test_root/dotnet-home"
export XDG_DATA_HOME="$test_root/data"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export NUGET_PACKAGES="$test_root/nuget"
export CW_WET_TOOL_DIR="$test_root/development-wet"
project="$repo_root/src/WorkExecutionToolbox.Cli/WorkExecutionToolbox.Cli.csproj"
mkdir -p "$DOTNET_CLI_HOME" "$test_root/bin"
for command in git gh; do
  cat > "$test_root/bin/$command" <<'STUB'
#!/usr/bin/env bash
echo 'Version/help unexpectedly accessed Git or GitHub' >&2
exit 99
STUB
  chmod +x "$test_root/bin/$command"
done
export PATH="$test_root/bin:$PATH"

# Ordinary build/test must not install a tool, including when the tool is absent.
dotnet build "$project" -c Release --nologo -m:1 -p:BuildInParallel=false
dotnet test "$project" -c Release --no-restore --nologo -m:1 -p:BuildInParallel=false
[[ ! -e $CW_WET_TOOL_DIR ]]
version=$(dotnet msbuild "$project" -getProperty:Version)
dotnet pack "$project" -c Release --no-build -o "$test_root/packages" --nologo
dotnet tool install WorkExecutionToolbox.Cli --source "$test_root/packages" \
  --version "$version" --tool-path "$CW_WET_TOOL_DIR"

verify_version() {
  for argument in --version -v v; do
    [[ $("$1" "$argument") == "wet $version" ]]
  done
  "$1" --help > "$test_root/help"
  grep -Fq 'dependency add ISSUE BLOCKER' "$test_root/help"
}

cd "$test_root" # No repository, authentication or cache is available here.
verify_version "$CW_WET_TOOL_DIR/wet"
# Same-version replacement remains executable after removing the transient package cache.
dotnet tool uninstall WorkExecutionToolbox.Cli --tool-path "$CW_WET_TOOL_DIR"
NUGET_PACKAGES="$test_root/install-cache" dotnet tool install WorkExecutionToolbox.Cli \
  --source "$test_root/packages" --version "$version" --tool-path "$CW_WET_TOOL_DIR"
rm -rf -- "$test_root/install-cache" "$test_root/packages"
verify_version "$CW_WET_TOOL_DIR/wet"
[[ ! -d $XDG_DATA_HOME/wet/github-issue-cache ]]

# Ordinary build/test also preserves an existing tool installation.
cp "$CW_WET_TOOL_DIR/wet" "$test_root/shim-before"
dotnet build "$project" -c Release --no-restore --nologo -m:1 -p:BuildInParallel=false
dotnet test "$project" -c Release --no-restore --nologo -m:1 -p:BuildInParallel=false
cmp "$test_root/shim-before" "$CW_WET_TOOL_DIR/wet"
verify_version "$CW_WET_TOOL_DIR/wet"

dotnet publish "$project" -c Release --no-build --no-restore -o "$test_root/publish" --nologo
verify_version "$test_root/publish/wet"
printf 'WET tool tests passed\n'
