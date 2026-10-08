import { t } from './shared/i18n';
// Presentation only: scheduling and readiness remain Server-owned.
export function statusColor(value) {
  const state = String(value ?? '').toLowerCase();
  if (['online', 'connected', 'ready', 'current', 'healthy', 'installed', 'satisfied', 'completed', 'succeeded', 'synchronized', 'active', 'updated'].includes(state)) return 'success';
  if (['stale', 'draining', 'drain-requested', 'starting', 'updating', 'restarting', 'reconnecting', 'not-ready', 'required', 'missing', 'degraded', 'pending', 'running', 'assigned', 'busy', 'available', 'cached', 'out-of-sync', "not-synchronized"].includes(state)) return 'warning';
  if (['offline', 'disconnected', 'failed', 'error', 'timedout', 'update-failed', 'restart-failed', "capability-regression", "configuration-incompatible"].includes(state)) return 'error';
  return 'gray';
}
/** Keep independent Worker observations separate; missing node data stays unknown. */
export function workerListSignals(worker, node) {
  return {
    connection: worker.availability,
    freshness: node ? node.observationsStale ? 'Stale' : 'Current' : 'Unknown',
    readiness: node ? node.executionReadiness : 'Unknown',
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
