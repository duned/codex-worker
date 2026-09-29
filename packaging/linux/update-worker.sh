#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this updater as root (for example, sudo ./update-worker.sh /path/to/publish)." >&2
  exit 1
fi

if [[ $# -lt 1 || $# -gt 2 || ! -d $1 ]]; then
  echo "Usage: $0 <published-worker-directory> [loopback-api-base-url]" >&2
  exit 2
fi

publish_dir=$(cd -- "$1" && pwd)
api_base=${2:-http://127.0.0.1:5080}
if [[ ! -x $publish_dir/CodexWorker ]]; then
  echo "The publish directory must contain an executable CodexWorker apphost." >&2
  exit 2
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required to coordinate the Worker drain and readiness checks." >&2
  exit 2
fi

stage_dir=/opt/codex-worker.next.$$
backup_dir=/opt/codex-worker.previous.$(date -u +%Y%m%dT%H%M%S.%N)
drain_pending=false
cleanup() {
  if [[ $drain_pending == true ]] && systemctl is-active --quiet codex-worker; then
    curl --fail --silent --max-time 5 -X POST "$api_base/api/worker/drain/cancel" >/dev/null || true
  fi
  rm -rf -- "$stage_dir"
}
trap cleanup EXIT
install -d -o root -g root -m 0755 "$stage_dir"
cp -a "$publish_dir"/. "$stage_dir"/
chown -R root:root "$stage_dir"
chmod 0755 "$stage_dir/CodexWorker"

if systemctl is-active --quiet codex-worker; then
  if ! curl --fail --silent --show-error --max-time 5 -X POST "$api_base/api/worker/drain" >/dev/null; then
    echo "Could not request a graceful Worker drain; no files were changed." >&2
    exit 1
  fi
  drain_pending=true
  drained=false
  for _ in $(seq 1 300); do
    if response=$(curl --fail --silent --max-time 1 "$api_base/api/worker/drain") && [[ $response =~ \"drainComplete\"[[:space:]]*:[[:space:]]*true ]]; then
      drained=true
      break
    fi
    sleep 1
  done
  if [[ $drained != true ]]; then
    curl --fail --silent --max-time 5 -X POST "$api_base/api/worker/drain/cancel" >/dev/null || true
    drain_pending=false
    echo "Worker did not drain within 300 seconds; its drain request was cancelled and binaries were left unchanged." >&2
    exit 1
  fi
fi

systemctl stop codex-worker
drain_pending=false
if [[ -d /opt/codex-worker ]]; then
  mv /opt/codex-worker "$backup_dir"
fi
if ! mv "$stage_dir" /opt/codex-worker; then
  [[ ! -d $backup_dir ]] || mv "$backup_dir" /opt/codex-worker
  systemctl start codex-worker || true
  exit 1
fi

if systemctl start codex-worker; then
  ready=false
  for _ in $(seq 1 60); do
    if response=$(curl --fail --silent --max-time 1 "$api_base/api/status") &&
      [[ $response =~ \"state\"[[:space:]]*:[[:space:]]*\"running\" ]] &&
      [[ $response =~ \"lifecycleState\"[[:space:]]*:[[:space:]]*\"ready\" ]]; then
      ready=true
      break
    fi
    sleep 1
  done
  if [[ $ready == true ]]; then
    echo "Codex Worker updated and ready. Previous binaries are preserved at $backup_dir."
    exit 0
  fi
fi

echo "Updated Worker did not become ready; restoring the previous binaries." >&2
systemctl stop codex-worker || true
failed_dir=/opt/codex-worker.failed.$(date -u +%Y%m%dT%H%M%S.%N)
mv /opt/codex-worker "$failed_dir"
if [[ -d $backup_dir ]]; then
  mv "$backup_dir" /opt/codex-worker
  systemctl start codex-worker || true
fi
echo "Failed binaries are preserved at $failed_dir." >&2
exit 1
