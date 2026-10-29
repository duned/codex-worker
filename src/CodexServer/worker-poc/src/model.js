import { t } from './shared/i18n';
import { isActiveExecutionState, statusColor } from './shared/status-model.mjs';
// Presentation only: scheduling and readiness remain Server-owned.
export { isActiveExecutionState, statusColor };
const connectedAvailability = new Set(['online', 'draining', 'connected']);
const disconnectedAvailability = new Set(['offline', 'disconnected', 'stale']);

/** Resolve the prominent connection summary from both Server observations.
 * Missing or contradictory evidence stays unknown; this is presentation only.
 * @returns {{ state: 'Disconnected' | 'Connected' | 'Unknown', tone: 'error' | 'success' | 'gray' }} */
export function workerConnectionStatus(worker, node) {
  const availability = typeof worker?.availability === 'string' ? worker.availability.toLowerCase() : '';
  const connectivity = typeof node?.connectivity === 'string' ? node.connectivity.toLowerCase() : '';
  const availabilityConnected = connectedAvailability.has(availability);
  const availabilityDisconnected = disconnectedAvailability.has(availability);
  const nodeConnected = connectivity === 'connected';
  const nodeDisconnected = connectivity === 'disconnected';

  if ((availabilityConnected && nodeDisconnected) || (availabilityDisconnected && nodeConnected)) {
    return { state: 'Unknown', tone: 'gray' };
  }
  if (availabilityDisconnected || nodeDisconnected) return { state: 'Disconnected', tone: 'error' };
  if (!availabilityConnected || !nodeConnected) {
    return { state: 'Unknown', tone: 'gray' };
  }
  return { state: 'Connected', tone: 'success' };
}

/** Map independent Worker observations for the Worker detail header.
 * Configuration is healthy only when the authoritative synchronization result
 * and a current Worker heartbeat are both available.
 */
export function workerDetailStatuses(worker, node, diagnostics) {
  const availability = typeof worker?.availability === 'string' ? worker.availability.toLowerCase() : '';
  const hasHeartbeat = Number.isFinite(Date.parse(worker?.lastHeartbeatAtUtc ?? ''));
  const freshness = availability === 'stale'
    ? { state: 'Stale', tone: 'warning' }
    : hasHeartbeat && ['online', 'draining', 'offline'].includes(availability)
      ? { state: 'Fresh', tone: 'success' }
      : { state: 'Unknown', tone: 'gray' };
  const synchronization = typeof diagnostics?.configurationSynchronization === 'string'
    ? diagnostics.configurationSynchronization.toLowerCase() : '';
  const configuration = freshness.state !== 'Fresh'
    ? { state: 'Unknown', tone: 'gray' }
    : synchronization === 'synchronized'
      ? { state: 'Ok', tone: 'success' }
      : ['error', 'not-synchronized', 'out-of-sync'].includes(synchronization)
        ? { state: 'Ko', tone: 'error' }
        : { state: 'Unknown', tone: 'gray' };
  const policy = typeof worker?.schedulingPolicy === 'string' ? worker.schedulingPolicy : '';
  const scheduling = policy === 'Enabled'
    ? { state: 'Active', tone: 'success' }
    : policy === 'Disabled'
      ? { state: 'Inactive', tone: 'gray' }
      : policy === 'Draining'
        ? { state: 'Draining', tone: 'warning' }
        : { state: 'Unknown', tone: 'gray' };

  return {
    connection: workerConnectionStatus(worker, node),
    freshness,
    configuration,
    scheduling
  };
}

/** Classify a heartbeat by the age of the Server-reported observation. */
export function workerHeartbeatAge(lastHeartbeatAtUtc, now) {
  const heartbeat = Date.parse(lastHeartbeatAtUtc ?? '');
  if (!Number.isFinite(heartbeat)) return { state: 'Lost', ageMilliseconds: null, tone: 'error' };
  const ageMilliseconds = Math.max(0, now - heartbeat);
  if (ageMilliseconds < 60_000) return { state: 'Fresh', ageMilliseconds, tone: 'success' };
  if (ageMilliseconds < 5 * 60_000) return { state: 'Delayed', ageMilliseconds, tone: 'warning' };
  return { state: 'Lost', ageMilliseconds, tone: 'error' };
}

/** Keep absent capacity distinct from a Worker reporting zero free slots. */
export function workerSlotSummary(worker) {
  return {
    occupied: worker?.activeExecutions ?? null,
    capacity: worker?.maximumCapacity ?? worker?.capacity ?? null,
    available: worker?.availableCapacity ?? null
  };
}

/** A Worker with no managed synchronization contract is not presented as synced. */
export function workerManagedSettingsState(worker, diagnostics) {
  if (!diagnostics) return 'Unavailable';
  const synchronization = diagnostics.configurationSynchronization?.toLowerCase();
  const hasHeartbeat = Number.isFinite(Date.parse(worker?.lastHeartbeatAtUtc ?? '')) &&
    ['online', 'draining', 'offline', 'stale'].includes(worker?.availability?.toLowerCase());
  const currentObservation = workerDetailStatuses(worker, null, diagnostics).freshness.state === 'Fresh';
  if (synchronization === 'synchronized') return currentObservation ? 'Synced' : 'Unknown';
  if (synchronization === 'cached') return 'Cached';
  if (synchronization === 'not-synchronized') return 'Pending';
  if (synchronization === 'error' || synchronization === 'out-of-sync') return 'Needs attention';
  if (synchronization === 'unavailable') return 'Unavailable';
  if (synchronization === 'unknown' && !diagnostics.workerReportedManagedDiagnostics) return hasHeartbeat ? 'Standalone' : 'Unknown';
  return 'Unknown';
}

/** Presentation can only downgrade readiness when connection evidence is not current. */
export function currentWorkerExecutionReadiness(worker, node) {
  if (!node) return 'Unknown';
  const connection = workerConnectionStatus(worker, node);
  if (connection.state === 'Disconnected' || node?.observationsStale === true) return 'not-ready';
  if (connection.state !== 'Connected') return 'Unknown';
  return node?.executionReadiness ?? 'Unknown';
}

/** Keep independent Worker observations separate; missing node data stays unknown. */
export function workerListSignals(worker, node) {
  const connection = workerConnectionStatus(worker, node);
  return {
    connection: connection.state,
    connectionTone: connection.tone,
    freshness: node && typeof node.observationsStale === 'boolean' ? node.observationsStale ? 'Stale' : 'Current' : 'Unknown',
    readiness: currentWorkerExecutionReadiness(worker, node),
    scheduling: worker.schedulingPolicy ?? 'Unknown',
    occupiedSlots: worker.activeExecutions,
    totalSlots: worker.maximumCapacity ?? worker.capacity,
    projects: worker.activeProjects ?? []
  };
}
export const terminalStates = ['Completed', 'Failed', 'Cancelled'];
export function workerExecutions(items, workerId) {
  return (items ?? []).filter(item => item.assignedWorkerId === workerId)
    .sort((a, b) => (Date.parse(b.createdAtUtc) || 0) - (Date.parse(a.createdAtUtc) || 0));
}
export function issueDetailRoute(projectId, issueNumber) {
  if (typeof projectId !== 'string' || !projectId.trim() ||
      !/^[1-9]\d*$/.test(String(issueNumber)) || Number(issueNumber) > 2147483647) return null;
  return `/projects/${encodeURIComponent(projectId)}/issues/${issueNumber}`;
}
export function issueLink(work, projectId) {
  return work?.type === 'github-issue' ? issueDetailRoute(projectId, work.id) : null;
}
export function timestamp(value) {
  const parsed = value ? Date.parse(value) : NaN;
  if (!Number.isFinite(parsed)) return t("shared.notReported");
  const date = new Date(parsed);
  const pad = number => String(number).padStart(2, '0');
  return `${pad(date.getDate())}/${pad(date.getMonth() + 1)}/${date.getFullYear()}, ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}
export function duration(item, now) {
  let ms = item.durationMilliseconds;
  if (!Number.isFinite(ms) || ms < 0) {
    const start = Date.parse(item.startedAtUtc);
    const end = item.completedAtUtc ? Date.parse(item.completedAtUtc) : terminalStates.includes(item.state) ? NaN : now;
    ms = end - start;
  }
  if (!Number.isFinite(ms) || ms < 0) return t("shared.durationUnavailable");
  const seconds = Math.floor(ms / 1000);
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}
