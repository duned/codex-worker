const activeExecutionStates = new Set(['working', 'running']);

export function isActiveExecutionState(value) {
  return activeExecutionStates.has(String(value ?? '').toLowerCase());
}

export function statusColor(value) {
  const state = String(value ?? '').toLowerCase();
  if (isActiveExecutionState(state)) return 'info';
  if (['online', 'connected', 'ready', 'current', 'healthy', 'installed', 'satisfied', 'completed', 'succeeded', 'synchronized', 'active', 'updated'].includes(state)) return 'success';
  if (['stale', 'draining', 'drain-requested', 'starting', 'updating', 'restarting', 'reconnecting', 'not-ready', 'required', 'missing', 'degraded', 'pending', 'assigned', 'busy', 'available', 'cached', 'out-of-sync', 'not-synchronized'].includes(state)) return 'warning';
  if (['offline', 'disconnected', 'failed', 'error', 'timedout', 'update-failed', 'restart-failed', 'capability-regression', 'configuration-incompatible'].includes(state)) return 'error';
  return 'gray';
}
