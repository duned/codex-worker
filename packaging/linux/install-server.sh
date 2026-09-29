#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer as root (for example, sudo ./install-server.sh /path/to/publish)." >&2
  exit 1
fi

if [[ $# -ne 1 || ! -d $1 ]]; then
  echo "Usage: $0 <published-server-directory>" >&2
  exit 2
fi

publish_dir=$(cd -- "$1" && pwd)
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
if [[ ! -x $publish_dir/CodexServer ]]; then
  echo "The publish directory must contain an executable CodexServer apphost." >&2
  exit 2
fi

getent group codex-server >/dev/null || groupadd --system codex-server
id codex-server >/dev/null 2>&1 || useradd --system --gid codex-server --home-dir /var/lib/codex-server --create-home --shell /usr/sbin/nologin codex-server

install -d -o root -g root -m 0755 /opt/codex-server
install -d -o root -g codex-server -m 0750 /etc/codex-server
install -d -o codex-server -g codex-server -m 0700 /var/lib/codex-server
cp -a "$publish_dir"/. /opt/codex-server/
chown -R root:root /opt/codex-server
chmod 0755 /opt/codex-server/CodexServer

if [[ ! -e /etc/codex-server/server.env ]]; then
  install -o root -g codex-server -m 0640 /dev/null /etc/codex-server/server.env
fi
chown root:codex-server /etc/codex-server/server.env
chmod 0640 /etc/codex-server/server.env
install -o root -g root -m 0644 "$script_dir/codex-server.service" /etc/systemd/system/codex-server.service

systemctl daemon-reload
echo "Codex Server installed. Configure /etc/codex-server/server.env, then run: systemctl enable --now codex-server"
