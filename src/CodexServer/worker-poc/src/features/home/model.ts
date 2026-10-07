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
