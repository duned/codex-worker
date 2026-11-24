import { tryT } from './i18n';
import type { ArchivedBranch, Capability, ExecutionSummary, GitHubIssue, NodeCommandSummary, WorkerObservation, WorkerProjectReadiness, WorkerUpdate } from './api/contracts';
import type { TranslationKey } from './i18n';

/** Visual tone of a status. Semantic colours are fixed: ok, bad, warn, run (animated) and idle. */
export type Tone = 'ok' | 'bad' | 'warn' | 'run' | 'idle';
export interface Status { tone: Tone; label: TranslationKey }

export const steps = ['prepare', 'implement', 'validate', 'integrate', 'report'] as const;
const stageStep: Record<string, number> = { Claiming: 0, Preparing: 0, Codex: 1, Implementing: 1, Validation: 2, Integration: 3, Reporting: 4 };
const stepTimingKeys = [['claimed', 'preparing'], ['codex'], ['validation'], ['integration'], ['reporting']] as const;

/** A project the Worker paused in its own memory (GitHub reconciliation, ambiguous completion, failed preparation). */
export const pausedOnWorker = (project: Pick<WorkerProjectReadiness, 'materializationState' | 'diagnosticCode'>) =>
  project.materializationState === 'blocked' && project.diagnosticCode === 'project-recovery-verification-failed';

/** One health state per Worker with its main cause, most severe first. Connection, scheduling and paused projects
 *  stay separate facts underneath; this is what a list or a header shows. */
export type Health = 'offline' | 'blocked' | 'paused' | 'pausing' | 'degraded' | 'working' | 'ready';
export interface WorkerHealth { state: Health; tone: Tone; label: TranslationKey; cause: { key: TranslationKey; values?: Record<string, string | number> } }
export function workerHealth(worker: WorkerObservation, projectName: (id: string) => string = id => id): WorkerHealth {
  const make = (state: Health, tone: Tone, key: TranslationKey, values?: Record<string, string | number>): WorkerHealth =>
    ({ state, tone, label: `health.${state}` as TranslationKey, cause: { key, values } });
  if (!isOnline(worker)) return make('offline', 'bad', 'workers.offlineReason');
  if (worker.schedulingBlocker) return make('blocked', 'bad', 'health.cause.blocker', { text: schedulingBlockerText(worker.schedulingBlocker) });
  if (worker.schedulingPolicy === 'Disabled') return make('paused', 'warn', 'scheduling.paused.detail');
  if (worker.schedulingPolicy === 'Draining') return make('pausing', 'warn', 'scheduling.pausing.detail');
  if (worker.lifecycleState && worker.lifecycleState !== 'running') return make('blocked', 'bad', 'workers.blockedReason');
  const paused = (worker.managedDiagnostics?.projects ?? []).filter(project =>
    pausedOnWorker({ materializationState: project.state, diagnosticCode: project.diagnosticCode ?? undefined }));
  if (paused.length) return make('degraded', 'warn', 'health.cause.projectsPaused', { list: paused.map(project => projectName(project.projectId)).join(', ') });
  const { used, total } = slots(worker);
  return used ? make('working', 'run', 'health.cause.working', { used, total }) : make('ready', 'ok', 'workers.idle');
}

/** Everything that needs an operator, grouped by cause so each group carries one kind of action. */
export interface AttentionInbox {
  pausedProjects: { worker: WorkerObservation; projectId: string; detail?: string }[];
  maintenance: WorkerObservation[];
  uncertain: ExecutionSummary[];
  ineligible: ExecutionSummary[];
  failed: ExecutionSummary[];
  workers: WorkerObservation[];
}
export function attentionInbox(executions: ExecutionSummary[], workers: WorkerObservation[]): AttentionInbox {
  const latestFailures = attention(executions);
  const pausedProjects = workers.filter(isOnline).flatMap(worker => (worker.managedDiagnostics?.projects ?? [])
    .filter(project => pausedOnWorker({ materializationState: project.state, diagnosticCode: project.diagnosticCode ?? undefined }))
    .map(project => ({ worker, projectId: project.projectId, detail: project.detail ?? undefined })));
  const maintenance = workers.filter(worker => worker.schedulingBlocker === 'worker-operation-unresolved');
  return {
    pausedProjects, maintenance,
    // An uncertain lease keeps its project slot until someone reconciles it.
    uncertain: latestFailures.filter(item => item.recoveryState === 'LeaseExpiredUncertain'),
    ineligible: executions.filter(item => item.state === 'Queued' && !!item.managedEligibilityState && item.managedEligibilityState !== 'eligible'),
    failed: latestFailures.filter(item => item.recoveryState !== 'LeaseExpiredUncertain'),
    // A Worker already listed for its own cause (unresolved operation, paused project) is not repeated as a generic blocker.
    workers: workers.filter(worker => scheduling(worker) === 'blocked' && !maintenance.includes(worker) &&
      !(isOnline(worker) && pausedProjects.some(item => item.worker === worker) && !worker.schedulingBlocker))
  };
}
export const attentionCount = (inbox: AttentionInbox) =>
  inbox.pausedProjects.length + inbox.maintenance.length + inbox.uncertain.length + inbox.ineligible.length + inbox.failed.length + inbox.workers.length;

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

const isIntegrationStage = (execution: ExecutionSummary) =>
  execution.currentStage === 'Integration' || execution.currentStage === 'Reporting';

/** The Worker reports a definitive remote push refusal with product-owned text (nothing was integrated). */
const isPushRejected = (execution: ExecutionSummary) => /rejected the push/i.test(execution.completionSummary ?? '');

/** Plain-language explanation of a finished execution that needs a decision. */
export function explain(execution: ExecutionSummary): { what: TranslationKey; todo: TranslationKey } | undefined {
  if (execution.state !== 'Failed') return undefined;
  if (execution.recoveryState === 'RebaseConflictRequeued') return { what: 'explain.conflict.what', todo: 'explain.conflictRetried.todo' };
  if (execution.recoveryState === 'LeaseExpiredRequeued') return { what: 'explain.workerLost.what', todo: 'explain.workerLostRetried.todo' };
  if (execution.recoveryState === 'LeaseExpiredUncertain') return isIntegrationStage(execution)
    ? { what: 'explain.integrationUncertain.what', todo: 'explain.uncertain.todo' }
    : { what: 'explain.workerLost.what', todo: 'explain.uncertain.todo' };
  if (isConflict(execution)) return execution.integrationResult === 'rebase-conflict'
    ? { what: 'explain.conflict.what', todo: 'explain.conflict.todo' }
    : isPushRejected(execution)
      ? { what: 'explain.pushRejected.what', todo: 'explain.pushRejected.todo' }
      : { what: 'explain.integration.what', todo: 'explain.integration.todo' };
  if (execution.failureClassification === 'CodexInterruption') return { what: 'explain.interrupted.what', todo: 'explain.interrupted.todo' };
  if (execution.failureClassification === 'Blocked') return { what: 'explain.blocked.what', todo: 'explain.blocked.todo' };
  if (execution.failureClassification === 'PreparationFailed') return { what: 'explain.preparation.what', todo: 'explain.preparation.todo' };
  if (execution.failureClassification === 'InfrastructureFailure') return { what: 'explain.infrastructure.what', todo: 'explain.infrastructure.todo' };
  return { what: 'explain.failed.what', todo: 'explain.failed.todo' };
}

/** Archive only hides finished executions. An uncertain lease still needs reconciliation, so it stays visible. */
export const canArchive = (execution: ExecutionSummary) => !execution.archivedAtUtc &&
  ['Completed', 'Failed', 'Cancelled'].includes(execution.state) && !(execution.state === 'Failed' && execution.recoveryState === 'LeaseExpiredUncertain');

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

// Worker restart/update operations (the Server drains the Worker before either).
/** An operation that still owns the Worker: a new restart or update cannot start until it settles or its drain is released. */
export const maintenanceBlocking = (operation: WorkerUpdate | undefined) => !!operation &&
  operation.state !== 'succeeded' && !(operation.state === 'failed' && operation.drainReleasedAtUtc);
/** Diagnostic the Server records when the Worker refused before draining or dispatching anything. */
export const refusedByWorker = 'refused-by-worker-installation-or-local-policy';
export function maintenanceMessage(operation: WorkerUpdate): TranslationKey {
  const kind = operation.restart ? 'restart' : 'update';
  if (operation.state === 'failed') return operation.diagnostic === refusedByWorker ? `maintenance.${kind}.refused`
    : operation.drainReleasedAtUtc ? `maintenance.${kind}.released` : `maintenance.${kind}.failed`;
  return `maintenance.${kind}.${operation.state}`;
}

/** Typed provisioning actions the dashboard offers for a Worker's tools. The node decides which are available
 * (`availableActions`) and its local policy still authorizes each one; elevated actions need explicit consent. */
export const toolActions = {
  login: { command: 'Login', label: 'tools.login', elevation: false, timeoutSeconds: 600 },
  checkauthentication: { command: 'CheckAuthentication', label: 'tools.checkAuthentication', elevation: false, timeoutSeconds: 120 },
  refresh: { command: 'Detect', label: 'tools.refresh', elevation: false, timeoutSeconds: 120 },
  install: { command: 'Install', label: 'tools.install', elevation: true, timeoutSeconds: 600 },
  update: { command: 'Update', label: 'tools.update', elevation: true, timeoutSeconds: 600 }
} as const satisfies Record<string, { command: string; label: TranslationKey; elevation: boolean; timeoutSeconds: number }>;
export type ToolAction = keyof typeof toolActions;

/** Tools whose vendor sign-in must be completed in a terminal on the Worker (the service account has no login shell,
 * so the command runs the CLI directly with that account's HOME): the code the browser returns has to be
 * pasted into the same CLI, and the product does not relay it through the Server (see docs/claude-provisioning.md). */
export const terminalSignIn: Record<string, string> = { 'claude-cli': `sudo -u codex-worker -H sh -c '"$HOME/.local/bin/claude" auth login'` };
export const offeredToolActions = (capability: Capability): ToolAction[] =>
  (Object.keys(toolActions) as ToolAction[]).filter(action => capability.availableActions.includes(action) &&
    !(action === 'login' && Object.hasOwn(terminalSignIn, capability.definition.id)));
/** A failure that only asks the operator to finish something on the Worker, not an error of the tool. */
export const needsLocalCompletion = (command: NodeCommandSummary) => command.failureDetail?.code === 'ManualVendorCliCompletionRequired';

export const commandActive = (command: NodeCommandSummary) => command.status === 'Pending' || command.status === 'Running';

/** Only the vendors' own device-login pages are linked; anything else is shown as text. */
export const deviceLoginUrl = (uri: string) =>
  ['https://github.com/login/device', 'https://auth.openai.com/codex/device'].includes(uri) ? uri : undefined;

/** The newest command for one tool, and whether it still blocks another action on that tool. */
export function toolCommand(commands: NodeCommandSummary[], nodeId: string, capabilityId: string) {
  const latest = commands.filter(item => item.request.nodeId === nodeId && item.request.capabilityId === capabilityId)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc))[0];
  return { latest, busy: !!latest && commandActive(latest) };
}

/** What a tool is doing right now, from the newest evidence: the command history, a request this page just
 * sent and is waiting to see, or the node's own operation state when the history is unavailable. */
export type ToolProgress =
  | { kind: 'command'; command: NodeCommandSummary }
  | { kind: 'requested'; action: string }
  | { kind: 'running'; action?: string }
  | { kind: 'idle' };
export function toolProgress(capability: Capability, latest: NodeCommandSummary | undefined,
  requested: { action: string; atUtc: number } | undefined): ToolProgress {
  if (requested && (!latest || Date.parse(latest.createdAtUtc) < requested.atUtc - 5000)) return { kind: 'requested', action: requested.action };
  if (latest) return { kind: 'command', command: latest };
  if (capability.state.operation?.state === 'Running') return { kind: 'running', action: capability.state.operation.action };
  return { kind: 'idle' };
}
/** The page keeps following a tool while anything about it can still change. */
export const toolInFlight = (progress: ToolProgress) =>
  progress.kind === 'requested' || progress.kind === 'running' || progress.kind === 'command' && commandActive(progress.command);
/** Maps a node operation name (e.g. "login", "refresh") to the typed command it reports as. */
export const toolActionCommand = (operation: string): string =>
  (toolActions as Record<string, { command: string }>)[operation.toLowerCase()]?.command ?? operation;

/** The actions that can resolve an execution in its current state, best first. A finished execution with a newer
 * attempt for the same Issue has none: the newer attempt is the one to act on. */
export type ExecutionAction = 'cancel' | 'retryIntegration' | 'retryFresh' | 'resolveUncertain' | 'manualCompletion';
const settledRecovery = ['RebaseConflictRequeued', 'LeaseExpiredRequeued', 'OperatorRetryQueued', 'OperatorVerifiedIntegrated'];
export function executionActions(execution: ExecutionSummary, superseded: boolean): ExecutionAction[] {
  if (execution.state === 'Queued') return ['cancel'];
  if (execution.state !== 'Failed' || superseded || settledRecovery.includes(execution.recoveryState ?? '')) return [];
  // The Server keeps an uncertain attempt reserved until an operator records whether it reached the base branch.
  if (execution.recoveryState === 'LeaseExpiredUncertain') return ['resolveUncertain'];
  const actions: ExecutionAction[] = [];
  if (isConflict(execution) && execution.recoverable) actions.push('retryIntegration');
  actions.push('retryFresh');
  if (execution.validationResult === 'passed' && execution.failureClassification === 'InfrastructureFailure') actions.push('manualCompletion');
  return actions;
}
/** A newer attempt for the same work supersedes this one. */
export const newerAttempt = (execution: ExecutionSummary, attempts: ExecutionSummary[]) => attempts
  .filter(other => other.id !== execution.id && other.workReference?.id === execution.workReference?.id && Date.parse(other.createdAtUtc) > Date.parse(execution.createdAtUtc))
  .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc))[0];

/** For a command the Worker's local policy denied, the setting that blocks it. Current Workers report the exact reason
 * (a typed policy code); for older ones it is inferred with the Worker's own rule. Action keys use the Worker's format
 * (WorkerProvisioning.Decide): `<type>:<capability>:<action>`, lower-case. */
export type PolicyHint =
  | { kind: 'allowlist'; key: string; exact: boolean }
  | { kind: 'deniedList'; key: string }
  | { kind: 'setting'; setting: 'enabled' | 'allowCredentials' | 'allowNonPrivileged'; exact: boolean }
  | { kind: 'elevation' };
const policyActionKey = (command: NodeCommandSummary) => {
  const { capabilityId, action } = command.request;
  // Same as the Worker: GitHub setup/SSH/repository actions and every sign-in action are authentication keys.
  const authentication = ['Login', 'Logout', 'CheckAuthentication', 'PrepareAuthentication', 'GenerateSshKey', 'InspectSshKey', 'RemoveSshKey', 'VerifyRepositoryAccess'].includes(action);
  return `${authentication ? 'authentication' : 'tool'}:${capabilityId}:${action}`.toLowerCase();
};
export function deniedPolicyHint(command: NodeCommandSummary): PolicyHint | undefined {
  if (command.status !== 'Failed' || command.diagnostic !== 'Denied') return undefined;
  const key = policyActionKey(command);
  switch (command.failureDetail?.code) {
    case 'PolicyActionNotAllowlisted': return { kind: 'allowlist', key, exact: true };
    case 'PolicyActionDenied': return { kind: 'deniedList', key };
    case 'PolicyProvisioningDisabled': return { kind: 'setting', setting: 'enabled', exact: true };
    case 'PolicyCredentialsDisabled': return { kind: 'setting', setting: 'allowCredentials', exact: true };
    case 'PolicyNonPrivilegedDisabled': return { kind: 'setting', setting: 'allowNonPrivileged', exact: true };
    case 'ElevationNotRequested': return { kind: 'elevation' };
  }
  const { capabilityId, action } = command.request;
  if (['Install', 'Update', 'Uninstall', 'Configure'].includes(action) && capabilityId !== 'claude-cli') return { kind: 'allowlist', key, exact: false };
  if (['Login', 'Logout', 'PrepareAuthentication', 'GenerateSshKey', 'RemoveSshKey'].includes(action)) return { kind: 'setting', setting: 'allowCredentials', exact: false };
  return { kind: 'setting', setting: 'allowNonPrivileged', exact: false };
}

// Archived branch cleanup: the Server proves on GitHub that a branch's exact tip is already in the base branch.
/** A branch the Server reported as deletable, with the exact tip it inspected (deletion is refused if the tip moves). */
export const deletableBranch = (branch: ArchivedBranch) => branch.decision === 'eligible' && /^[a-f0-9]{40}([a-f0-9]{24})?$/i.test(branch.tip);
/** Splits a selection into requests the Server accepts (at most 100 branches each). */
export const branchBatches = <T>(items: T[], size = 100) => Array.from({ length: Math.ceil(items.length / size) }, (_, index) => items.slice(index * size, (index + 1) * size));

/** Readable text for a Server scheduling blocker code (unknown codes are shown as sent). */
export const schedulingBlockerText = (code: string) => tryT(`workers.blocker.${code}`, code);
