const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'executions-react-'));
buildSync({ entryPoints: [resolve('src/CodexServer/worker-poc/src/features/executions/model.ts')], bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'model.cjs') });
const { execution, executionList, reconciliation, cancellationResult, reconciliationResult, query, offset, canReconcile, presentation, validEvidence } = require(join(directory, 'model.cjs'));
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
