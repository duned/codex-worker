import { t } from './shared/i18n';
import { isActiveExecutionState, statusColor } from './shared/status-model.mjs';
// Presentation only: scheduling and readiness remain Server-owned.
export { isActiveExecutionState, statusColor };
const connectedAvailability = new Set(['online', 'draining', 'connected']);
const disconnectedAvailability = new Set(['offline', 'disconnected']);

/** Resolve the prominent connection summary from both Server observations.
 * Missing or contradictory evidence stays unknown; this is presentation only.
 * @returns {{ state: 'Disconnected' | 'Stale' | 'Connected' | 'Unknown', tone: 'error' | 'warning' | 'success' | 'gray' }} */
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
  if (!availabilityConnected || !nodeConnected || typeof node?.observationsStale !== 'boolean') {
    return { state: 'Unknown', tone: 'gray' };
  }
  return node.observationsStale
    ? { state: 'Stale', tone: 'warning' }
    : { state: 'Connected', tone: 'success' };
}

/** Presentation can only downgrade readiness when connection evidence is not current. */
export function currentWorkerExecutionReadiness(worker, node) {
  const connection = workerConnectionStatus(worker, node);
  if (connection.state === 'Disconnected' || connection.state === 'Stale') return 'not-ready';
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
export function issueLink(work, repository) {
  if (work?.type !== 'github-issue' || !/^[1-9]\d*$/.test(work.id)) return null;
  if (work.url) {
    try {
      const url = new URL(work.url);
      if (url.origin === 'https://github.com' && !url.username && !url.password && !url.search && !url.hash &&
          /^\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/issues\/[1-9]\d*$/.test(url.pathname) && url.pathname.split('/').at(-1) === work.id) return url.href;
    } catch { /* Fall back to the central repository identity. */ }
  }
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository ?? '') &&
    !repository.split('/').some(part => part === '.' || part === '..')
    ? `https://github.com/${repository}/issues/${work.id}` : null;
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
