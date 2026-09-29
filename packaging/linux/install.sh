#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer as root (for example, sudo ./install.sh /path/to/publish)." >&2
  exit 1
fi

if [[ $# -ne 1 || ! -d $1 ]]; then
  echo "Usage: $0 <published-worker-directory>" >&2
  exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
publish_dir=$(cd -- "$1" && pwd)
if [[ ! -x $publish_dir/CodexWorker ]]; then
  echo "The publish directory must contain an executable CodexWorker apphost." >&2
  exit 2
fi

getent group codex-worker >/dev/null || groupadd --system codex-worker
id codex-worker >/dev/null 2>&1 || useradd --system --gid codex-worker --home-dir /var/lib/codex-worker --create-home --shell /usr/sbin/nologin codex-worker

install -d -o root -g root -m 0755 /opt/codex-worker
install -d -o root -g codex-worker -m 0750 /etc/codex-worker
install -d -o codex-worker -g codex-worker -m 0700 /var/lib/codex-worker
install -d -o codex-worker -g codex-worker -m 0700 /var/lib/codex-worker/.codex-worker
install -d -o codex-worker -g codex-worker -m 0750 /var/lib/codex-worker/projects

cp -a "$publish_dir"/. /opt/codex-worker/
chown -R root:root /opt/codex-worker
chmod 0755 /opt/codex-worker/CodexWorker

if [[ ! -e /etc/codex-worker/worker.yml ]]; then
  install -o root -g codex-worker -m 0640 "$script_dir/worker.managed.example.yml" /etc/codex-worker/worker.yml
fi
if [[ ! -e /etc/codex-worker/worker.env ]]; then
  install -o root -g codex-worker -m 0640 /dev/null /etc/codex-worker/worker.env
fi
chown root:codex-worker /etc/codex-worker/worker.yml /etc/codex-worker/worker.env
chmod 0640 /etc/codex-worker/worker.yml /etc/codex-worker/worker.env
install -o root -g root -m 0644 "$script_dir/codex-worker.service" /etc/systemd/system/codex-worker.service

systemctl daemon-reload
echo "Codex Worker installed. Configure /etc/codex-worker/worker.yml and worker.env, then run: systemctl enable --now codex-worker"
