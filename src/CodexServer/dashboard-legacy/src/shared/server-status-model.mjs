const failedStates = new Set(['error', 'failed', 'offline']);

/** Present only the authoritative /api/status response and its successful fetch time. */
export function serverStatusPresentation(data, hasError, loading, updatedAt) {
  const freshness = hasError ? updatedAt > 0 ? 'stale' : 'unknown' : updatedAt > 0 ? 'current' : 'unknown';
  if (hasError) return { kind: 'unavailable', tone: 'error', freshness };
  if (loading) return { kind: 'loading', tone: 'gray', freshness };
  if (!data || typeof data.state !== 'string' || !data.state.trim()) return { kind: 'unknown', tone: 'gray', freshness };

  const state = data.state.trim();
  if (state.toLowerCase() === 'ready') return { kind: 'ready', tone: 'success', freshness };
  if (failedStates.has(state.toLowerCase())) return { kind: 'offline', tone: 'error', freshness };
  return { kind: 'not-ready', tone: 'warning', freshness, state };
}
