import type { ObserverSummary } from '../../shared/api/contracts';
// Connectivity is a receipt observation, independent of reported execution readiness.
// Staleness never proves that a standalone host is offline.
export function observerConnection(observer: ObserverSummary) {
  if (observer.registrationState === 'revoked') return 'revoked';
  if (observer.registrationState === 'pairing-pending') return 'pending';
  if (observer.registrationState === 'pairing-expired') return 'expired';
  if (!observer.snapshotReceivedAtUtc) return 'awaiting';
  if (observer.freshness === 'unknown') return 'unknown';
  if (observer.freshness !== 'fresh') return 'stale';
  if (observer.reportedLifecycle === 'stopped') return 'offline';
  return 'connected';
}
export function observerIssueUrl(repository: string, number: number) {
  return /^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+$/.test(repository) &&
    repository.split('/').every(part => part !== '.' && part !== '..') && Number.isSafeInteger(number) && number > 0
    ? `https://github.com/${repository}/issues/${number}` : undefined;
}

// Accepted registrations only; pairing and revoked records belong to administration.
export function acceptedObservers(items: ObserverSummary[]) {
  return items.filter(item => item.registrationState === 'reporting' || item.registrationState === 'awaiting-first-report');
}
export function observerName(item: ObserverSummary) {
  return item.name || `obs_…${item.observerId.slice(-8)}`;
}
export function observerTerminal(item: import('../../shared/api/contracts').ObserverExecutionObservation['execution']) {
  return item.outcome != null || item.completedAtUtc != null ||
    ['Completed', 'Blocked', 'Failed', 'IntegrationConflict', 'InfrastructureFailure', 'Cancelled', 'Superseded'].includes(item.stage);
}
export function observerExecutions(inventory: import('../../shared/api/contracts').ObserverInventory | undefined) {
  const accepted = new Set(acceptedObservers(inventory?.observers ?? []).map(item => item.observerId));
  const latest = new Map<string, import('../../shared/api/contracts').ObserverInventoryExecution>();
  for (const item of inventory?.executions ?? []) {
    if (!accepted.has(item.observerId)) continue;
    const key = `${item.observerId}:${item.observation.execution.localExecutionId}`;
    const previous = latest.get(key);
    // Terminal projections are authoritative even if a delayed running report arrives later.
    if (!previous || !observerTerminal(previous.observation.execution) &&
      (observerTerminal(item.observation.execution) || Date.parse(item.observation.observedAtUtc) > Date.parse(previous.observation.observedAtUtc))) latest.set(key, item);
  }
  return [...latest.values()].sort((a, b) => Date.parse(b.observation.observedAtUtc) - Date.parse(a.observation.observedAtUtc));
}
