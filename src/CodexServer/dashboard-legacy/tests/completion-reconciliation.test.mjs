import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import { build } from 'esbuild';

const bundle = await build({
  stdin: { contents: `export { maintenanceScope, maintenanceCommand } from './src/features/executions/model';`, resolveDir: new URL('..', import.meta.url).pathname },
  bundle: true, platform: 'node', format: 'cjs', write: false,
});
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { maintenanceScope, maintenanceCommand } = module.exports;
const id = 'a'.repeat(32), workerId = 'b'.repeat(32), localId = 'c'.repeat(32), assignmentId = 'd'.repeat(32);
const now = Date.now();
const worker = { workerId, availability: 'online', schedulingPolicy: 'Draining', activeAssignments: 0,
  capabilities: [{ type: 'protocol', name: 'execution-maintenance-v1' }, { type: 'protocol', name: 'execution-completion-reconciliation-v1' }] };
const node = { id: workerId, observationsStale: false };
const item = { id, assignedWorkerId: workerId, workerExecutionId: localId, assignmentId, state: 'Failed',
  validationResult: 'passed', failureClassification: 'InfrastructureFailure', lease: { executionId: id, workerId, generation: 7, state: 'Released' } };
const command = { request: { operationId: 'e'.repeat(32), workerId, serverExecutionId: id, workerExecutionId: localId, assignmentId,
  generation: 7, action: 'reconcile-completion', apply: false, timeoutSeconds: 60, limit: 1, offset: 0 },
  status: 'succeeded', authorizedBy: 'operator', createdAtUtc: new Date(now).toISOString(), report: {
    outcome: 'succeeded', reason: 'completion-reconciliation-preview', observations: [], completionProof: {
      token: 'F'.repeat(64), commit: 'a'.repeat(40), baseBranch: 'main', validationResult: 'passed', issueOpen: true,
      labels: ['working', 'unrelated'], issueContentChanged: true, validatedIssueFingerprint: 'B'.repeat(64),
      currentIssueFingerprint: 'C'.repeat(64), verifiedAtUtc: new Date(now).toISOString(),
    },
  } };

test('quarantined completion action requires released ownership, passed validation and drained idle Worker', () => {
  assert.equal(maintenanceScope(item, 'reconcile-completion', false, worker, node, now).allowed, true);
  for (const changed of [{ ...item, validationResult: 'failed' }, { ...item, failureClassification: 'TaskFailure' },
    { ...item, state: 'Completed' }, { ...item, lease: { ...item.lease, state: 'Expired' } }, { ...item, workerExecutionId: undefined }]) {
    assert.equal(maintenanceScope(changed, 'reconcile-completion', true, worker, node, now).allowed, false);
  }
  for (const changed of [{ ...worker, schedulingPolicy: 'Enabled' }, { ...worker, activeAssignments: 1 }]) {
    assert.equal(maintenanceScope(item, 'reconcile-completion', true, changed, node, now).allowed, false);
  }
});

test('completion preview retains authoritative evidence and rejects malformed proof', () => {
  const parsed = maintenanceCommand(command);
  assert.deepEqual(parsed.report.completionProof, command.report.completionProof);
  for (const patch of [{ token: '' }, { commit: 'short-sha' }, { issueOpen: 'true' }, { labels: [1] }, { validationResult: 'failed' }]) {
    assert.throws(() => maintenanceCommand({ ...command, report: { ...command.report,
      completionProof: { ...command.report.completionProof, ...patch } } }));
  }
});

test('expired uncertain execution uses standard Worker GUIDs and fresh Worker completion proof', () => {
  const dashed = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
  const expired = { ...item, workerExecutionId: dashed, validationResult: undefined, failureClassification: undefined,
    recoveryState: 'LeaseExpiredUncertain', lease: { ...item.lease, state: 'Expired' } };
  assert.equal(maintenanceScope(expired, 'reconcile-completion', false, worker, node, now).allowed, true);
  assert.equal(maintenanceScope({ ...expired, validationResult: 'failed' }, 'reconcile-completion', false, worker, node, now).allowed, false);
  assert.equal(maintenanceScope(expired, 'cleanup', true, worker, node, now).allowed, false);
  const wire = { ...command, request: { ...command.request, workerExecutionId: dashed }, report: { ...command.report,
    observations: [{ executionId: dashed, serverExecutionId: id, assignmentId, generation: 7, state: 'InfrastructureFailure',
      recoveryState: 'managed-completion-quarantined', reportingStatus: 'pending', project: 'p', issueNumber: 65, archived: false }] } };
  assert.equal(maintenanceCommand(wire).report.observations[0].executionId, dashed);
  assert.equal(maintenanceCommand({ ...wire, request: { ...wire.request, action: 'inventory' } }).status, 'succeeded');
  assert.throws(() => maintenanceCommand({ ...wire, request: { ...wire.request, workerExecutionId: 'not-a-guid' } }));
});
