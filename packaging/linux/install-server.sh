#!/usr/bin/env bash
set -euo pipefail

readonly repository="duned/codex-worker"
readonly release_root="https://github.com/${repository}/releases"
version=""
publish_dir=""
staging_dir=""
bootstrap_github_cli=false
non_interactive=false
keep_bootstrap_provisioning_policy=false
bootstrap_restore_needed=false
bootstrap_previous_enabled=false
bootstrap_previous_elevation=false

server_helper=/usr/local/bin/codex-server
readonly provisioning_sudoers=/etc/sudoers.d/codex-server-provisioning

cleanup() {
  if [[ $bootstrap_restore_needed == true ]]; then
    set +e
    restore_bootstrap_policy
    local restore_status=$?
    set -e
    if [[ $restore_status != 0 ]]; then
      printf 'Codex Server installer: failed to restore the previous local provisioning policy; inspect /etc/codex-server/server.env and restart codex-server.service.\n' >&2
    fi
  fi
  [[ -z "$staging_dir" ]] || rm -rf -- "$staging_dir"
  [[ -z "${work_dir:-}" ]] || rm -rf -- "$work_dir"
}
trap cleanup EXIT

usage() {
  cat <<'EOF'
Usage: install-server.sh [--version VERSION] [PUBLISHED_DIRECTORY]

Install Codex Server from the official GitHub release (latest by default), or
from a local self-contained publish directory when PUBLISHED_DIRECTORY is set.

Options:
  --version VERSION  Install a specific release (optional leading v is allowed)
  --bootstrap-github-cli  Offer or perform a typed local GitHub CLI installation
  --non-interactive       Do not prompt; only perform bootstrap when explicitly requested
  --keep-bootstrap-provisioning-policy  Leave both local provisioning settings enabled
  -h, --help         Show this help

Examples:
  curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/main/packaging/linux/install-server.sh | sudo bash
  curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/v1.2.3/packaging/linux/install-server.sh | sudo bash -s -- --version 1.2.3
EOF
}

fail() { printf 'Codex Server installer: %s\n' "$*" >&2; exit 1; }

ensure_management_token() {
  local environment_file="$1"

  if grep -Eq '^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=' "$environment_file"; then
    return 0
  fi

  command -v openssl >/dev/null 2>&1 || fail 'OpenSSL is required to generate the initial management token.'
  local management_token
  management_token="$(openssl rand -hex 32)" || fail 'Could not generate the initial management token.'
  [[ "$management_token" =~ ^[[:xdigit:]]{64}$ ]] || fail 'OpenSSL returned an invalid management token.'
  printf '\nCODEX_SERVER_MANAGEMENT_TOKEN=%s\n' "$management_token" >> "$environment_file" || fail 'Could not save the initial management token.'
  unset management_token
}

ensure_credential_encryption_key() (
  set +x
  local environment_file="$1"

  if grep -Eq '^[[:space:]]*CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY[[:space:]]*=' "$environment_file"; then
    return 0
  fi

  command -v openssl >/dev/null 2>&1 || fail 'OpenSSL is required to generate the Server credential encryption key.'
  local encryption_key
  encryption_key="$(openssl rand -base64 32)" || fail 'Could not generate the Server credential encryption key.'
  [[ "$encryption_key" =~ ^[A-Za-z0-9+/]{43}=$ ]] || fail 'OpenSSL returned an invalid Server credential encryption key.'
  printf '\nCODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY=%s\n' "$encryption_key" >> "$environment_file" || fail 'Could not save the Server credential encryption key.'
  unset encryption_key
)

write_operator_helper() {
  cat <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ $# == 1 && ( $1 == --help || $1 == -h || $1 == --version ) ]]; then
  exec /opt/codex-server/current/CodexServer "$@"
fi
if [[ $EUID -ne 0 ]]; then
  echo 'Run Server local administration and token commands with sudo.' >&2
  exit 1
fi
if [[ ${1:-} != worker-token && ${1:-} != worker && ${1:-} != status && ${1:-} != diagnostics && ${1:-} != config && ${1:-} != projects && ${1:-} != executions && ${1:-} != github && ${1:-} != credential && ${1:-} != provision && ${1:-} != backup ]]; then
  echo 'Usage: sudo codex-server <status|diagnostics|config|projects|executions|github|credential|provision|backup|worker-token|worker> [arguments]' >&2
  exit 2
fi
# Only this allowlisted mutation runs as root so it can preserve the installed
# EnvironmentFile ownership. The executable accepts a fixed settings allowlist.
if [[ ${1:-} == config && ${2:-} == set ]]; then
  exec systemd-run --quiet --wait --pipe --collect \
    --property=User=root --property=Group=root \
    --property=Environment=CODEX_SERVER_OPERATOR_SERVICE_CONTEXT=1 \
    --property=Environment=CODEX_SERVER_CONFIGURATION_FILE=/etc/codex-server/server.env \
    --property=Environment=HOME=/var/lib/codex-server \
    --property=EnvironmentFile=/etc/codex-server/server.env \
    --property=WorkingDirectory=/opt/codex-server/current \
    --property=UMask=0077 \
    /opt/codex-server/current/CodexServer "$@"
fi
# Let systemd parse its EnvironmentFile syntax; never source secrets as shell code.
exec systemd-run --quiet --wait --pipe --collect \
  --property=User=codex-server --property=Group=codex-server \
  --property=Environment=CODEX_SERVER_OPERATOR_SERVICE_CONTEXT=1 \
  --property=EnvironmentFile=/etc/codex-server/server.env \
  --property=WorkingDirectory=/opt/codex-server/current \
  --property=UMask=0077 \
  /opt/codex-server/current/CodexServer "$@"
EOF
}

ensure_server_environment() (
  umask 0077
  local environment_file=$1
  if [[ ! -e $environment_file ]]; then
    # Retained installs keep their original database selection, including the
    # legacy home default. Only a fresh install gets the documented directory.
    printf 'Server__DataDirectory=/var/lib/codex-server\n' > "$environment_file"
  fi
  ensure_management_token "$environment_file"
  ensure_credential_encryption_key "$environment_file"
)

write_provisioning_sudoers() {
  cat <<'EOF'
# Managed by the Codex Server installer. Do not add commands here.
Cmnd_Alias CODEX_SERVER_PROVISIONING = \
    /usr/bin/apt-get update, \
    /usr/bin/apt-get install -y --no-install-recommends git openssh-client, \
    /usr/bin/apt-get install -y --no-install-recommends gh, \
    /usr/bin/apt-get install -y --no-install-recommends nodejs npm, \
    /usr/bin/apt-get remove -y git, \
    /usr/bin/apt-get remove -y gh, \
    /usr/bin/npm install --global --prefix /usr/local --registry https\://registry.npmjs.org --userconfig /dev/null --globalconfig /dev/null --cache /var/cache/codex-provisioning/npm --no-audit --no-fund "@openai/codex@latest", \
    /usr/bin/npm uninstall --global --prefix /usr/local --registry https\://registry.npmjs.org --userconfig /dev/null --globalconfig /dev/null --cache /var/cache/codex-provisioning/npm --no-audit --no-fund "@openai/codex", \
    /usr/bin/chmod a+rx /usr/local/bin /usr/local/lib /usr/local/lib/node_modules /usr/local/lib/node_modules/@openai, \
    /usr/bin/chmod -R a+rX /usr/local/lib/node_modules/@openai/codex
codex-server ALL=(root) NOPASSWD: CODEX_SERVER_PROVISIONING
EOF
}

install_provisioning_sudoers() {
  command -v visudo >/dev/null 2>&1 || fail 'visudo is required to validate the local provisioning privilege policy.'
  local temporary_policy
  temporary_policy="$(mktemp /etc/sudoers.d/.codex-server-provisioning.XXXXXX)" || fail 'Could not create the temporary provisioning privilege policy.'
  if ! write_provisioning_sudoers > "$temporary_policy"; then
    rm -f -- "$temporary_policy"
    fail 'Could not generate the local provisioning privilege policy.'
  fi
  chown root:root "$temporary_policy"
  chmod 0440 "$temporary_policy"
  if ! visudo -cf "$temporary_policy" >/dev/null; then
    rm -f -- "$temporary_policy"
    fail 'The generated local provisioning privilege policy did not pass visudo validation.'
  fi
  mv -f -- "$temporary_policy" "$provisioning_sudoers"
  visudo -c >/dev/null || fail 'The installed sudoers configuration did not pass visudo validation.'
}

read_server_boolean() {
  local property=$1 document value
  document="$("$server_helper" config show --json)" || fail 'Could not inspect current Server provisioning policy.'
  value="$(sed -n "s/^[[:space:]]*\"${property}\":[[:space:]]*\\(true\\|false\\),\\{0,1\\}[[:space:]]*$/\\1/p" <<< "$document")"
  [[ $value == true || $value == false ]] || fail "Could not read the current Server setting ${property}."
  printf '%s' "$value"
}

set_server_boolean() {
  "$server_helper" config set "$1" "$2" --json >/dev/null || return 1
}

restore_bootstrap_policy() {
  [[ $bootstrap_restore_needed == true ]] || return 0
  set_server_boolean EnableLocalProvisioning "$bootstrap_previous_enabled" || return 1
  set_server_boolean AllowLocalProvisioningElevation "$bootstrap_previous_elevation" || return 1
  systemctl restart codex-server || return 1
  bootstrap_restore_needed=false
}

bootstrap_github_cli_installation() {
  local command_document command_id status iteration
  bootstrap_previous_enabled="$(read_server_boolean enableLocalProvisioning)"
  bootstrap_previous_elevation="$(read_server_boolean allowLocalProvisioningElevation)"
  bootstrap_restore_needed=true
  set_server_boolean EnableLocalProvisioning true || fail 'Could not temporarily enable local provisioning for bootstrap.'
  set_server_boolean AllowLocalProvisioningElevation true || fail 'Could not temporarily enable local elevation for bootstrap.'
  systemctl restart codex-server || fail 'Could not restart Server with the temporary bootstrap policy.'

  command_document="$("$server_helper" provision create server github-cli install --allow-elevation --timeout-seconds 600 --json)" ||
    fail 'Could not queue the typed GitHub CLI bootstrap command.'
  command_id="$(sed -n 's/^[[:space:]]*"id":[[:space:]]*"\([[:xdigit:]]\{32\}\)"[,]*[[:space:]]*$/\1/p' <<< "$command_document")"
  [[ $command_id =~ ^[[:xdigit:]]{32}$ ]] || fail 'The Server did not return a valid typed provisioning command ID.'

  for ((iteration = 0; iteration < 610; iteration++)); do
    command_document="$("$server_helper" provision show "$command_id" --json)" || fail 'Could not inspect GitHub CLI bootstrap progress.'
    status="$(sed -n 's/^[[:space:]]*"status":[[:space:]]*"\([A-Za-z]*\)"[,]*[[:space:]]*$/\1/p' <<< "$command_document")"
    case "$status" in
      Succeeded)
        printf 'GitHub CLI installed and verified through typed local provisioning.\n'
        if [[ $keep_bootstrap_provisioning_policy == true ]]; then
          bootstrap_restore_needed=false
          printf 'Local provisioning and elevation remain enabled as requested.\n'
        else
          restore_bootstrap_policy || fail 'GitHub CLI bootstrap succeeded, but the previous provisioning policy could not be restored.'
          printf 'The previous restrictive local provisioning policy has been restored.\n'
        fi
        return 0
        ;;
      Failed|TimedOut|Cancelled)
        printf 'GitHub CLI bootstrap ended as %s. Inspect with: sudo codex-server provision show %s --json\n' "$status" "$command_id" >&2
        fail 'The typed GitHub CLI bootstrap command did not complete successfully.'
        ;;
      Pending|Running) sleep 1 ;;
      *) fail 'The Server returned an invalid GitHub CLI bootstrap status.' ;;
    esac
  done
  fail "GitHub CLI bootstrap did not finish before the installer wait expired. Inspect with: sudo codex-server provision show $command_id --json"
}

# Keep the credential logic available to the local installer test without running installation.
# As in install-worker.sh, BASH_SOURCE may be empty when executing from stdin.
if [[ "${BASH_SOURCE[0]:-$0}" != "$0" ]]; then
  return 0
fi

while (($#)); do
  case "$1" in
    --version)
      (($# >= 2)) || { usage >&2; exit 2; }
      [[ -z "$version" ]] || fail '--version may only be specified once.'
      version="${2#v}"
      version="${version#V}"
      [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || fail 'Version must be a release version such as 1.2.3.'
      shift 2
      ;;
    --bootstrap-github-cli)
      [[ $bootstrap_github_cli == false ]] || fail '--bootstrap-github-cli may only be specified once.'
      bootstrap_github_cli=true
      shift
      ;;
    --non-interactive)
      [[ $non_interactive == false ]] || fail '--non-interactive may only be specified once.'
      non_interactive=true
      shift
      ;;
    --keep-bootstrap-provisioning-policy)
      [[ $keep_bootstrap_provisioning_policy == false ]] || fail '--keep-bootstrap-provisioning-policy may only be specified once.'
      keep_bootstrap_provisioning_policy=true
      shift
      ;;
    -h|--help) usage; exit 0 ;;
    --*) fail "Unknown option: $1" ;;
    *)
      [[ -z "$publish_dir" ]] || { usage >&2; exit 2; }
      publish_dir="$1"
      shift
      ;;
  esac
done

if [[ $keep_bootstrap_provisioning_policy == true && $bootstrap_github_cli != true ]]; then
  fail '--keep-bootstrap-provisioning-policy requires --bootstrap-github-cli.'
fi

if [[ ${EUID} -ne 0 ]]; then
  fail 'Run as root (for example, curl -fsSL <installer-url> | sudo bash).'
fi

if [[ $bootstrap_github_cli != true && $non_interactive != true && -r /dev/tty && -w /dev/tty ]]; then
  read -r -p 'Install GitHub CLI on this Server through typed local provisioning? [y/N] ' bootstrap_answer </dev/tty
  case "$bootstrap_answer" in [Yy]|[Yy][Ee][Ss]) bootstrap_github_cli=true ;; esac
fi

if [[ -n "$publish_dir" ]]; then
  [[ -d "$publish_dir" ]] || fail "Published directory does not exist: $publish_dir"
  publish_dir="$(cd -- "$publish_dir" && pwd)"
  [[ -x "$publish_dir/CodexServer" ]] || fail 'The publish directory must contain an executable CodexServer apphost.'
else
  [[ -r /etc/os-release ]] || fail 'Cannot identify the operating system; Ubuntu 24.04 is required.'
  # shellcheck disable=SC1091
  . /etc/os-release
  [[ "${ID:-}" == ubuntu && "${VERSION_ID:-}" == 24.04 ]] || fail "Unsupported operating system: ${PRETTY_NAME:-unknown}. Ubuntu 24.04 is required."
  [[ "$(uname -m)" == x86_64 ]] || fail "Unsupported architecture: $(uname -m). Linux x64 is required."
  for command_name in curl tar sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || fail "Required command not found: $command_name"
  done

  if [[ -z "$version" ]]; then
    latest_url="$(curl --fail --silent --show-error --location --output /dev/null --write-out '%{url_effective}' "${release_root}/latest")" || fail 'Could not resolve the latest release from GitHub.'
    version="${latest_url##*/}"
    version="${version#v}"
    version="${version#V}"
    [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || fail "Could not read a release version from GitHub URL: $latest_url"
  fi

  work_dir="$(mktemp -d)" || fail 'Could not create a temporary download directory.'
  artifact="codex-server-${version}-linux-x64.tar.gz"
  curl --fail --silent --show-error --location "${release_root}/download/v${version}/${artifact}" --output "$work_dir/$artifact" || fail "Could not download release artifact $artifact."
  curl --fail --silent --show-error --location "${release_root}/download/v${version}/checksums.txt" --output "$work_dir/checksums.txt" || fail "Could not download checksums for release $version."
  (cd "$work_dir" && awk -v artifact="$artifact" '$2 == artifact && length($1) == 64 { print; found++ } END { if (found != 1) exit 1 }' checksums.txt | sha256sum --check --status) || fail "Checksum verification failed for $artifact. No files were installed."
  mkdir "$work_dir/publish"
  tar --extract --gzip --file "$work_dir/$artifact" --directory "$work_dir/publish" || fail "Could not extract $artifact."
  publish_dir="$work_dir/publish"
  [[ -x "$publish_dir/CodexServer" ]] || fail 'The verified archive does not contain an executable CodexServer apphost.'
fi

getent group codex-server >/dev/null || groupadd --system codex-server
id codex-server >/dev/null 2>&1 || useradd --system --gid codex-server --home-dir /var/lib/codex-server --create-home --shell /usr/sbin/nologin codex-server
install_provisioning_sudoers

install -d -o root -g root -m 0755 /opt/codex-server/releases
install -d -o root -g codex-server -m 0750 /etc/codex-server
install -d -o codex-server -g codex-server -m 0700 /var/lib/codex-server
install -d -o codex-server -g codex-server -m 0750 /var/log/codex-server
previous_target=""
if [[ -L /opt/codex-server/current ]]; then
  previous_target="$(readlink /opt/codex-server/current)"
fi

if [[ -n "$version" ]]; then
  release_dir="/opt/codex-server/releases/$version"
  if [[ ! -e "$release_dir/CodexServer" ]]; then
    [[ ! -e "$release_dir" ]] || fail "Existing release directory is incomplete: $release_dir"
    staging_dir="/opt/codex-server/releases/.${version}.installing.$$"
    install -d -o root -g root -m 0755 "$staging_dir"
    cp -a "$publish_dir"/. "$staging_dir"/
    chown -R root:root "$staging_dir"
    chmod 0755 "$staging_dir/CodexServer"
    mv -- "$staging_dir" "$release_dir"
    staging_dir=""
  fi
  new_target="releases/$version"
else
  release_id="local-$(date -u +%Y%m%d%H%M%S)-$$"
  release_dir="/opt/codex-server/releases/$release_id"
  staging_dir="/opt/codex-server/releases/.${release_id}.installing"
  install -d -o root -g root -m 0755 "$staging_dir"
  cp -a "$publish_dir"/. "$staging_dir"/
  chown -R root:root "$staging_dir"
  chmod 0755 "$staging_dir/CodexServer"
  mv -- "$staging_dir" "$release_dir"
  staging_dir=""
  new_target="releases/$release_id"
fi

ensure_server_environment /etc/codex-server/server.env
chown root:codex-server /etc/codex-server/server.env
chmod 0640 /etc/codex-server/server.env
install -d -o root -g root -m 0755 /usr/local/bin
write_operator_helper > /usr/local/bin/codex-server
chown root:root /usr/local/bin/codex-server
chmod 0755 /usr/local/bin/codex-server
cat > /etc/systemd/system/codex-server.service <<'EOF'
[Unit]
Description=Codex Server
After=network.target

[Service]
Type=exec
User=codex-server
Group=codex-server
WorkingDirectory=/opt/codex-server/current
EnvironmentFile=/etc/codex-server/server.env
ExecStart=/opt/codex-server/current/CodexServer
Restart=on-failure
RestartSec=5
RuntimeDirectory=codex-server
RuntimeDirectoryMode=0750
LogsDirectory=codex-server
LogsDirectoryMode=0750
Environment=TMPDIR=/run/codex-server
TimeoutStopSec=30
UMask=0077

[Install]
WantedBy=multi-user.target
EOF
chmod 0644 /etc/systemd/system/codex-server.service

ln -sfn "$new_target" /opt/codex-server/current.new
mv -Tf /opt/codex-server/current.new /opt/codex-server/current
systemctl daemon-reload
systemctl enable codex-server
if systemctl is-active --quiet codex-server; then
  start_command=restart
else
  start_command=start
fi
if ! systemctl "$start_command" codex-server; then
  if [[ -n "$previous_target" ]]; then
    ln -sfn "$previous_target" /opt/codex-server/current.new
    mv -Tf /opt/codex-server/current.new /opt/codex-server/current
    systemctl daemon-reload
    systemctl restart codex-server || true
  else
    rm -f /opt/codex-server/current
  fi
  fail "Could not start Codex Server after installing ${version:-the local build}; the previous release selection was restored when available. Check journalctl -u codex-server."
fi
if [[ $bootstrap_github_cli == true ]]; then bootstrap_github_cli_installation; fi
printf 'Codex Server %s installed. Service: ' "${version:-local build}"
systemctl is-active codex-server || true
cat <<'EOF'
Configure /etc/codex-server/server.env, then restart with: systemctl restart codex-server
The management token is stored in /etc/codex-server/server.env. Retrieve it when needed with:
sudo sed -n 's/^[[:space:]]*CODEX_SERVER_MANAGEMENT_TOKEN[[:space:]]*=[[:space:]]*//p' /etc/codex-server/server.env
The default endpoint is http://127.0.0.1:5090. Check status: systemctl status codex-server
Logs: journalctl -u codex-server
Create a short-lived, one-use Worker bootstrap token: sudo codex-server worker-token create
EOF
