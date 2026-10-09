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
  optional(item, ['workerVersion', 'displayName', 'lifecycleState', 'schedulingPolicy', 'lastHeartbeatAtUtc', 'authenticationCredentialStatus', 'authenticationCredentialRevokedAtUtc', 'platform', 'firstRegisteredAtUtc'], [],
    ['capacity', 'maximumCapacity', 'activeExecutions', 'availableCapacity', 'activeAssignments']);
  if (item.capabilities != null) { if (!Array.isArray(item.capabilities)) throw new Error('Invalid Worker capabilities.'); item.capabilities.forEach(value => { optional(fields(value, ['type', 'name']), ['scope', 'version']); }); }
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
  optional(item, ['workerVersion', 'recentOperationalError'], ['canActivate', 'capabilityObservationsCurrent']);
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
  optional(item, ['diagnostic', 'startedAtUtc', 'deadlineUtc', 'completedAtUtc']);
  if (item.loginInstructions != null) fields(item.loginInstructions, ['verificationUri', 'userCode']);
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
  if (request.nodeId !== 'server') return value as import('./contracts').NodeCommandSummary;
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
