import test from 'node:test';
import assert from 'node:assert/strict';
import { branchOperation, selectable, selections } from '../src/features/projects/archived-branches.mjs';
const eligible = { branch: 'done/feature/task-1', tip: 'a'.repeat(40), decision: 'eligible', reason: 'integrated' };
const request = { operationId: 'op', workerId: 'worker', project: 'one', repository: 'owner/one', action: 'archived-branches', apply: false, olderThanDays: 30, offset: 0 };
test('selection uses eligible archive refs and observed tips only', () => {
  const rows = [eligible, { ...eligible, branch: 'feature/active' }, { ...eligible, branch: 'unrelated' }, { ...eligible, branch: 'complete/feature/kept', decision: 'review' }];
  assert.deepEqual(selections(rows, rows.map(row => row.branch)), [{ branch: eligible.branch, expectedTip: eligible.tip }]);
  assert.equal(selectable({ ...eligible, tip: null }), false);
  assert.equal(selectable({ ...eligible, branch: 'complete/feature/task' }), true);
});
test('project, worker, request identity and page changes reject stale results', () => {
  const operation = { request, status: 'succeeded', report: { outcome: 'succeeded', branches: [eligible] } };
  assert.equal(branchOperation({ operation }, request), operation);
  for (const key of ['project', 'repository', 'workerId', 'operationId', 'offset', 'olderThanDays', 'apply']) {
    assert.throws(() => branchOperation(operation, { ...request, [key]: 'changed' }));
  }
});
test('partial results preserve retained and failed reasons; invalid responses fail closed', () => {
  const branches = [{ ...eligible, decision: 'deleted' }, { ...eligible, branch: 'complete/feature/task-2', decision: 'review', reason: 'remote-tip-changed' }];
  assert.deepEqual(branchOperation({ request, status: 'failed', report: { branches } }, request).report.branches, branches);
  for (const value of [null, {}, { request, status: 'succeeded', report: { branches: [{}] } }]) assert.throws(() => branchOperation(value, request));
});

test('inventory rejects unrelated, unsafe, duplicate and unknown branch results', () => {
  for (const branch of ['feature/active', 'unrelated', 'done/feature/../main', 'complete/feature/foo.lock']) {
    assert.throws(() => branchOperation({ request, status: 'succeeded', report: { branches: [{ ...eligible, branch }] } }, request));
  }
  for (const branches of [[eligible, eligible], [{ ...eligible, decision: 'unknown' }]]) {
    assert.throws(() => branchOperation({ request, status: 'succeeded', report: { branches } }, request));
  }
});
test('deletion reports stay within the confirmed branch selection', () => {
  const deletion = { ...request, apply: true, branches: [{ branch: eligible.branch, expectedTip: eligible.tip }] };
  const operation = { request: deletion, status: 'succeeded', report: { branches: [{ ...eligible, decision: 'deleted' }] } };
  assert.equal(branchOperation(operation, deletion), operation);
  assert.throws(() => branchOperation({ ...operation, request: { ...deletion, branches: [] } }, deletion));
  assert.throws(() => branchOperation({ ...operation, report: { branches: [{ ...eligible, branch: 'complete/feature/other' }] } }, deletion));
});

test('Server target projection fails closed on changed project binding and inconsistent eligibility', async () => {
  const { readBranchTargets, requireBranchTarget } = await import('../src/features/projects/archived-branches.mjs');
  const project = { id: 'one', revision: 1, name: 'one', repository: 'owner/one' };
  const target = { workerId: 'obs_' + 'a'.repeat(32), displayName: 'Standalone', mode: 'standalone-observer', eligible: true, reason: null, canApply: false, applyReason: 'worker-drain-required' };
  const value = { projectId: project.id, revision: 1, project: project.name, repository: project.repository, targets: [target] };
  assert.equal(readBranchTargets(value, project), value);
  const scope = { ...request, workerId: target.workerId, projectId: project.id, projectRevision: project.revision };
  assert.equal(requireBranchTarget(value, scope), target);
  assert.throws(() => requireBranchTarget(value, { ...scope, apply: true }), /worker-drain-required/);
  assert.throws(() => requireBranchTarget(value, { ...scope, workerId: 'b'.repeat(32) }), /branch-target-changed/);
  for (const changed of [{ ...value, repository: 'owner/other' }, { ...value, revision: 2 }, { ...value, targets: [{ ...target, eligible: true, reason: 'unauthorized' }] }, { ...value, targets: [target, target] }])
    assert.throws(() => readBranchTargets(changed, project));
});
