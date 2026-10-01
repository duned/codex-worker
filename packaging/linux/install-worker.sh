#!/usr/bin/env bash
set -euo pipefail

readonly repository="duned/codex-worker"
readonly install_root=/opt/codex-worker
readonly config_root=/etc/codex-worker
readonly data_root=/var/lib/codex-worker
readonly archive_name_prefix=codex-worker

# When Bash reads this installer from stdin (for example through curl | sudo bash),
# BASH_SOURCE has no entry. In that case the systemd unit is fetched from GitHub.
script_dir=""
installer_source=${BASH_SOURCE[0]:-}
if [[ -n $installer_source ]]; then
  script_dir=$(cd -- "$(dirname -- "$installer_source")" && pwd) || {
    echo "Codex Worker installation failed: could not resolve the installer directory" >&2
    exit 1
  }
fi

usage() {
  echo "Usage: $0 [--version VERSION] [--server URL] [--capacity 1..8] [--register] [--start] [--token-file PATH]"
  echo "Install and optionally configure/register/start the Codex Worker on Ubuntu 24.04 x86_64."
  echo "For unattended registration, set CODEX_WORKER_BOOTSTRAP_TOKEN or use --token-file; tokens are never accepted as command-line values."
}

requested_version=latest
requested_server=""
requested_capacity=""
register_requested=false
start_requested=false
token_file=""
server_was_set=false
capacity_was_set=false
while (($#)); do
  case "$1" in
    --version)
      if (($# < 2)) || [[ -z $2 ]]; then
        echo "--version requires a version such as 0.13.0" >&2
        exit 2
      fi
      requested_version=$2
      shift 2
      ;;
    --server)
      (($# >= 2)) && [[ -n $2 ]] || { echo "--server requires an absolute HTTP or HTTPS URL" >&2; exit 2; }
      requested_server=$2; server_was_set=true; shift 2
      ;;
    --capacity)
      (($# >= 2)) && [[ $2 =~ ^[1-8]$ ]] || { echo "--capacity must be an integer from 1 to 8" >&2; exit 2; }
      requested_capacity=$2; capacity_was_set=true; shift 2
      ;;
    --register) register_requested=true; shift ;;
    --start) start_requested=true; shift ;;
    --token-file)
      (($# >= 2)) && [[ -n $2 ]] || { echo "--token-file requires a path" >&2; exit 2; }
      token_file=$2; shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

fail() {
  echo "Codex Worker installation failed: $*" >&2
  exit 1
}

if [[ ${EUID} -ne 0 ]]; then
  fail "run as root, for example: curl -fsSL https://raw.githubusercontent.com/$repository/main/packaging/linux/install-worker.sh | sudo bash"
fi

validate_server_url() {
  [[ $1 =~ ^https?://[^[:space:]]+$ && $1 != *'"'* && $1 != *$'\\'* && $1 != *'#'* && $1 != *'?'* && $1 != *'@'* ]] || return 1
}
if [[ $server_was_set == true ]] && ! validate_server_url "$requested_server"; then
  fail "--server must be an absolute HTTP or HTTPS URL without whitespace or YAML-special characters"
fi
if [[ -n $token_file && ! -r $token_file ]]; then
  fail "bootstrap token file is not readable"
fi
if [[ -n $token_file ]]; then
  token_file_mode=$(stat -c '%a' -- "$token_file") || fail "could not inspect bootstrap token file permissions"
  if (( (8#$token_file_mode & 077) != 0 )); then
    fail "bootstrap token file must not be accessible to group or other users (use mode 0600)"
  fi
fi
if [[ -n $token_file && -n ${CODEX_WORKER_BOOTSTRAP_TOKEN:-} ]]; then
  fail "use only one bootstrap token source: --token-file or CODEX_WORKER_BOOTSTRAP_TOKEN"
fi
bootstrap_token=${CODEX_WORKER_BOOTSTRAP_TOKEN:-}
unset CODEX_WORKER_BOOTSTRAP_TOKEN
if [[ $register_requested == true && -z $token_file && -z $bootstrap_token ]]; then
  if [[ ! -r /dev/tty || ! -w /dev/tty ]]; then
    fail "--register requires CODEX_WORKER_BOOTSTRAP_TOKEN or --token-file PATH when no interactive terminal is available"
  fi
fi

if [[ ! -r /etc/os-release ]]; then
  fail "cannot identify the operating system (missing /etc/os-release)"
fi
# shellcheck disable=SC1091
. /etc/os-release
if [[ ${ID:-} != ubuntu || ${VERSION_ID:-} != 24.04 ]]; then
  fail "supported operating system is Ubuntu 24.04; found ${PRETTY_NAME:-unknown}"
fi
machine=$(uname -m)
if [[ $machine != x86_64 ]]; then
  fail "supported architecture is x86_64; found $machine"
fi

for command_name in curl tar sha256sum install getent groupadd useradd id systemctl runuser awk stat; do
  command -v "$command_name" >/dev/null 2>&1 || fail "required system command is missing: $command_name"
done

if [[ $requested_version == latest ]]; then
  release_url=$(curl --fail --silent --show-error --location --output /dev/null --write-out '%{url_effective}' "https://github.com/$repository/releases/latest") || fail "could not resolve the latest GitHub Release"
  tag=${release_url##*/}
  version=${tag#v}
  if [[ ! $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
    fail "latest GitHub Release has an unsupported tag: $tag"
  fi
else
  version=${requested_version#v}
  if [[ ! $version =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
    fail "invalid version '$requested_version'; expected a version such as 0.13.0"
  fi
  tag="v$version"
fi

archive="$archive_name_prefix-$version-linux-x64.tar.gz"
release_base="https://github.com/$repository/releases/download/$tag"
temporary_dir=$(mktemp -d) || fail "could not create a temporary directory"
stage_dir="$install_root.next.$$"
backup_dir="$install_root.previous.$(date -u +%Y%m%dT%H%M%S.%N)"
service_was_active=false
swap_started=false
service_stopped=false
service_was_enabled=false
if systemctl is-enabled --quiet codex-worker; then service_was_enabled=true; fi
unit_changed=false
runtime_was_present=false
[[ ! -x $install_root/CodexWorker ]] || runtime_was_present=true
unit_path=/etc/systemd/system/codex-worker.service
unit_was_present=false
if [[ -f $unit_path ]]; then
  cp -a -- "$unit_path" "$temporary_dir/previous.service"
  unit_was_present=true
fi
service_was_present=false
if [[ $unit_was_present == true ]] || systemctl cat codex-worker >/dev/null 2>&1; then
  service_was_present=true
fi
config_changed=false
if [[ -f $config_root/worker.yml ]]; then
  cp -a -- "$config_root/worker.yml" "$temporary_dir/previous.yml"
fi

cleanup() {
  local status=$?
  if ((status != 0)) && [[ $service_stopped == true ]]; then
    # Stop any partially started service before changing its executable or unit.
    if [[ $service_was_present == false && $unit_changed == false ]] || systemctl stop codex-worker; then
      if [[ ( $runtime_was_present == false || $service_was_enabled == false ) &&
            ( $service_was_present == true || $unit_changed == true ) ]]; then
        systemctl disable codex-worker || echo "Could not disable the Worker service" >&2
      fi
      if [[ $swap_started == true ]]; then
        rm -rf -- "$install_root"
        if [[ -d $backup_dir ]]; then
          mv -- "$backup_dir" "$install_root" || echo "Could not restore previous Worker binaries from $backup_dir" >&2
        fi
      fi
      if [[ $unit_changed == true ]]; then
        if [[ $unit_was_present == true && $runtime_was_present == true ]]; then
          cp -a -- "$temporary_dir/previous.service" "$unit_path"
        else
          rm -f -- "$unit_path"
        fi
      fi
      if [[ $runtime_was_present == true ]]; then
        if [[ $config_changed == true && -f $temporary_dir/previous.yml ]]; then
          cp -a -- "$temporary_dir/previous.yml" "$config_root/worker.yml"
        fi
        if systemctl daemon-reload; then
          if [[ $service_was_active == true && -x $install_root/CodexWorker ]]; then
            if ! systemctl start codex-worker; then
              systemctl stop codex-worker || echo "Could not stop the previous Worker after restart failure" >&2
              echo "Previous Worker could not restart; inspect it before starting manually." >&2
            fi
          fi
        else
          echo "Could not reload the restored Worker unit; service left stopped." >&2
        fi
        echo "Previous Worker runtime retained/restored at $install_root." >&2
      else
        rm -rf -- "$install_root"
        rm -f -- "$unit_path"
        systemctl daemon-reload || echo "Could not reload systemd after cleanup" >&2
        systemctl reset-failed codex-worker >/dev/null 2>&1 || true
        echo "Worker service stopped and removed; no Worker runtime was installed." >&2
      fi
    else
      echo "Could not stop Worker during rollback; files retained. Run: sudo systemctl stop codex-worker before retrying." >&2
    fi
    echo "The codex-worker account and data/log directories remain. Configuration remains in $config_root; identity and recovery credentials remain in $data_root/.codex-worker." >&2
    echo "Retry: rerun install-worker.sh --version $version --register --start with the same Server URL and --token-file PATH (a protected valid bootstrap token). Keep the existing identity and credentials." >&2
  fi
  rm -rf -- "$temporary_dir" "$stage_dir"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

curl --fail --silent --show-error --location "$release_base/$archive" --output "$temporary_dir/$archive" || fail "could not download Worker $version release artifact"
curl --fail --silent --show-error --location "$release_base/checksums.txt" --output "$temporary_dir/checksums.txt" || fail "could not download release checksums"

checksum_line=$(awk -v name="$archive" '$2 == name || $2 == "*" name { print; found++ } END { if (found != 1) exit 1 }' "$temporary_dir/checksums.txt") || fail "checksums.txt must contain exactly one checksum for $archive"
expected_hash=${checksum_line:0:64}
if [[ ! $expected_hash =~ ^[[:xdigit:]]{64}$ ]]; then
  fail "release checksum for $archive is malformed"
fi
actual_hash=$(sha256sum "$temporary_dir/$archive")
actual_hash=${actual_hash%% *}
if [[ ${actual_hash,,} != ${expected_hash,,} ]]; then
  fail "checksum verification failed for $archive"
fi

mkdir "$temporary_dir/extracted" || fail "could not prepare the extraction directory"
tar -tzf "$temporary_dir/$archive" | while IFS= read -r entry; do
  case "$entry" in
    /*|../*|*/../*|*/..)
      echo "Unsafe path in release archive: $entry" >&2
      exit 1
      ;;
  esac
done || fail "release archive contains an unsafe path"
tar -xzf "$temporary_dir/$archive" -C "$temporary_dir/extracted" || fail "could not extract the verified release artifact"
if [[ ! -x $temporary_dir/extracted/CodexWorker || ! -f $temporary_dir/extracted/VERSION ]]; then
  fail "release artifact is missing the CodexWorker apphost or VERSION file"
fi
artifact_version=$(<"$temporary_dir/extracted/VERSION")
if [[ $artifact_version != "$version" ]]; then
  fail "release artifact version is $artifact_version, expected $version"
fi

getent group codex-worker >/dev/null || groupadd --system codex-worker || fail "could not create codex-worker group"
id codex-worker >/dev/null 2>&1 || useradd --system --gid codex-worker --home-dir "$data_root" --create-home --shell /usr/sbin/nologin codex-worker || fail "could not create codex-worker account"
install -d -o root -g codex-worker -m 0750 "$config_root" || fail "could not prepare $config_root"
install -d -o codex-worker -g codex-worker -m 0700 "$data_root" "$data_root/.codex-worker" || fail "could not prepare Worker state directories"
install -d -o codex-worker -g codex-worker -m 0750 "$data_root/projects" "$data_root/.codex-worker/worktrees" || fail "could not prepare Worker project directories"
install -d -o codex-worker -g codex-worker -m 0750 /var/log/codex-worker || fail "could not prepare Worker log directory"

# Even an inactive unit can have a pending auto-restart. Stop it before mutation.
if systemctl is-active --quiet codex-worker; then service_was_active=true; fi
if [[ $service_was_present == true || $service_was_active == true ]]; then
  systemctl stop codex-worker || fail "could not stop the Worker before installation"
fi
service_stopped=true
config_changed=true

config_was_present=false
if [[ -e $config_root/worker.yml ]]; then config_was_present=true; fi
if [[ $config_was_present == false ]]; then
  curl --fail --silent --show-error --location "https://raw.githubusercontent.com/$repository/main/packaging/linux/worker.managed.example.yml" --output "$temporary_dir/worker.yml" || fail "could not download the starter Worker configuration"
  install -o root -g codex-worker -m 0640 "$temporary_dir/worker.yml" "$config_root/worker.yml" || fail "could not install starter Worker configuration"
fi

# Guide only a clean first install. Existing operator YAML remains untouched unless
# an option explicitly supplies a value.
interactive=false
if [[ -r /dev/tty && -w /dev/tty && $config_was_present == false ]]; then interactive=true; fi
if [[ $interactive == true ]]; then
  if [[ $server_was_set == false ]]; then
    read -r -p "Codex Server URL: " requested_server </dev/tty
    if [[ -n $requested_server ]]; then
      validate_server_url "$requested_server" || fail "Server URL must be an absolute HTTP or HTTPS URL"
      server_was_set=true
    fi
  fi
  if [[ $capacity_was_set == false ]]; then
    read -r -p "Worker capacity [1]: " requested_capacity </dev/tty
    requested_capacity=${requested_capacity:-1}
    [[ $requested_capacity =~ ^[1-8]$ ]] || fail "Worker capacity must be an integer from 1 to 8"
    capacity_was_set=true
  fi
  if [[ $register_requested == false ]]; then
    read -r -p "Register Worker now? [Y/n]: " answer </dev/tty
    [[ ${answer,,} != n && ${answer,,} != no ]] && register_requested=true
  fi
  if [[ $start_requested == false ]]; then
    read -r -p "Start Worker after installation? [Y/n]: " answer </dev/tty
    [[ ${answer,,} != n && ${answer,,} != no ]] && start_requested=true
  fi
fi

# A clean interactive install can ask whether to register, while an explicit
# --register can be used with either a new or existing configuration. Resolve
# the terminal source after that choice is known, always through the controlling
# terminal so curl | sudo bash never consumes the script's piped stdin.
if [[ $register_requested == true && -z $token_file && -z $bootstrap_token ]]; then
  IFS= read -r -s -p "Bootstrap token: " bootstrap_token </dev/tty || fail "could not read the bootstrap token from the interactive terminal"
  printf '\n' >/dev/tty
  [[ -n $bootstrap_token ]] || fail "bootstrap token cannot be empty"
fi

if [[ $server_was_set == true || $capacity_was_set == true ]]; then
  config_update="$temporary_dir/worker.yml"
  WORKER_SERVER_URL="$requested_server" WORKER_CAPACITY="$requested_capacity" \
    WORKER_SET_SERVER="$server_was_set" WORKER_SET_CAPACITY="$capacity_was_set" \
    awk '
      /^worker:$/ { section = "worker"; print; next }
      /^server:$/ { section = "server"; print; next }
      /^[^[:space:]]/ { section = "" }
      section == "worker" && WORKER_SET_CAPACITY == "true" && /^  maxParallelTasks:/ {
        print "  maxParallelTasks: " WORKER_CAPACITY; next
      }
      section == "server" && WORKER_SET_SERVER == "true" && /^  url:/ {
        print "  url: \"" WORKER_SERVER_URL "\""; next
      }
      { print }
    ' "$config_root/worker.yml" > "$config_update" || fail "could not update Worker configuration"
  if [[ $capacity_was_set == true ]] && ! grep -q '^  maxParallelTasks:' "$config_update"; then
    fail "Worker configuration has no worker.maxParallelTasks setting"
  fi
  if [[ $server_was_set == true ]] && ! grep -q '^  url:' "$config_update"; then
    fail "Worker configuration has no server.url setting"
  fi
  install -o root -g codex-worker -m 0640 "$config_update" "$config_root/worker.yml" || fail "could not save Worker configuration"
fi
if [[ $server_was_set == false ]]; then
  requested_server=$(awk '/^server:$/ { in_server = 1; next } /^[^[:space:]]/ { in_server = 0 } in_server && /^  url:/ { sub(/^  url:[[:space:]]*/, ""); if (substr($0, 1, 1) == "\"" || substr($0, 1, 1) == sprintf("%c", 39)) $0 = substr($0, 2, length($0) - 2); print; exit }' "$config_root/worker.yml")
fi
if [[ ! -e $config_root/worker.env ]]; then
  install -o root -g codex-worker -m 0640 /dev/null "$config_root/worker.env" || fail "could not create Worker environment file"
fi
chown root:codex-worker "$config_root/worker.yml" "$config_root/worker.env" || fail "could not set Worker configuration ownership"
chmod 0640 "$config_root/worker.yml" "$config_root/worker.env" || fail "could not set Worker configuration permissions"

rm -rf -- "$stage_dir" || fail "could not clear the temporary staging directory"
install -d -o root -g root -m 0755 "$stage_dir" || fail "could not create the temporary staging directory"
cp -a "$temporary_dir/extracted"/. "$stage_dir"/ || fail "could not stage the verified Worker release"
chown -R root:root "$stage_dir" || fail "could not set Worker binary ownership"
chmod 0755 "$stage_dir/CodexWorker" || fail "could not set Worker executable permissions"
runuser -u codex-worker -- "$stage_dir/CodexWorker" --help >/dev/null || fail "staged Worker executable cannot run as the service account"
if [[ -f $script_dir/codex-worker.service ]]; then
  cp -- "$script_dir/codex-worker.service" "$temporary_dir/codex-worker.service"
else
  curl --fail --silent --show-error --location "https://raw.githubusercontent.com/$repository/main/packaging/linux/codex-worker.service" --output "$temporary_dir/codex-worker.service" || fail "could not download the systemd unit"
fi
grep -Fxq "ExecStart=$install_root/CodexWorker run --config $config_root/worker.yml" "$temporary_dir/codex-worker.service" || fail "systemd ExecStart does not match the packaged Worker executable and configuration"

if [[ $register_requested == true ]]; then
  [[ -n $requested_server ]] && validate_server_url "$requested_server" || fail "registration requires a valid server.url in Worker configuration or --server URL"
  [[ $requested_server != *codex-server.example* ]] || fail "replace the starter server.url or pass --server URL before registering"
  identity_file="$data_root/.codex-worker/worker-id"
  capacity_for_registration=${requested_capacity:-$(awk '/^  maxParallelTasks:/ { print $2; exit }' "$config_root/worker.yml")}
  capacity_for_registration=${capacity_for_registration:-1}
  token_value=$bootstrap_token
  if [[ -n $token_file ]]; then
    IFS= read -r token_value < "$token_file" || [[ -n $token_value ]] || fail "bootstrap token file is empty"
  fi
  [[ -n $token_value ]] || fail "bootstrap token cannot be empty"
  if ! printf '%s\n' "$token_value" | runuser -u codex-worker -- "$stage_dir/CodexWorker" register \
      --server "$requested_server" --token-stdin --capacity "$capacity_for_registration" --identity-file "$identity_file"; then
    fail "Worker registration failed; the bootstrap token was not written to installer output"
  fi
  unset token_value bootstrap_token
fi
if [[ -d $install_root ]]; then
  mv -- "$install_root" "$backup_dir" || fail "could not preserve the existing Worker installation"
fi
swap_started=true
if ! mv -- "$stage_dir" "$install_root"; then
  fail "could not activate Worker $version"
fi

unit_changed=true
install -o root -g root -m 0644 "$temporary_dir/codex-worker.service" "$unit_path" || fail "could not install the systemd unit"
systemctl daemon-reload || fail "systemd could not reload unit files"
if [[ $service_was_active == true && $start_requested == false ]]; then
  systemctl start codex-worker || fail "Worker $version was installed but the service could not be restarted"
fi
if [[ $start_requested == true ]]; then
  systemctl enable --now codex-worker || fail "Worker was installed but could not be enabled and started"
fi

echo "Codex Worker $version installed in $install_root. Existing binaries are preserved at $backup_dir when present."
if [[ $server_was_set == true ]]; then echo "Configured Codex Server URL in $config_root/worker.yml."; fi
if [[ $capacity_was_set == true ]]; then echo "Configured Worker capacity to $requested_capacity."; fi
if [[ $register_requested == true ]]; then echo "Worker registration completed."; else echo "Worker registration was not requested."; fi
if [[ $start_requested == true ]]; then echo "Worker service enable/start request completed."; elif [[ $service_was_active == true ]]; then echo "Worker service was active before installation; its restart request completed."; else echo "Worker service was not started by this installation."; fi
echo "Add project configuration and required environment values to $config_root/worker.yml and $config_root/worker.env."

echo "Installation/registration do not imply execution readiness. External Git, gh, Codex CLI and service-account authentication are not installed or configured automatically."
echo "Service start and capability readiness are separate. Check runtime status and diagnostics: systemctl status codex-worker; journalctl -u codex-worker."
