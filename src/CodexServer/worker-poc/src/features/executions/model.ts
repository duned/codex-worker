import { t, statusLabel } from '../../shared/i18n';
import type { ExecutionSummary } from '../../shared/api/contracts';
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
export function canReconcile(item: Execution) { return item.state === 'Failed' && item.recoveryState === 'LeaseExpiredUncertain' && item.lease?.state === 'Expired'; }
export function presentation(item: Execution): { text: string; tone: 'gray' | 'success' | 'warning' | 'error' } {
  if (item.recoveryState === 'LeaseExpiredUncertain') return { text: t("executions.integrationUncertainReviewEvidence"), tone: 'warning' };
  if (item.state === 'Failed') return { text: t("executions.executionFailed"), tone: 'error' };
  if (item.state === 'Completed') return { text: t("executions.completed"), tone: 'success' };
  return { text: item.currentStage ? `${statusLabel(item.state)} · ${statusLabel(item.currentStage)}` : statusLabel(item.state), tone: ['Running', 'Assigned'].includes(item.state) ? 'warning' : 'gray' };
}
export function validEvidence(disposition: string, evidence: string, commit: string) {
  return !!evidence.trim() && evidence.length <= 1000 && !/[\u0000-\u001f\u007f-\u009f]/.test(evidence) &&
    (disposition === 'NotIntegrated' || disposition === 'Integrated' && /^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$/.test(commit));
}
