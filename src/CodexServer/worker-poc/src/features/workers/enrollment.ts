import type { PairingRequest, WorkerObservation } from '../../shared/api/contracts';
import { record } from '../../shared/api/validation';
export function readPairingRequest(text: string, operation: 'enroll' | 'associate', origin: string): PairingRequest {
  let value: Record<string, unknown>;
  try { value = record(JSON.parse(text)); } catch { throw new Error('Paste the complete public request printed by the node.'); }
  if (Object.keys(value).sort().join(',') !== 'contractVersion,operation,server,workerId' || value.contractVersion !== 1
    || typeof value.workerId !== 'string' || !/^[0-9a-f]{32}$/i.test(value.workerId) || value.operation !== operation || value.server !== origin || !origin.startsWith('https://'))
    throw new Error('Request does not match this HTTPS Server and selected operation. Return to the node; do not edit its request.');
  return value as unknown as PairingRequest;
}
export function registrationInstruction(version: string, operation: 'enroll' | 'associate', origin: string) {
  if (!/^\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.-]+)?$/.test(version) || !origin.startsWith('https://')) return 'A supported pinned release and the final trusted HTTPS Server origin are required.';
  return operation === 'enroll'
    ? `curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/v${version}/packaging/linux/install-worker.sh | sudo bash -s -- --version ${version} --server ${origin} --pair --start`
    : `sudo -u codex-worker env HOME=/var/lib/codex-worker codex-worker register --config /etc/codex-worker/worker.yml --server ${origin} --operation associate --pair\n# After acknowledged registration:\nsudo systemctl start codex-worker`;
}
// Persist only a validated public request, never authorization or completion.
export const pairingDraftKey = 'codex-worker-public-pairing-request';
export function pairingAcknowledged(worker: WorkerObservation | undefined) {
  return worker?.authenticationCredentialStatus === 'active' && worker.availability === 'online'
    && Number.isFinite(Date.parse(worker.lastHeartbeatAtUtc ?? ''));
}
export function restorePairingRequest(text: string | null, origin: string): PairingRequest | undefined {
  if (!text) return undefined;
  try {
    const value = record(JSON.parse(text));
    if (value.operation !== 'enroll' && value.operation !== 'associate') return undefined;
    return readPairingRequest(text, value.operation, origin);
  } catch { return undefined; }
}
