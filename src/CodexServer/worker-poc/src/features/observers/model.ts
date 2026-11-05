import type { ObserverSummary } from '../../shared/api/contracts';
// Connectivity is a receipt observation, independent of reported execution readiness.
// Staleness never proves that a standalone host is offline.
export function observerConnection(observer: ObserverSummary) {
  if (observer.registrationState === 'revoked') return 'revoked';
  if (observer.registrationState === 'pairing-pending') return 'pending';
  if (observer.registrationState === 'pairing-expired') return 'expired';
  if (!observer.snapshotReceivedAtUtc) return 'awaiting';
  if (observer.freshness !== 'fresh') return 'stale';
  if (observer.reportedLifecycle === 'stopped') return 'offline';
  return 'connected';
}
export function observerIssueUrl(repository: string, number: number) {
  return /^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+$/.test(repository) &&
    repository.split('/').every(part => part !== '.' && part !== '..') && Number.isSafeInteger(number) && number > 0
    ? `https://github.com/${repository}/issues/${number}` : undefined;
}
