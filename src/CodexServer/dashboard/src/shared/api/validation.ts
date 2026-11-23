import type { WorkerObservation, ProjectSummary, ExecutionSummary, NodeSummary, WorkerDiagnostics, GitHubIssue, Credential, ExecutionArchiveResult, WorkerRelease, WorkerUpdate, ArchivedBranchPage, ArchivedBranchDeleteResult, BranchSelection } from './contracts';
import type { Validator } from './client';
export function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid object.');
  return value as Record<string, unknown>;
}
// Validate optional presentation fields too: malicious objects must never reach
// React as strings or be mistaken for boolean/number evidence.
function optional(item: Record<string, unknown>, strings: string[] = [], booleans: string[] = [], numbers: string[] = []) {
  if (strings.some(key => item[key] != null && typeof item[key] !== 'string')
    || booleans.some(key => item[key] != null && typeof item[key] !== 'boolean')
    || numbers.some(key => item[key] != null && (typeof item[key] !== 'number' || !Number.isFinite(item[key])))) throw new Error('Invalid optional fields.');
}
function validateWorker(value: unknown) {
  const item = fields(value, ['workerId', 'availability']);
  optional(item, ['workerVersion', 'displayName', 'lifecycleState', 'schedulingPolicy', 'schedulingBlocker', 'lastHeartbeatAtUtc', 'authenticationCredentialStatus', 'authenticationCredentialRevokedAtUtc', 'platform', 'firstRegisteredAtUtc'], [],
    ['capacity', 'maximumCapacity', 'activeExecutions', 'availableCapacity', 'activeAssignments', 'configuredMaximumCapacity', 'workerExecutionSlots', 'configurationRevision']);
  if (item.configuredMaximumCapacity != null && (!Number.isSafeInteger(item.configuredMaximumCapacity) || Number(item.configuredMaximumCapacity) < 1 || Number(item.configuredMaximumCapacity) > 8)) throw new Error('Invalid configured Worker capacity.');
  if (item.workerExecutionSlots != null && (!Number.isSafeInteger(item.workerExecutionSlots) || Number(item.workerExecutionSlots) < 1 || Number(item.workerExecutionSlots) > 8)) throw new Error('Invalid Worker execution slots.');
  if (item.configurationRevision != null && (!Number.isSafeInteger(item.configurationRevision) || Number(item.configurationRevision) < 1)) throw new Error('Invalid Worker configuration revision.');
  if (item.telegram != null) {
    const telegram = fields(item.telegram, []);
    if (typeof telegram.enabled !== 'boolean' || typeof telegram.tokenConfigured !== 'boolean' || 'botToken' in telegram || 'token' in telegram) throw new Error('Invalid Telegram settings.');
    optional(telegram, ['chatId', 'credentialId']);
  }
  if (item.capabilities != null) { if (!Array.isArray(item.capabilities)) throw new Error('Invalid Worker capabilities.'); item.capabilities.forEach(value => { optional(fields(value, ['type', 'name']), ['scope', 'version', 'checkedAtUtc']); }); }
  if (item.hostResources != null) {
    const resources = fields(item.hostResources, ['measuredAtUtc']);
    optional(resources, [], [], ['logicalCpuCount', 'totalMemoryBytes', 'usedMemoryBytes', 'diskTotalBytes', 'diskAvailableBytes', 'cpuUsagePercent', 'memoryUsagePercent', 'sampleSeconds']);
    if (!Number.isFinite(Date.parse(resources.measuredAtUtc as string)) || Object.entries(resources).some(([key, value]) => typeof value === 'number' && (value < 0 || (key.endsWith('Percent') && value > 100)))) throw new Error('Invalid host resources.');
  }
  if (item.activeProjects != null) stringList(item.activeProjects);
}
function fields(value: unknown, strings: string[], booleans: string[] = []) {
  const item = record(value);
  if (strings.some(key => typeof item[key] !== 'string') || booleans.some(key => typeof item[key] !== 'boolean')) throw new Error('Invalid fields.');
  return item;
}
function list<T>(validate: (value: unknown) => void): Validator<T[]> {
  return value => {
    if (!Array.isArray(value) || value.length > 10000) throw new Error('Invalid list.');
    value.forEach(validate); return value as T[];
  };
}
export const workers = list<WorkerObservation>(value => { validateWorker(value); });
export const projects = list<ProjectSummary>(value => { const item = fields(value, ['id', 'name', 'repository']); optional(item, [], ['enabled'], ['revision']);
  if (item.requirements != null) { if (!Array.isArray(item.requirements)) throw new Error('Invalid requirements.'); item.requirements.forEach(value => { optional(fields(value, ['type', 'name']), ['version']); }); } });
export const executions = list<ExecutionSummary>(value => {
  const item = fields(value, ['id', 'projectId', 'state', 'createdAtUtc']);
  optional(item, ['assignedWorkerId', 'currentStage', 'startedAtUtc', 'assignedAtUtc', 'completedAtUtc', 'recoveryState', 'recoveryReason', 'pendingReason', 'managedEligibilityState', 'managedEligibilityCheckedAtUtc', 'completionSummary',
    'validationResult', 'integrationResult', 'failureClassification', 'retryOfExecutionId', 'workerExecutionId', 'workspaceRecovery', 'archivedAtUtc'], ['recoverable'], ['durationMilliseconds', 'attemptNumber']);
  if (item.stageStartedAtUtc != null) {
    const stages = record(item.stageStartedAtUtc);
    const allowed = new Set(['claimed', 'preparing', 'codex', 'validation', 'integration', 'reporting']);
    if (Object.entries(stages).some(([stage, timestamp]) => !allowed.has(stage) || typeof timestamp !== 'string' || !Number.isFinite(Date.parse(timestamp)))) throw new Error('Invalid stage timestamps.');
  }
  if (item.managedEligibilityReasons != null && (!Array.isArray(item.managedEligibilityReasons) || item.managedEligibilityReasons.some(reason => typeof reason !== 'string'))) throw new Error('Invalid eligibility reasons.');
  if (item.workReference != null) {
    const work = fields(item.workReference, ['type', 'id']);
    optional(work, ['url', 'title']);
    if (work.selection != null) {
      const selection = fields(work.selection, ['engine']);
      optional(selection, ['model', 'effort']);
      if (selection.engine !== 'codex' && selection.engine !== 'ccc') throw new Error('Invalid execution engine.');
    }
  }
});
export const nodes = list<NodeSummary>(value => {
  const item = fields(value, ['id', 'kind', 'connectivity', 'executionReadiness'], ['observationsStale']);
  optional(item, ['provisioningReadiness', 'displayName', 'health']);
  if (!Array.isArray(item.capabilities)) throw new Error('Invalid capabilities.');
  for (const capability of item.capabilities) {
    const entry = record(capability);
    fields(entry.definition, ['id', 'displayName'], ['requiresAuthentication', 'requiresConfiguration']);
    const state = fields(entry.state, ['installation', 'health']);
    optional(state, ['authentication', 'configuration', 'update', 'detectedVersion', 'detectedAtUtc', 'diagnosticCode']);
    if (state.operation != null) optional(fields(state.operation, ['state']), ['action', 'diagnosticCode']);
    if (!Array.isArray(entry.availableActions) || entry.availableActions.some(value => typeof value !== 'string')) throw new Error('Invalid actions.');
  }
});
export const diagnostics: Validator<WorkerDiagnostics> = value => {
  const item = fields(value, ['configurationSynchronization', 'provisioningState'], ['aiAgentReady', 'gitHubReady', 'gitReady']);
  optional(item, ['workerVersion', 'recentOperationalError'], ['canActivate', 'capabilityObservationsCurrent'], ['configurationRevision']);
  if (item.configurationRevision != null && (!Number.isSafeInteger(item.configurationRevision) || Number(item.configurationRevision) < 1)) throw new Error('Invalid diagnostics configuration revision.');
  if (item.projects != null) {
    if (!Array.isArray(item.projects)) throw new Error('Invalid project readiness.');
    item.projects.forEach(value => {
      const project = fields(value, ['projectId', 'projectName', 'observationStatus'], ['isEligible']);
      optional(project, ['materializationState', 'diagnosticCode'], [], ['workerReportedRevision']);
      if (project.workerReportedRevision != null && (!Number.isSafeInteger(project.workerReportedRevision) || Number(project.workerReportedRevision) < 1)) throw new Error('Invalid project revision.');
      stringList(project.missingRequirements);
    });
  }
  if (item.reasons != null) stringList(item.reasons);
  if (item.activationBlockingReasons != null && (!Array.isArray(item.activationBlockingReasons) || item.activationBlockingReasons.some(value => typeof value !== 'string'))) throw new Error('Invalid reasons.');
  if (item.workerReportedManagedDiagnostics != null) {
    const managed = fields(item.workerReportedManagedDiagnostics, ['retrieval', 'synchronization', 'source']);
    optional(managed, ['failureStage', 'diagnosticCode']);
    if (!['unverified', 'retrieved', 'unavailable', 'unauthorized'].includes(String(managed.retrieval)) ||
        !['not-synchronized', 'synchronized', 'cached', 'unavailable', 'error'].includes(String(managed.synchronization)) ||
        !['none', 'server-retrieved', 'cached'].includes(String(managed.source)) ||
        (managed.failureStage != null && !['retrieval', 'contract-validation', 'synchronization'].includes(String(managed.failureStage))) ||
        (managed.diagnosticCode != null && !['server-unavailable', 'server-unauthorized', 'managed-contract-invalid',
          'managed-local-configuration-invalid', 'managed-synchronization-failed', 'project-capabilities-missing',
          'project-preparation-failed'].includes(String(managed.diagnosticCode)))) throw new Error('Invalid managed diagnostics.');
  }
  if (item.latestProvisioningOperation != null) optional(record(item.latestProvisioningOperation), ['action', 'status']);
  return value as WorkerDiagnostics;
};
export function sessionDocument(value: unknown) {
  const item = fields(value, ['csrfToken', 'expiresAtUtc']);
  const csrfToken = String(item.csrfToken), expires = Date.parse(String(item.expiresAtUtc));
  if (!csrfToken || !Number.isFinite(expires) || expires <= Date.now()) throw new Error('Invalid session.');
  return { csrfToken, expires };
}
export const worker: Validator<import('./contracts').WorkerAdministration> = value => {
  validateWorker(value); return value as import('./contracts').WorkerAdministration;
};
// Server commands project safe metadata only; Worker commands retain validated
// short-lived device instructions for their node-scoped preparation view.
export const command: Validator<import('./contracts').NodeCommandSummary> = value => {
  const item = fields(value, ['id', 'createdAtUtc', 'status']);
  const request = fields(item.request, ['nodeId', 'capabilityId', 'action']);
  // claudeLogin is an object validated below, not a string.
  optional(item, ['diagnostic', 'startedAtUtc', 'deadlineUtc', 'completedAtUtc']);
  if (item.loginInstructions != null) fields(item.loginInstructions, ['verificationUri', 'userCode']);
  let failureDetail;
  if (item.failureDetail != null) {
    // code is a string enum name; processExitCode and providerStep are optional context.
    const failure = fields(item.failureDetail, ['description']);
    if (failure.code != null && typeof failure.code !== 'string' || String(failure.description).length > 2048) throw Error('Invalid failure detail.');
    failureDetail = { code: typeof failure.code === 'string' ? failure.code : undefined, description: String(failure.description) };
  }
  let claudeLogin;
  if (item.claudeLogin != null) {
    const progress = fields(item.claudeLogin, ['provider', 'state', 'instructionsUri'], ['requiresBrowserAction', 'requiresLocalCliCodeInput']);
    if (progress.provider !== 'anthropic' || progress.state !== 'manual-vendor-cli-completion-required' ||
        progress.instructionsUri !== 'https://code.claude.com/docs/en/troubleshoot-install' ||
        progress.requiresBrowserAction !== true || progress.requiresLocalCliCodeInput !== true ||
        request.nodeId === 'server' || request.capabilityId !== 'claude-cli' || request.action !== 'Login' || item.status !== 'Running')
      throw Error('Invalid Claude login progress.');
    claudeLogin = { provider: 'anthropic', state: 'manual-vendor-cli-completion-required',
      instructionsUri: 'https://code.claude.com/docs/en/troubleshoot-install', requiresBrowserAction: true, requiresLocalCliCodeInput: true };
  }
  let publicIdentity;
  if (item.publicIdentity != null) {
    const identity = fields(item.publicIdentity, ['publicKey', 'fingerprint']);
    publicIdentity = { publicKey: String(identity.publicKey), fingerprint: String(identity.fingerprint) };
  }
  if (request.nodeId !== 'server') return value as import('./contracts').NodeCommandSummary;
  return { id: String(item.id), createdAtUtc: String(item.createdAtUtc), status: String(item.status),
    request: { nodeId: String(request.nodeId), capabilityId: String(request.capabilityId), action: String(request.action) },
    diagnostic: typeof item.diagnostic === 'string' ? item.diagnostic : undefined,
    startedAtUtc: typeof item.startedAtUtc === 'string' ? item.startedAtUtc : undefined,
    deadlineUtc: typeof item.deadlineUtc === 'string' ? item.deadlineUtc : undefined,
    completedAtUtc: typeof item.completedAtUtc === 'string' ? item.completedAtUtc : undefined, publicIdentity, failureDetail, claudeLogin };
};
export const commands: Validator<import('./contracts').NodeCommandSummary[]> = value => {
  if (!Array.isArray(value) || value.length > 10000) throw Error('Invalid commands.');
  return value.map(command);
};
export const serverGitHubConnection: Validator<import('./contracts').ServerGitHubConnection> = value => {
  const item = fields(value, [], ['provisioningEnabled', 'elevationAllowed']);
  return { commands: commands(item.commands), provisioningEnabled: Boolean(item.provisioningEnabled), elevationAllowed: Boolean(item.elevationAllowed) };
};
export const serverStatus: Validator<import('./contracts').ServerStatus> = value => {
  const item = fields(value, ['state', 'startedAtUtc']);
  optional(item, ['version'], ['persistenceAvailable']);
  if (item.freshness != null) {
    const markers = fields(item.freshness, []);
    for (const key of ['projects', 'workers', 'executions'])
      if (!Number.isSafeInteger(markers[key]) || Number(markers[key]) < 0) throw new Error('Invalid freshness marker.');
  }
  return value as import('./contracts').ServerStatus;
};

function stringList(value: unknown) { if (!Array.isArray(value) || value.some(item => typeof item !== 'string')) throw new Error('Invalid string list.'); }
export const deliveryAuthorization: Validator<import('./contracts').DeliveryAuthorization> = value => {
  optional(fields(value, ['status']), ['revokedAtUtc']); return value as import('./contracts').DeliveryAuthorization;
};
export const pairingAuthorization: Validator<import('./contracts').PairingAuthorization> = value => {
  const item = fields(value, ['authorization']);
  if (!item.authorization || typeof item.lifetimeSeconds !== 'number' || !Number.isInteger(item.lifetimeSeconds) || item.lifetimeSeconds < 1 || item.lifetimeSeconds > 900) throw new Error('Invalid authorization lifetime.');
  return value as import('./contracts').PairingAuthorization;
};
export const version: Validator<{ version: string }> = value => { fields(value, ['version']); return value as { version: string }; };
export const provisioningPlans = list<import('./contracts').ProvisioningPlanSummary>(value => {
  const item = fields(value, ['id', 'workerId', 'createdAtUtc', 'state']); optional(item, ['currentActionId']);
  if (!Array.isArray(item.actions)) throw new Error('Invalid plan actions.');
  item.actions.forEach(value => { optional(fields(value, ['type', 'name']), ['version']); });
});

// Single-resource reads reuse the list validators so one contract stays authoritative.
const single = <T>(validate: Validator<T[]>): Validator<T> => value => validate([value])[0];
export const project = single(projects);
export const execution = single(executions);
const validateIssue = (value: unknown) => {
  const item = fields(value, ['title', 'state', 'url', 'createdAtUtc', 'updatedAtUtc'], ['isEligible']);
  if (!Number.isSafeInteger(item.number) || Number(item.number) < 1) throw new Error('Invalid Issue number.');
  optional(item, ['body', 'author']);
  stringList(item.labels); stringList(item.eligibilityReasons);
  if (!Array.isArray(item.blockedBy)) throw new Error('Invalid blockers.');
  item.blockedBy.forEach(blocker => { const entry = record(blocker); if (!Number.isSafeInteger(entry.number)) throw new Error('Invalid blocker.'); optional(entry, ['state', 'title']); });
};
export const issues = list<GitHubIssue>(validateIssue);
export const issue: Validator<GitHubIssue> = value => { validateIssue(value); return value as GitHubIssue; };
export const credentials = list<Credential>(value => { optional(fields(value, ['id', 'provider', 'type', 'status']), ['assignedWorkerId', 'revokedAtUtc']); });
/** Mutation responses are re-read from their collections; only require a JSON object or no content. */
export const archiveResult: Validator<ExecutionArchiveResult> = value => {
  const item = record(value);
  if (!Array.isArray(item.changed) || item.changed.some(id => typeof id !== 'string') || !Array.isArray(item.refused)) throw new Error('Invalid archive result.');
  item.refused.forEach(refusal => fields(refusal, ['id', 'reason']));
  return item as unknown as ExecutionArchiveResult;
};
export const accepted: Validator<unknown> = value => { if (value !== null) record(value); return value; };

export const workerRelease: Validator<WorkerRelease> = value => {
  const item = fields(value, [], ['updateAvailable', 'selfUpdateSupported', 'restartSupported']);
  optional(item, ['currentVersion', 'latestVersion', 'checkedAtUtc', 'diagnostic']);
  return item as unknown as WorkerRelease;
};
/** A Worker restart/update operation, or null when none was ever requested. */
export const workerUpdate: Validator<WorkerUpdate | null> = value => {
  if (value === null) return null;
  const item = fields(value, ['operationId', 'workerId', 'state']);
  if (!/^[a-f0-9]{32}$/.test(String(item.operationId)) || !['waiting-for-idle', 'updating', 'reconnecting', 'succeeded', 'failed'].includes(String(item.state)))
    throw new Error('Invalid Worker update.');
  optional(item, ['requestedAtUtc', 'diagnostic', 'drainReleasedAtUtc', 'expectedVersion', 'originalVersion'], ['restart']);
  if (item.recovery != null) {
    const recovery = fields(item.recovery, ['checkedAtUtc'], ['schedulingBlocked', 'eligible']);
    optional(recovery, ['reportedVersion'], ['retryEligible', 'releaseDrainEligible']);
    if (!Array.isArray(recovery.checks) || recovery.checks.length > 20) throw new Error('Invalid Worker recovery status.');
    recovery.checks.forEach(check => optional(fields(check, ['code', 'description', 'nextStep'], ['satisfied']), ['detail']));
  }
  return item as unknown as WorkerUpdate;
};

// Branch names stay inside the archive prefixes Workers use and are valid Git refs; anything else is refused before display.
const archivedBranchName = (name: unknown) => typeof name === 'string' && name.length <= 250 && /^(done\/feature|complete\/feature|completed)\/.+/.test(name) &&
  !/[\s\x00-\x1f\x7f~^:?*[\\%#&]/.test(name) && !name.includes('..') && !name.includes('@{') && !name.includes('//') && !/[/.]$/.test(name) &&
  name.split('/').every(part => !part.startsWith('.') && !part.endsWith('.lock'));
const commitId = (value: unknown) => typeof value === 'string' && /^[a-f0-9]{40}([a-f0-9]{24})?$/i.test(value);
const reasonCode = (value: unknown) => typeof value === 'string' && /^[a-z0-9-]{1,60}$/.test(value);

/** One page of the Server's archived branch inventory for the project and age this page asked about. */
export const archivedBranchPage = (projectId: string, olderThanDays: number, offset: number): Validator<ArchivedBranchPage> => value => {
  const page = fields(value, ['projectId', 'repository', 'baseBranch']);
  if (page.projectId !== projectId || page.olderThanDays !== olderThanDays || page.offset !== offset || !Number.isSafeInteger(page.total)
    || Number(page.total) < 0 || !Array.isArray(page.branches) || page.branches.length > 100 || offset + page.branches.length > Number(page.total)) throw new Error('Invalid branch inventory.');
  const names = new Set<string>();
  page.branches.forEach(value => {
    const row = fields(value, ['branch', 'tip', 'decision', 'reason']);
    if (names.has(String(row.branch)) || !archivedBranchName(row.branch) || !commitId(row.tip) || !reasonCode(row.reason)
      || !['eligible', 'recent', 'not-integrated', 'unknown'].includes(String(row.decision))
      || (row.committedAtUtc != null && !Number.isFinite(Date.parse(String(row.committedAtUtc))))
      || (['eligible', 'recent'].includes(String(row.decision)) && row.committedAtUtc == null)) throw new Error('Invalid branch inventory.');
    names.add(String(row.branch));
  });
  return page as unknown as ArchivedBranchPage;
};

/** A deletion result must answer exactly the branches this page selected, in order. */
export const archivedBranchDeletion = (projectId: string, selected: BranchSelection[]): Validator<ArchivedBranchDeleteResult> => value => {
  const result = fields(value, ['projectId', 'repository']);
  if (result.projectId !== projectId || !Array.isArray(result.branches) || result.branches.length !== selected.length) throw new Error('Unexpected branch deletion result.');
  result.branches.forEach((value, index) => {
    const row = fields(value, ['branch', 'outcome', 'reason']);
    if (row.branch !== selected[index].branch || !reasonCode(row.reason)
      || !['deleted', 'missing', 'changed', 'retained', 'failed', 'not-attempted'].includes(String(row.outcome))) throw new Error('Unexpected branch deletion result.');
  });
  return result as unknown as ArchivedBranchDeleteResult;
};
