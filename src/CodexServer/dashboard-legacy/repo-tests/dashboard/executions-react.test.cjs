const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'executions-react-'));
buildSync({ entryPoints: [resolve('src/CodexServer/worker-poc/src/features/executions/model.ts')], bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'model.cjs') });
const { execution, executionList, reconciliation, cancellationResult, reconciliationResult, query, offset, canReconcile, presentation, validEvidence,
  attentionAssessment, maintenanceScope, maintenanceInventoryScope, archiveApplyAllowed, maintenanceCommand, maintenanceCommands, maintenanceDetail, lastActivity, timeSince } = require(join(directory, 'model.cjs'));
after(() => rmSync(directory, { recursive: true, force: true }));
const queued = { id: 'request', projectId: 'project', state: 'Queued', createdAtUtc: '2026-01-01T00:00:00Z', workReference: { type: 'github-issue', id: '7' }, attemptNumber: 1 };
const uncertain = { ...queued, state: 'Failed', recoveryState: 'LeaseExpiredUncertain', lease: { executionId: 'request', workerId: 'worker', generation: 2, acquiredAtUtc: queued.createdAtUtc, expiresAtUtc: queued.createdAtUtc, state: 'Expired' } };
test('bounded filters preserve API context without inventing totals', () => {
  const url = new URL(query('?project=project&state=Failed&issue=7&offset=100'), 'https://fixture.test');
  assert.deepEqual(Object.fromEntries(url.searchParams), { limit: '50', offset: '100', projectId: 'project', state: 'Failed', workId: '7', workType: 'github-issue' });
  assert.equal(offset('?offset=50000'), 10000);
  assert.equal(offset('?offset=-50'), 0);
  assert.equal(offset('?offset=nope'), 0);
});
test('reported stage and terminal outcome stay distinct from infrastructure health', () => {
  for (const stage of ['Implementation', 'Validation', 'Integration']) assert.equal(presentation({ ...queued, state: 'Running', currentStage: stage }).text, `Running · ${stage}`);
  assert.equal(presentation(queued).text, 'Queued');
  assert.equal(presentation({ ...queued, state: 'Completed', currentStage: 'Integration' }).tone, 'success');
  assert.equal(presentation({ ...queued, state: 'Failed', failureClassification: 'Task' }).text, 'Execution failed');
  assert.equal(presentation({ ...queued, state: 'Cancelled' }).tone, 'gray');
  assert.equal(presentation(uncertain).tone, 'warning');
});
test('reconciliation requires expired uncertain lease and explicit bounded evidence', () => {
  assert.equal(canReconcile(uncertain), true);
  for (const value of [{ ...uncertain, state: 'Running' }, { ...uncertain, lease: undefined }, { ...uncertain, lease: { ...uncertain.lease, state: 'Active' } }, { ...uncertain, recoveryState: 'OperatorRetryQueued' }]) assert.equal(canReconcile(value), false);
  assert.equal(validEvidence('NotIntegrated', 'Verified base branch; commit absent', ''), true);
  assert.equal(validEvidence('Integrated', 'Verified base branch', 'a'.repeat(40)), true);
  for (const [disposition, evidence, commit] of [['Integrated', 'Checked', 'short'], ['NotIntegrated', ' ', ''], ['NotIntegrated', 'x'.repeat(1001), ''], ['NotIntegrated', 'line\nbreak', ''], ['NotIntegrated', 'control\u0085character', ''], ['unknown', 'Checked', '']]) assert.equal(validEvidence(disposition, evidence, commit), false);
});
test('Server-shaped recovery and lineage survive validation, malformed evidence is rejected', () => {
  const value = { ...uncertain, assignmentId: 'assignment', workerExecutionId: 'local', retryOfExecutionId: 'previous', recoverable: true, workspaceRecovery: 'FreshWorkspaceRequired', integrationResult: 'Unknown', missingRequirements: ['git'] };
  assert.deepEqual(execution(value), value);
  assert.deepEqual(executionList([value]), [value]);
  assert.deepEqual(reconciliation({ execution: value, retry: queued }), { execution: value, retry: queued });
  for (const fields of [{ lease: { ...uncertain.lease, generation: '2' } }, { attemptNumber: 0 }, { recoverable: 'true' }, { integrationResult: {} }, { missingRequirements: [{}] }, { workReference: { type: 'github-issue', id: {} } }]) assert.throws(() => execution({ ...value, ...fields }));
});

test('mutation results must establish the exact execution and reconciliation outcome', () => {
  const cancelled = { ...queued, state: 'Cancelled' };
  assert.deepEqual(cancellationResult(queued.id)(cancelled), cancelled);
  assert.throws(() => cancellationResult('another')(cancelled));
  assert.throws(() => cancellationResult(queued.id)(queued));
  const integrated = { ...uncertain, recoveryState: 'OperatorVerifiedIntegrated' };
  assert.deepEqual(reconciliationResult(queued.id, 'Integrated')({ execution: integrated }), { execution: integrated, retry: undefined });
  assert.throws(() => reconciliationResult('another', 'Integrated')({ execution: integrated }));
  assert.throws(() => reconciliationResult(queued.id, 'Integrated')({ execution: uncertain }));
  const source = { ...uncertain, recoveryState: 'OperatorRetryQueued' }, retry = { ...queued, id: 'retry', retryOfExecutionId: queued.id, attemptNumber: 2 };
  assert.deepEqual(reconciliationResult(queued.id, 'NotIntegrated')({ execution: source, retry }), { execution: source, retry });
  assert.throws(() => reconciliationResult(queued.id, 'NotIntegrated')({ execution: source }));
  assert.throws(() => reconciliationResult(queued.id, 'NotIntegrated')({ execution: source, retry: { ...retry, retryOfExecutionId: 'another' } }));
});

const executionId = '11111111111111111111111111111111', workerId = '22222222222222222222222222222222';
const assignmentId = '33333333333333333333333333333333', workerExecutionId = '44444444444444444444444444444444';
const managed = { ...queued, id: executionId, state: 'Running', assignedWorkerId: workerId, assignmentId, workerExecutionId,
  lease: { executionId, workerId, generation: 3, acquiredAtUtc: queued.createdAtUtc, expiresAtUtc: '2026-01-08T00:00:00Z', state: 'Active' } };
const capableWorker = { workerId, availability: 'online', schedulingPolicy: 'Active', activeAssignments: 1,
  capabilities: [{ type: 'protocol', name: 'execution-maintenance-v1' }] };
const currentNode = { id: workerId, kind: 'worker', connectivity: 'connected', executionReadiness: 'ready', observationsStale: false, capabilities: [] };
test('attention view classifies stale lease, offline or stale Worker data, age and missing provenance from bounded Server records', () => {
  const now = Date.parse('2026-01-10T00:00:00Z');
  const current = { ...managed, createdAtUtc: '2026-01-09T00:00:00Z', assignedAtUtc: '2026-01-09T00:00:00Z', startedAtUtc: '2026-01-09T00:00:00Z',
    lease: { ...managed.lease, acquiredAtUtc: '2026-01-09T00:00:00Z', expiresAtUtc: '2026-01-11T00:00:00Z' } };
  const staleLease = { ...current, state: 'Failed', recoveryState: 'LeaseExpiredUncertain', lease: { ...current.lease, state: 'Expired', expiresAtUtc: '2026-01-01T00:00:00Z' } };
  assert.equal(attentionAssessment(staleLease, capableWorker, currentNode, now).classification, 'stale-lease');
  assert.equal(attentionAssessment(current, { ...capableWorker, availability: 'offline' }, currentNode, now).classification, 'worker-offline');
  assert.equal(attentionAssessment(current, capableWorker, { ...currentNode, observationsStale: true }, now).classification, 'worker-data-outdated');
  assert.equal(attentionAssessment(current, undefined, currentNode, now, 'loading').reason, 'attention.workerStateLoading');
  assert.equal(attentionAssessment(current, undefined, currentNode, now, 'unavailable').reason, 'attention.workerStateUnavailable');
  const oldActivity = { ...current, createdAtUtc: '2025-12-31T00:00:00Z', assignedAtUtc: '2025-12-31T00:00:00Z', startedAtUtc: '2025-12-31T00:00:00Z' };
  assert.equal(attentionAssessment(oldActivity, capableWorker, currentNode, now).classification, 'stale');
  assert.equal(attentionAssessment({ ...current, workerExecutionId: undefined }, capableWorker, currentNode, now).classification, 'provenance-uncertain');
  assert.equal(attentionAssessment({ ...current, assignedWorkerId: undefined }, undefined, currentNode, now).classification, 'provenance-uncertain');
  assert.equal(attentionAssessment({ ...current, state: 'Completed', recoverable: false, recoveryState: 'OperatorCleaned' }, undefined, currentNode, now), undefined);
  assert.equal(attentionAssessment(current, capableWorker, currentNode, now), undefined);
  assert.equal(lastActivity({ ...managed, completedAtUtc: '2026-01-04T00:00:00Z' }), '2026-01-04T00:00:00Z');
  assert.equal(timeSince('2026-01-09T22:00:00Z', now), '2h');
});
test('managed maintenance action matrix uses exact provenance, current Worker capability, lease, drain and archive retention', () => {
  const now = Date.parse('2026-02-10T00:00:00Z');
  const terminal = { ...managed, state: 'Completed', completedAtUtc: '2026-01-01T00:00:00Z', recoverable: false,
    lease: { ...managed.lease, state: 'Released' } };
  assert.equal(maintenanceScope(managed, 'inspect', false, capableWorker, currentNode, now).allowed, true);
  assert.equal(maintenanceScope(managed, 'inspect', false, { ...capableWorker, availability: 'offline' }, currentNode, now).reason, 'maintenance.workerOffline');
  assert.equal(maintenanceScope(managed, 'inspect', false, { ...capableWorker, capabilities: [] }, currentNode, now).reason, 'maintenance.protocolUnavailable');
  assert.equal(maintenanceScope(managed, 'inspect', false, capableWorker, { ...currentNode, observationsStale: true }, now).reason, 'maintenance.workerDataOutdated');
  assert.equal(maintenanceScope({ ...managed, assignmentId: undefined }, 'inspect', false, capableWorker, currentNode, now).reason, 'maintenance.missingProvenance');
  assert.equal(maintenanceScope(terminal, 'cleanup', false, capableWorker, currentNode, now).allowed, true);
  assert.equal(maintenanceScope(terminal, 'cleanup', true, capableWorker, currentNode, now).reason, 'maintenance.drainRequired');
  const drained = { ...capableWorker, schedulingPolicy: 'Draining', activeAssignments: 0 };
  assert.equal(maintenanceScope(terminal, 'cleanup', true, drained, currentNode, now).allowed, true);
  assert.equal(maintenanceScope(terminal, 'archive', false, capableWorker, currentNode, now).allowed, true);
  assert.equal(maintenanceScope({ ...terminal, recoverable: true }, 'archive', false, capableWorker, currentNode, now).allowed, true, 'Worker preview resolves current local resource eligibility');
  assert.equal(maintenanceScope(terminal, 'archive', true, drained, currentNode, now).allowed, true);
  assert.equal(maintenanceScope({ ...terminal, completedAtUtc: '2026-02-01T00:00:00Z' }, 'archive', false, capableWorker, currentNode, now).reason, 'maintenance.archiveRetentionRequired');
  assert.equal(maintenanceScope({ ...managed, state: 'Assigned' }, 'retry-report', false, capableWorker, currentNode, now).allowed, true);
  assert.equal(maintenanceScope({ ...managed, state: 'Assigned' }, 'retry-report', true, capableWorker, currentNode, now).reason, 'maintenance.reportRetryUnavailable');
  assert.equal(maintenanceScope({ ...managed, state: 'Assigned' }, 'retry-report', true, { ...capableWorker, schedulingPolicy: 'Draining' }, currentNode, now).allowed, true);
  assert.equal(maintenanceInventoryScope({ ...managed, workerExecutionId: undefined }, capableWorker, currentNode).allowed, true);
  assert.equal(maintenanceInventoryScope({ ...managed, assignedWorkerId: undefined }, capableWorker, currentNode).reason, 'maintenance.missingWorkerProvenance');
});
test('maintenance audit contracts validate exact identities and surface per-record outcomes', () => {
  const command = { request: { operationId: '55555555555555555555555555555555', workerId, serverExecutionId: executionId,
    workerExecutionId, assignmentId, generation: 3, action: 'cleanup', apply: false, timeoutSeconds: 60, limit: 1, offset: 0 },
  status: 'succeeded', createdAtUtc: '2026-01-01T00:00:00Z', authorizedBy: 'server-management:test',
  report: { outcome: 'succeeded', reason: 'integrated', observations: [{ executionId: workerExecutionId, serverExecutionId: executionId,
    assignmentId, generation: 3, state: 'Completed', recoveryState: 'recoverable', reportingStatus: 'none', project: 'project', issueNumber: 7, archived: false }] } };
  const parsedCommand = maintenanceCommand(command);
  assert.equal(parsedCommand.request.operationId, command.request.operationId);
  assert.equal(parsedCommand.report.reason, 'integrated');
  assert.equal(maintenanceCommands([command])[0].request.action, 'cleanup');
  const detail = { operation: command, execution: { ...managed, state: 'Completed' }, workerStatus: 'online', status: 'succeeded',
    observations: [{ observation: command.report.observations[0], execution: { ...managed, state: 'Completed' }, status: 'potentially-recoverable' }] };
  const parsedDetail = maintenanceDetail(detail);
  assert.equal(parsedDetail.operation.request.operationId, command.request.operationId);
  assert.equal(parsedDetail.observations[0].status, 'potentially-recoverable');
  assert.equal(parsedDetail.execution.state, 'Completed');
  const archivePreview = { ...command, request: { ...command.request, action: 'archive', apply: false },
    report: { outcome: 'succeeded', reason: 'already-clean', observations: [{ ...command.report.observations[0], recoveryState: 'operator-cleaned' }] } };
  const archiveDetail = { ...detail, operation: archivePreview,
    observations: [{ observation: archivePreview.report.observations[0], status: 'confirmed' }] };
  assert.equal(archiveApplyAllowed(archiveDetail), true);
  assert.equal(archiveApplyAllowed({ ...archiveDetail, observations: [{ ...archiveDetail.observations[0], observation: { ...archivePreview.report.observations[0], archived: true } }] }), false);
  assert.equal(archiveApplyAllowed({ ...archiveDetail, observations: [{ ...archiveDetail.observations[0], observation: { ...archivePreview.report.observations[0], reportingStatus: 'pending' } }] }), false);
  assert.throws(() => maintenanceCommand({ ...command, request: { ...command.request, action: 'purge' } }));
  assert.throws(() => maintenanceDetail({ ...detail, observations: [{ observation: { ...command.report.observations[0], executionId: 'bad' }, status: 'confirmed' }] }));
  const inventory = maintenanceCommand({ ...command, request: { operationId: '66666666666666666666666666666666', workerId, action: 'inventory', apply: false, timeoutSeconds: 60, limit: 50, offset: 0 }, report: { outcome: 'succeeded', reason: 'inventory-observed', observations: [] } });
  assert.equal(inventory.request.action, 'inventory');
});
