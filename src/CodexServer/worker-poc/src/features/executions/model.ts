import { t, statusLabel, type TranslationKey } from '../../shared/i18n';
import { isActiveExecutionState } from '../../shared/status-model.mjs';
import type { ExecutionMaintenanceCommand, ExecutionMaintenanceDetail, ExecutionMaintenanceObservation, ExecutionSummary, NodeSummary, WorkerObservation } from '../../shared/api/contracts';
import { executions, record } from '../../shared/api/validation';
import type { Validator } from '../../shared/api/client';
export interface Execution extends ExecutionSummary {
  assignmentId?: string; executionId?: string; workerExecutionId?: string;
  validationResult?: string; integrationResult?: string; failureClassification?: string;
  recoverable?: boolean; retryOfExecutionId?: string; attemptNumber?: number; workspaceRecovery?: string;
  missingRequirements?: string[];
  lease?: { executionId: string; workerId: string; generation: number; acquiredAtUtc: string; expiresAtUtc: string; state: string; renewalIntervalSeconds?: number };
}
export const execution: Validator<Execution> = value => {
  executions([value]);
  const item = record(value);
  for (const key of ['assignmentId', 'executionId', 'workerExecutionId', 'validationResult', 'integrationResult', 'failureClassification', 'retryOfExecutionId', 'workspaceRecovery']) {
    if (item[key] != null && typeof item[key] !== 'string') throw new Error('Invalid execution evidence.');
  }
  if (item.recoverable != null && typeof item.recoverable !== 'boolean') throw new Error('Invalid recovery evidence.');
  if (item.attemptNumber != null && (!Number.isSafeInteger(item.attemptNumber) || Number(item.attemptNumber) < 1)) throw new Error('Invalid attempt.');
  if (item.missingRequirements != null && (!Array.isArray(item.missingRequirements) || item.missingRequirements.some(x => typeof x !== 'string'))) throw new Error('Invalid requirements.');
  if (item.lease != null) {
    const lease = record(item.lease);
    if (['executionId', 'workerId', 'acquiredAtUtc', 'expiresAtUtc', 'state'].some(key => typeof lease[key] !== 'string') || !Number.isSafeInteger(lease.generation) || Number(lease.generation) < 1 || (lease.renewalIntervalSeconds != null && (!Number.isSafeInteger(lease.renewalIntervalSeconds) || Number(lease.renewalIntervalSeconds) < 1))) throw new Error('Invalid lease.');
  }
  return value as Execution;
};
export const executionList: Validator<Execution[]> = value => executions(value).map(execution);
const guidN = /^[a-fA-F0-9]{32}$/;
const maintenanceActions = ['inventory', 'inspect', 'cleanup', 'archive', 'retry-report'] as const;
const maintenanceStatuses = new Set(['pending', 'running', 'uncertain', 'succeeded', 'refused', 'failed', 'cancelled']);
function maintenanceObservation(value: unknown): ExecutionMaintenanceObservation {
  const item = record(value);
  if (typeof item.executionId !== 'string' || !guidN.test(item.executionId) ||
      !['state', 'recoveryState', 'reportingStatus', 'project'].every(key => typeof item[key] === 'string' && String(item[key]).length <= (key === 'project' ? 200 : 100)) ||
      !Number.isSafeInteger(item.issueNumber) || Number(item.issueNumber) < 1 || typeof item.archived !== 'boolean' ||
      item.serverExecutionId != null && (typeof item.serverExecutionId !== 'string' || !guidN.test(item.serverExecutionId)) ||
      item.assignmentId != null && (typeof item.assignmentId !== 'string' || !guidN.test(item.assignmentId)) ||
      item.generation != null && (!Number.isSafeInteger(item.generation) || Number(item.generation) < 1)) throw new Error('Invalid maintenance observation.');
  return item as unknown as ExecutionMaintenanceObservation;
}
export const maintenanceCommand: Validator<ExecutionMaintenanceCommand> = value => {
  const item = record(value), request = record(item.request);
  const status = item.status;
  if (typeof request.operationId !== 'string' || !guidN.test(request.operationId) ||
      typeof request.workerId !== 'string' || !guidN.test(request.workerId) ||
      !maintenanceActions.includes(request.action as typeof maintenanceActions[number]) ||
      typeof request.apply !== 'boolean' || !Number.isSafeInteger(request.timeoutSeconds) || Number(request.timeoutSeconds) < 5 || Number(request.timeoutSeconds) > 300 ||
      !Number.isSafeInteger(request.limit) || Number(request.limit) < 1 || Number(request.limit) > 100 ||
      !Number.isSafeInteger(request.offset) || Number(request.offset) < 0 || Number(request.offset) > 10000 ||
      typeof status !== 'string' || !maintenanceStatuses.has(status) ||
      typeof item.createdAtUtc !== 'string' || typeof item.authorizedBy !== 'string' || item.authorizedBy.length > 200 ||
      item.deadlineUtc != null && typeof item.deadlineUtc !== 'string' || item.completedAtUtc != null && typeof item.completedAtUtc !== 'string') throw new Error('Invalid maintenance command.');
  if (request.action !== 'inventory' && (typeof request.serverExecutionId !== 'string' || !guidN.test(request.serverExecutionId) ||
      typeof request.workerExecutionId !== 'string' || !guidN.test(request.workerExecutionId) ||
      typeof request.assignmentId !== 'string' || !guidN.test(request.assignmentId) ||
      !Number.isSafeInteger(request.generation) || Number(request.generation) < 1)) throw new Error('Invalid maintenance scope.');
  let report;
  if (item.report != null) {
    const current = record(item.report);
    if (!['succeeded', 'refused', 'failed', 'uncertain'].includes(String(current.outcome)) || typeof current.reason !== 'string' ||
        !/^[a-zA-Z0-9-]{1,100}$/.test(current.reason) || !Array.isArray(current.observations) || current.observations.length > Number(request.limit)) throw new Error('Invalid maintenance report.');
    report = { outcome: String(current.outcome), reason: current.reason, observations: current.observations.map(maintenanceObservation) };
  }
  return { request: request as unknown as ExecutionMaintenanceCommand['request'], status, createdAtUtc: item.createdAtUtc,
    authorizedBy: item.authorizedBy, deadlineUtc: item.deadlineUtc as string | undefined, completedAtUtc: item.completedAtUtc as string | undefined, report };
};
export const maintenanceCommands: Validator<ExecutionMaintenanceCommand[]> = value => {
  if (!Array.isArray(value) || value.length > 100) throw new Error('Invalid maintenance operation list.');
  return value.map(maintenanceCommand);
};
export const maintenanceDetail: Validator<ExecutionMaintenanceDetail> = value => {
  const item = record(value), operation = maintenanceCommand(item.operation);
  if (typeof item.workerStatus !== 'string' || item.workerStatus.length > 100 || typeof item.status !== 'string' || item.status.length > 100 ||
      !Array.isArray(item.observations) || item.observations.length > operation.request.limit) throw new Error('Invalid maintenance details.');
  const observations = item.observations.map(value => {
    const row = record(value);
    if (typeof row.status !== 'string' || row.status.length > 100) throw new Error('Invalid maintenance observation status.');
    const current = { observation: maintenanceObservation(row.observation), status: row.status } as ExecutionMaintenanceDetail['observations'][number];
    if (row.execution != null) current.execution = execution(row.execution);
    else current.execution = null;
    return current;
  });
  const result: ExecutionMaintenanceDetail = { operation, workerStatus: item.workerStatus, observations, status: item.status };
  if (item.execution != null) result.execution = execution(item.execution);
  else result.execution = null;
  return result;
};
export function maintenanceReason(code: string) {
  const reasons: Record<string, TranslationKey> = {
    'already-clean': 'maintenance.reason.alreadyClean', integrated: 'maintenance.reason.integrated', 'archive-resources-remain': 'maintenance.reason.archiveResourcesRemain',
    'execution-active': 'maintenance.reason.executionActive', 'recovery-or-attempt-active': 'maintenance.reason.recoveryActive', 'lineage-inconsistent': 'maintenance.reason.lineageInconsistent',
    'incomplete-recovery-metadata': 'maintenance.reason.provenanceIncomplete', 'inspection-unavailable': 'maintenance.reason.inspectionUnavailable',
    'archive-retention-or-review': 'maintenance.reason.archiveRetention', 'server-authority-required': 'maintenance.reason.serverAuthorityRequired',
    'recovery-protocol-required': 'maintenance.reason.recoveryProtocolRequired', 'worker-drain-required': 'maintenance.reason.drainRequired',
    'completion-report-preview': 'maintenance.reason.reportPreview', 'completion-report-acknowledged': 'maintenance.reason.reportAcknowledged',
    'inventory-observed': 'maintenance.reason.inventoryObserved', 'operator-cancelled-before-dispatch': 'maintenance.reason.operatorCancelled',
    'server-authority-rejected': 'maintenance.reason.serverAuthorityRejected', 'stale-lease-report-reconciliation-required': 'maintenance.reason.staleLease',
    'worker-record-missing': 'maintenance.reason.workerRecordMissing', 'worker-identity-mismatch': 'maintenance.reason.workerIdentityMismatch',
    'terminal-worker-proof-required': 'maintenance.reason.terminalWorkerProofRequired', 'maintenance-failed-inspect-before-retry': 'maintenance.reason.operationFailed'
  };
  const key = reasons[code];
  return key ? t(key) : statusLabel(code);
}
export function archiveApplyAllowed(detail: ExecutionMaintenanceDetail | undefined) {
  if (!detail || detail.operation.request.action !== 'archive' || detail.operation.request.apply ||
      detail.operation.report?.outcome !== 'succeeded' || detail.operation.report.reason !== 'already-clean' || detail.observations.length !== 1) return false;
  const observation = detail.observations[0].observation;
  return ['Completed', 'Blocked', 'Failed', 'IntegrationConflict', 'InfrastructureFailure', 'Cancelled', 'Superseded'].includes(observation.state) &&
    ['operator-cleaned', 'expired-cleaned', 'resumed-cleaned', 'discarded', 'cleaned-no-changes'].includes(observation.recoveryState) &&
    ['none', 'acknowledged'].includes(observation.reportingStatus) && !observation.archived;
}
export const reconciliation: Validator<{ execution: Execution; retry?: Execution }> = value => {
  const item = record(value); return { execution: execution(item.execution), retry: item.retry == null ? undefined : execution(item.retry) };
};
export function cancellationResult(id: string): Validator<Execution> {
  return value => {
    const item = execution(value);
    if (item.id !== id || item.state !== 'Cancelled') throw new Error('Invalid cancellation result.');
    return item;
  };
}
export function reconciliationResult(id: string, disposition: string): typeof reconciliation {
  return value => {
    const result = reconciliation(value);
    const expected = disposition === 'Integrated' ? 'OperatorVerifiedIntegrated' : 'OperatorRetryQueued';
    if (result.execution.id !== id || result.execution.recoveryState !== expected ||
      (disposition === 'NotIntegrated' ? !result.retry || result.retry.retryOfExecutionId !== id : !!result.retry)) throw new Error('Invalid reconciliation result.');
    return result;
  };
}
export const states = ['Queued', 'Assigned', 'Running', 'Completed', 'Failed', 'Cancelled'];
export function query(search: string) {
  const context = new URLSearchParams(search), params = new URLSearchParams({ limit: '50', offset: String(offset(search)) });
  for (const [key, apiKey] of [['project', 'projectId'], ['state', 'state'], ['issue', 'workId']]) {
    const value = context.get(key); if (value) params.set(apiKey, value);
  }
  if (context.get('issue')) params.set('workType', 'github-issue');
  return `/api/v1/executions?${params}`;
}
export function offset(search: string) {
  const value = new URLSearchParams(search).get('offset') ?? '0';
  return /^\d+$/.test(value) ? Math.min(10000, Number(value)) : 0;
}
export function lastActivity(item: Execution) {
  return [item.createdAtUtc, item.assignedAtUtc, item.startedAtUtc, item.completedAtUtc]
    .filter((value): value is string => typeof value === 'string' && Number.isFinite(Date.parse(value)))
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0];
}
export function timeSince(value: string | undefined, now: number) {
  if (!value) return undefined;
  const elapsed = now - Date.parse(value);
  if (!Number.isFinite(elapsed) || elapsed < 0) return undefined;
  const minutes = Math.floor(elapsed / 60000);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}
export interface AttentionAssessment { classification: string; reason: string; nextAction: string; lastActivityAtUtc?: string; canReconcile: boolean }
export function attentionAssessment(item: Execution, worker: WorkerObservation | undefined, node: NodeSummary | undefined, now: number,
  workerInventoryStatus: 'current' | 'loading' | 'unavailable' = 'current'): AttentionAssessment | undefined {
  const active = item.state === 'Assigned' || item.state === 'Running';
  const resolvedRecoveryStates = ['OperatorCleaned', 'ExpiredCleaned', 'ResumedCleaned', 'Discarded', 'CleanedNoChanges', 'CompletionReconciled'];
  const unresolvedRecovery = item.recoverable === true || !!item.recoveryState && !resolvedRecoveryStates.includes(item.recoveryState);
  const activity = lastActivity(item), age = activity ? now - Date.parse(activity) : 0;
  const overAge = age >= 7 * 24 * 60 * 60 * 1000;
  const reconcile = canReconcile(item);
  const result = (classification: string, reason: string, nextAction: string): AttentionAssessment => ({ classification, reason, nextAction, lastActivityAtUtc: activity, canReconcile: reconcile });
  if (item.lease && (item.lease.state === 'Expired' || item.lease.expiresAtUtc && Date.parse(item.lease.expiresAtUtc) <= now) && item.state !== 'Completed' && item.state !== 'Cancelled')
    return result('stale-lease', 'attention.expiredLease', reconcile ? 'attention.reconcileExecution' : 'attention.reviewExecution');
  const workerRelevant = active || unresolvedRecovery;
  const incompleteProof = !guidN.test(item.id) || !guidN.test(item.assignedWorkerId ?? '') || !item.assignmentId || !guidN.test(item.assignmentId) ||
    !item.workerExecutionId || !guidN.test(item.workerExecutionId) || !item.lease || !guidN.test(item.lease.workerId) ||
    item.lease.workerId !== item.assignedWorkerId || item.lease.executionId !== item.id || !Number.isSafeInteger(item.lease.generation) || item.lease.generation < 1;
  if (workerRelevant && incompleteProof) return result('provenance-uncertain', 'attention.provenanceIncomplete', 'attention.inspectExecution');
  if (workerRelevant && !worker) return result('worker-unknown', workerInventoryStatus === 'loading' ? 'attention.workerStateLoading' :
    workerInventoryStatus === 'unavailable' ? 'attention.workerStateUnavailable' : 'attention.workerRecordMissing', 'attention.refreshWorkerState');
  if (workerRelevant && worker && worker.availability !== 'online' && worker.availability !== 'draining')
    return result('worker-offline', 'attention.workerOffline', 'attention.waitForWorker');
  if (workerRelevant && node?.observationsStale === true) return result('worker-data-outdated', 'attention.workerDataOutdated', 'attention.refreshWorkerState');
  if (active && overAge) return result('stale', 'attention.noRecentServerActivity', 'attention.inspectExecution');
  if (item.recoveryState === 'LeaseExpiredUncertain') return result('reconciliation-required', 'attention.reconciliationRequired', 'attention.reconcileExecution');
  if (unresolvedRecovery)
    return result('maintenance-review', 'attention.recoveryNeedsReview', 'attention.inspectExecution');
  return undefined;
}
export function maintenanceInventoryScope(item: Execution, worker: WorkerObservation | undefined, node: NodeSummary | undefined) {
  if (!guidN.test(item.assignedWorkerId ?? '')) return { allowed: false, reason: 'maintenance.missingWorkerProvenance' };
  if (!worker) return { allowed: false, reason: 'maintenance.workerUnavailable' };
  if (worker.workerId !== item.assignedWorkerId) return { allowed: false, reason: 'maintenance.ownershipMismatch' };
  if (worker.availability !== 'online' && worker.availability !== 'draining') return { allowed: false, reason: 'maintenance.workerOffline' };
  if (node?.observationsStale === true) return { allowed: false, reason: 'maintenance.workerDataOutdated' };
  if (!worker.capabilities?.some(capability => capability.type === 'protocol' && capability.name === 'execution-maintenance-v1'))
    return { allowed: false, reason: 'maintenance.protocolUnavailable' };
  return { allowed: true, reason: undefined };
}
export function maintenanceScope(item: Execution, action: 'inspect' | 'cleanup' | 'archive' | 'retry-report', apply: boolean,
  worker: WorkerObservation | undefined, node: NodeSummary | undefined, now: number) {
  if (!guidN.test(item.id) || !guidN.test(item.assignedWorkerId ?? '') || !guidN.test(item.workerExecutionId ?? '') ||
      !guidN.test(item.assignmentId ?? '') || !item.lease || !Number.isSafeInteger(item.lease.generation) || item.lease.generation < 1)
    return { allowed: false, reason: 'maintenance.missingProvenance' };
  const inventory = maintenanceInventoryScope(item, worker, node);
  if (!inventory.allowed) return inventory;
  if (!worker) return { allowed: false, reason: 'maintenance.workerUnavailable' };
  if (item.lease.workerId !== item.assignedWorkerId || item.lease.executionId !== item.id)
    return { allowed: false, reason: 'maintenance.ownershipMismatch' };
  if (action === 'cleanup' || action === 'archive') {
    if (!['Completed', 'Failed'].includes(item.state) || item.lease.state !== 'Released') return { allowed: false, reason: 'maintenance.terminalLeaseRequired' };
    if (apply && (worker.schedulingPolicy !== 'Draining' || worker.activeAssignments !== 0)) return { allowed: false, reason: 'maintenance.drainRequired' };
  }
  if (action === 'archive') {
    const completed = Date.parse(item.completedAtUtc ?? '');
    const ambiguous = ['LeaseExpiredUncertain', 'OperatorRetryQueued'].includes(item.recoveryState ?? '');
    if (!Number.isFinite(completed) || completed > now - 30 * 24 * 60 * 60 * 1000 || ambiguous)
      return { allowed: false, reason: 'maintenance.archiveRetentionRequired' };
  }
  if (action === 'retry-report' && !(item.state === 'Assigned' || item.state === 'Running'
      ? item.lease.state === 'Active' : ['Completed', 'Failed'].includes(item.state) && item.lease.state === 'Released'))
    return { allowed: false, reason: 'maintenance.reportRetryUnavailable' };
  if (action === 'retry-report' && apply && (worker.schedulingPolicy !== 'Draining' || item.lease.state !== 'Active' && item.lease.state !== 'Released'))
    return { allowed: false, reason: 'maintenance.reportRetryUnavailable' };
  return { allowed: true, reason: undefined };
}
export function canReconcile(item: Execution) { return item.state === 'Failed' && item.recoveryState === 'LeaseExpiredUncertain' && item.lease?.state === 'Expired'; }
export function presentation(item: Execution): { text: string; tone: 'gray' | 'success' | 'warning' | 'error' | 'info'; active: boolean } {
  if (item.recoveryState === 'LeaseExpiredUncertain') return { text: t("executions.integrationUncertainReviewEvidence"), tone: 'warning', active: false };
  if (item.state === 'Failed') return { text: t("executions.executionFailed"), tone: 'error', active: false };
  if (item.state === 'Completed') return { text: t("executions.completed"), tone: 'success', active: false };
  const active = isActiveExecutionState(item.state);
  return { text: item.currentStage ? `${statusLabel(item.state)} · ${statusLabel(item.currentStage)}` : statusLabel(item.state), tone: active ? 'info' : item.state === 'Assigned' ? 'warning' : 'gray', active };
}
export function validEvidence(disposition: string, evidence: string, commit: string) {
  return !!evidence.trim() && evidence.length <= 1000 && !/[\u0000-\u001f\u007f-\u009f]/.test(evidence) &&
    (disposition === 'NotIntegrated' || disposition === 'Integrated' && /^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$/.test(commit));
}
