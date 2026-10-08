import type { ExecutionSummary, NodeSummary, WorkerObservation } from '../../shared/api/contracts';
export function executionNeedsAttention(item: ExecutionSummary) {
  return !!(item.pendingReason || item.recoveryState || item.managedEligibilityState === 'blocked');
}
export function workerAttention(worker: WorkerObservation, nodes: NodeSummary[] | undefined) {
  const node = nodes?.find(item => item.kind === 'worker' && item.id === worker.workerId);
  const reasons = [];
  if (worker.availability !== 'online') reasons.push(`Connection: ${worker.availability}`);
  if (!node) reasons.push('Readiness observations unavailable');
  else if (node.observationsStale) reasons.push('Readiness observations stale');
  else if (node.executionReadiness !== 'ready') reasons.push(`Execution prerequisites: ${node.executionReadiness}`);
  if (worker.schedulingPolicy !== 'Enabled') reasons.push(`Scheduling: ${worker.schedulingPolicy ?? 'unavailable'}`);
  return reasons;
}
// Missing metrics stay unknown; capacity is reported, never inferred from scheduling policy.
export function aggregateCapacity(workers: WorkerObservation[] | undefined, field: 'maximumCapacity' | 'activeExecutions' | 'availableCapacity') {
  if (!workers || workers.some(worker => worker[field] == null)) return 'Unavailable';
  return workers.reduce((sum, worker) => sum + (worker[field] ?? 0), 0);
}

export function activityTime(item: ExecutionSummary) {
  return Math.max(...[item.createdAtUtc, item.assignedAtUtc, item.startedAtUtc, item.completedAtUtc]
    .map(value => value ? Date.parse(value) : NaN).filter(Number.isFinite), 0);
}
export function recentProjects(projects: import('../../shared/api/contracts').ProjectSummary[], activity: ExecutionSummary[]) {
  const latest = new Map<string, ExecutionSummary>();
  for (const item of activity) {
    const previous = latest.get(item.projectId);
    if (!previous || activityTime(item) > activityTime(previous)) latest.set(item.projectId, item);
  }
  // Preserve catalog order for ties and projects without recorded activity.
  return projects.map(project => ({ project, latest: latest.get(project.id) }))
    .sort((a, b) => (b.latest ? activityTime(b.latest) : -1) - (a.latest ? activityTime(a.latest) : -1)).slice(0, 5);
}
export const completedStatuses = ['Completed', 'Failed', 'Cancelled'];
export function completedExecutions(activity: ExecutionSummary[], status = 'All') {
  return activity.filter(item => completedStatuses.includes(item.state) && (status === 'All' || item.state === status))
    .sort((a, b) => (Date.parse(b.completedAtUtc ?? '') || 0) - (Date.parse(a.completedAtUtc ?? '') || 0));
}
export function sectionMessage(data: unknown[] | undefined, loading: boolean, error: string | undefined, empty: string) {
  return error ? 'Data unavailable. Retry with Refresh system state.' : !data ? loading ? 'Loading…' : 'Data unavailable.' : !data.length ? empty : undefined;
}
export function repositoryLink(repository: string) {
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) && !repository.split('/').some(part => part === '.' || part === '..')
    ? `https://github.com/${repository}` : undefined;
}
