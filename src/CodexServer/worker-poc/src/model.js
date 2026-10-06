// Presentation only: scheduling and readiness remain Server-owned.
export function statusColor(value) {
  const state = String(value ?? '').toLowerCase();
  if (['online', 'connected', 'ready', 'current', 'healthy', 'installed', 'satisfied', 'completed', 'succeeded', 'synchronized', 'active', 'updated'].includes(state)) return 'success';
  if (['stale', 'offline', 'disconnected', 'draining', 'drain-requested', 'starting', 'updating', 'restarting', 'reconnecting', 'not-ready', 'required', 'missing', 'degraded', 'pending', 'running', 'assigned', 'busy', 'available', 'cached', 'out-of-sync', 'not-synchronized'].includes(state)) return 'warning';
  if (['failed', 'error', 'timedout', 'update-failed', 'restart-failed', 'capability-regression', 'configuration-incompatible'].includes(state)) return 'error';
  return 'gray';
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
  return Number.isFinite(parsed) ? new Date(parsed).toISOString().replace('T', ' ').replace('.000Z', ' UTC') : 'Not reported';
}
export function duration(item, now) {
  let ms = item.durationMilliseconds;
  if (!Number.isFinite(ms) || ms < 0) {
    const start = Date.parse(item.startedAtUtc);
    const end = item.completedAtUtc ? Date.parse(item.completedAtUtc) : terminalStates.includes(item.state) ? NaN : now;
    ms = end - start;
  }
  if (!Number.isFinite(ms) || ms < 0) return 'Duration unavailable';
  const seconds = Math.floor(ms / 1000);
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}
