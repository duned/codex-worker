import type { WorkerObservation, ProjectSummary, ExecutionSummary, NodeSummary, WorkerDiagnostics } from './contracts';
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
  optional(item, ['displayName', 'lifecycleState', 'schedulingPolicy', 'lastHeartbeatAtUtc', 'authenticationCredentialStatus', 'authenticationCredentialRevokedAtUtc'], [],
    ['capacity', 'maximumCapacity', 'activeExecutions', 'availableCapacity', 'activeAssignments']);
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
export const projects = list<ProjectSummary>(value => { fields(value, ['id', 'name', 'repository']); });
export const executions = list<ExecutionSummary>(value => {
  const item = fields(value, ['id', 'projectId', 'state', 'createdAtUtc']);
  optional(item, ['assignedWorkerId', 'currentStage', 'startedAtUtc', 'assignedAtUtc', 'completedAtUtc', 'recoveryState', 'recoveryReason', 'pendingReason', 'managedEligibilityState', 'managedEligibilityCheckedAtUtc', 'completionSummary'], [], ['durationMilliseconds']);
  if (item.managedEligibilityReasons != null && (!Array.isArray(item.managedEligibilityReasons) || item.managedEligibilityReasons.some(reason => typeof reason !== 'string'))) throw new Error('Invalid eligibility reasons.');
  if (item.workReference != null) optional(fields(item.workReference, ['type', 'id']), ['url']);
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
  optional(item, [], ['canActivate']);
  if (item.activationBlockingReasons != null && (!Array.isArray(item.activationBlockingReasons) || item.activationBlockingReasons.some(value => typeof value !== 'string'))) throw new Error('Invalid reasons.');
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
// Project only safe metadata: device instructions must never enter the query cache.
export const command: Validator<import('./contracts').NodeCommandSummary> = value => {
  const item = fields(value, ['id', 'createdAtUtc', 'status']);
  const request = fields(item.request, ['nodeId', 'capabilityId', 'action']);
  optional(item, ['diagnostic', 'startedAtUtc', 'deadlineUtc', 'completedAtUtc']);
  let failureDetail;
  if (item.failureDetail != null) {
    const failure = fields(item.failureDetail, ['description']);
    if (String(failure.description).length > 2048) throw Error('Invalid failure detail.');
    failureDetail = { description: String(failure.description) };
  }
  let publicIdentity;
  if (item.publicIdentity != null) {
    const identity = fields(item.publicIdentity, ['publicKey', 'fingerprint']);
    publicIdentity = { publicKey: String(identity.publicKey), fingerprint: String(identity.fingerprint) };
  }
  return { id: String(item.id), createdAtUtc: String(item.createdAtUtc), status: String(item.status),
    request: { nodeId: String(request.nodeId), capabilityId: String(request.capabilityId), action: String(request.action) },
    diagnostic: typeof item.diagnostic === 'string' ? item.diagnostic : undefined,
    startedAtUtc: typeof item.startedAtUtc === 'string' ? item.startedAtUtc : undefined,
    deadlineUtc: typeof item.deadlineUtc === 'string' ? item.deadlineUtc : undefined,
    completedAtUtc: typeof item.completedAtUtc === 'string' ? item.completedAtUtc : undefined, publicIdentity, failureDetail };
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
  fields(value, ['state', 'version', 'startedAtUtc']);
  return value as import('./contracts').ServerStatus;
};
