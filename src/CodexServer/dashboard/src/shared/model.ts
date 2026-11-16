import type { ExecutionSummary, GitHubIssue, WorkerObservation } from './api/contracts';
import type { TranslationKey } from './i18n';

/** Visual tone of a status. Semantic colours are fixed: ok, bad, warn, run (animated) and idle. */
export type Tone = 'ok' | 'bad' | 'warn' | 'run' | 'idle';
export interface Status { tone: Tone; label: TranslationKey }

export const steps = ['prepare', 'implement', 'validate', 'integrate', 'report'] as const;
const stageStep: Record<string, number> = { Claiming: 0, Preparing: 0, Codex: 1, Implementing: 1, Validation: 2, Integration: 3, Reporting: 4 };
const stepTimingKeys = [['claimed', 'preparing'], ['codex'], ['validation'], ['integration'], ['reporting']] as const;

export const isActive = (execution: ExecutionSummary) => execution.state === 'Assigned' || execution.state === 'Running';
export const isConflict = (execution: ExecutionSummary) => execution.failureClassification === 'IntegrationConflict';

export function executionStatus(execution: ExecutionSummary): Status {
  switch (execution.state) {
    case 'Queued': return { tone: 'idle', label: 'status.queued' };
    case 'Assigned': return { tone: 'run', label: 'status.starting' };
    case 'Running': {
      const step = stageStep[execution.currentStage ?? ''] ?? 0;
      return { tone: 'run', label: (['status.preparing', 'status.implementing', 'status.validating', 'status.integrating', 'status.reporting'] as const)[step] };
    }
    case 'Completed': return { tone: 'ok', label: 'status.integrated' };
    case 'Cancelled': return { tone: 'idle', label: 'status.cancelled' };
    case 'Failed':
      if (execution.recoveryState === 'RebaseConflictRequeued') return { tone: 'warn', label: 'status.conflictRetried' };
      if (execution.recoveryState === 'LeaseExpiredRequeued') return { tone: 'warn', label: 'status.workerLostRetried' };
      if (isConflict(execution)) return { tone: 'bad', label: 'status.conflict' };
      if (execution.failureClassification === 'CodexInterruption') return { tone: 'warn', label: 'status.interrupted' };
      return { tone: 'bad', label: 'status.failed' };
    default: return { tone: 'idle', label: 'status.unknown' };
  }
}

/** The step an execution is on (or failed at), and which steps are done. */
export function stepProgress(execution: ExecutionSummary) {
  const timings = execution.stageStartedAtUtc ?? {};
  const lastTimed = stepTimingKeys.reduce((last, keys, index) => keys.some(key => timings[key]) ? index : last, -1);
  const reached = execution.state === 'Completed' ? steps.length : execution.state === 'Queued' ? -1 :
    stageStep[execution.currentStage ?? ''] ?? (lastTimed >= 0 ? lastTimed : execution.state === 'Assigned' ? 0 : -1);
  const failed = execution.state === 'Failed' || execution.state === 'Cancelled';
  return steps.map((name, index) => ({
    name,
    state: index < reached ? 'done' as const : index === reached ? (failed ? 'fail' as const : 'now' as const) : 'todo' as const,
    durationMs: stepDuration(execution, index)
  }));
}

function stepDuration(execution: ExecutionSummary, index: number): number | undefined {
  const timings = execution.stageStartedAtUtc ?? {};
  const start = stepTimingKeys[index].map(key => timings[key]).find(Boolean);
  if (!start) return undefined;
  const next = stepTimingKeys.slice(index + 1).flat().map(key => timings[key]).find(Boolean)
    ?? (isActive(execution) ? undefined : execution.completedAtUtc);
  const end = next ? Date.parse(next) : isActive(execution) ? Date.now() : undefined;
  return end === undefined ? undefined : Math.max(0, end - Date.parse(start));
}

/** Plain-language explanation of a finished execution that needs a decision. */
export function explain(execution: ExecutionSummary): { what: TranslationKey; todo: TranslationKey } | undefined {
  if (execution.state !== 'Failed') return undefined;
  if (execution.recoveryState === 'RebaseConflictRequeued') return { what: 'explain.conflict.what', todo: 'explain.conflictRetried.todo' };
  if (execution.recoveryState === 'LeaseExpiredRequeued') return { what: 'explain.workerLost.what', todo: 'explain.workerLostRetried.todo' };
  if (execution.recoveryState === 'LeaseExpiredUncertain') return { what: 'explain.workerLost.what', todo: 'explain.uncertain.todo' };
  if (isConflict(execution)) return execution.integrationResult === 'rebase-conflict'
    ? { what: 'explain.conflict.what', todo: 'explain.conflict.todo' }
    : { what: 'explain.integration.what', todo: 'explain.conflict.todo' };
  if (execution.failureClassification === 'CodexInterruption') return { what: 'explain.interrupted.what', todo: 'explain.interrupted.todo' };
  if (execution.failureClassification === 'Blocked') return { what: 'explain.blocked.what', todo: 'explain.blocked.todo' };
  return { what: 'explain.failed.what', todo: 'explain.failed.todo' };
}

export const needsAttention = (execution: ExecutionSummary) => execution.state === 'Failed' &&
  !['RebaseConflictRequeued', 'LeaseExpiredRequeued'].includes(execution.recoveryState ?? '');
/** Only the latest attempt of each Issue can need attention: a newer attempt supersedes older failures. */
export function attention(executions: ExecutionSummary[]) {
  const latest = new Map<string, ExecutionSummary>();
  for (const execution of executions) {
    const key = `${execution.projectId}/${execution.workReference?.type}/${execution.workReference?.id}`, current = latest.get(key);
    if (!current || Date.parse(execution.createdAtUtc) > Date.parse(current.createdAtUtc)) latest.set(key, execution);
  }
  return [...latest.values()].filter(needsAttention);
}

/** Latest execution per Issue number for one project. */
export function latestByIssue(executions: ExecutionSummary[]) {
  const latest = new Map<number, ExecutionSummary>();
  for (const execution of executions) {
    if (execution.workReference?.type !== 'github-issue') continue;
    const number = Number(execution.workReference.id), current = latest.get(number);
    if (!current || Date.parse(execution.createdAtUtc) > Date.parse(current.createdAtUtc)) latest.set(number, execution);
  }
  return latest;
}

export function issueStatus(issue: GitHubIssue | undefined, latest: ExecutionSummary | undefined, readyLabel = 'codex-ready'): Status {
  if (latest && latest.state !== 'Cancelled') return executionStatus(latest);
  if (issue?.state?.toLowerCase() === 'closed') return { tone: 'ok', label: 'status.closed' };
  if (issue?.blockedBy.some(blocker => blocker.state?.toLowerCase() !== 'closed')) return { tone: 'warn', label: 'status.waiting' };
  if (issue?.labels.includes(readyLabel)) return { tone: 'idle', label: 'status.ready' };
  return { tone: 'idle', label: 'status.open' };
}

// Workers: connection, scheduling and activity are separate facts.
export const isOnline = (worker: WorkerObservation) => worker.availability === 'online';
export type Scheduling = 'accepting' | 'pausing' | 'paused' | 'blocked';
export function scheduling(worker: WorkerObservation): Scheduling {
  if (worker.schedulingPolicy === 'Disabled') return 'paused';
  if (worker.schedulingPolicy === 'Draining') return 'pausing';
  if (!isOnline(worker) || worker.schedulingBlocker || (worker.lifecycleState && worker.lifecycleState !== 'running')) return 'blocked';
  return 'accepting';
}
export const schedulingTone: Record<Scheduling, Tone> = { accepting: 'ok', pausing: 'warn', paused: 'warn', blocked: 'bad' };
export const slots = (worker: WorkerObservation) => ({
  total: worker.workerExecutionSlots ?? worker.maximumCapacity ?? worker.capacity ?? 1,
  used: worker.activeExecutions ?? worker.activeAssignments ?? 0
});

// Time
export function relative(value: string | undefined, now = Date.now()): { key: TranslationKey; n: number } | undefined {
  if (!value) return undefined;
  const seconds = Math.max(0, Math.round((now - Date.parse(value)) / 1000));
  if (seconds < 60) return { key: 'time.seconds', n: seconds };
  if (seconds < 3600) return { key: 'time.minutes', n: Math.floor(seconds / 60) };
  if (seconds < 86400) return { key: 'time.hours', n: Math.floor(seconds / 3600) };
  return { key: 'time.days', n: Math.floor(seconds / 86400) };
}
export function duration(ms: number | undefined) {
  if (ms === undefined || !Number.isFinite(ms)) return '—';
  const total = Math.round(ms / 1000), h = Math.floor(total / 3600), m = Math.floor(total % 3600 / 60), s = total % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}
export function dateTime(value: string | undefined, language: string) {
  if (!value) return '—';
  return new Date(value).toLocaleString(language === 'es' ? 'es-ES' : 'en-GB', { dateStyle: 'short', timeStyle: 'short' });
}
export const issueNumber = (execution: ExecutionSummary) => execution.workReference?.type === 'github-issue' ? Number(execution.workReference.id) : undefined;
