import { record } from '../../shared/api/validation';
export interface Credential {
  id: string; provider: string; type: string; secretReference: string; status: string;
  version: number; createdAtUtc: string; updatedAtUtc: string; assignedWorkerId?: string; revokedAtUtc?: string;
}
export function credential(value: unknown): Credential {
  const item = record(value);
  const keys = ['id', 'provider', 'type', 'secretReference', 'status', 'createdAtUtc', 'updatedAtUtc'] as const;
  if (keys.some(key => typeof item[key] !== 'string') || !Number.isSafeInteger(item.version) || Number(item.version) < 1
    || ['assignedWorkerId', 'revokedAtUtc'].some(key => item[key] != null && typeof item[key] !== 'string')) throw Error('Invalid credential metadata.');
  // Never retain unknown fields, including secret payloads.
  return { id: String(item.id), provider: String(item.provider), type: String(item.type), secretReference: String(item.secretReference),
    status: String(item.status), version: Number(item.version), createdAtUtc: String(item.createdAtUtc), updatedAtUtc: String(item.updatedAtUtc),
    assignedWorkerId: typeof item.assignedWorkerId === 'string' ? item.assignedWorkerId : undefined,
    revokedAtUtc: typeof item.revokedAtUtc === 'string' ? item.revokedAtUtc : undefined };
}
export function credentials(value: unknown): Credential[] {
  if (!Array.isArray(value) || value.length > 10000) throw Error('Invalid credential list.');
  return value.map(credential);
}
export const credentialPath = (id: string) => `/api/v1/credentials/${encodeURIComponent(id)}`;
export type CredentialAttempt = { kind: 'create'; provider: string; type: string; existingIds: string[] }
  | { kind: 'replace' | 'assign' | 'revoke'; before: Credential; workerId?: string };
export function credentialOutcome(attempt: CredentialAttempt, items: Credential[]) {
  if (attempt.kind === 'create') {
    const matches = items.filter(c => !attempt.existingIds.includes(c.id) && c.provider === attempt.provider.trim() && c.type === attempt.type.trim());
    return matches.length === 1 ? matches[0] : undefined;
  }
  const current = items.find(c => c.id === attempt.before.id);
  if (!current) return undefined;
  return (attempt.kind === 'replace' ? current.version === attempt.before.version + 1 && current.status === 'Ready'
    : attempt.kind === 'assign' ? current.assignedWorkerId === attempt.workerId && current.status === 'Ready'
    : current.status === 'Revoked') ? current : undefined;
}
