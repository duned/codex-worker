import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import { build } from 'esbuild';

const bundle = await build({
  stdin: { contents: `export { conflictPreview } from './src/features/executions/ManagedConflictRecovery';`, resolveDir: new URL('..', import.meta.url).pathname },
  bundle: true, platform: 'node', format: 'cjs', write: false,
});
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { conflictPreview } = module.exports;
const preview = { executionId: 'a'.repeat(32), workerId: 'b'.repeat(32), eligible: true, reason: 'Eligible', candidate: {
  projectId: 'project', serverExecutionId: 'a'.repeat(32), workerExecutionId: 'c'.repeat(32), integrationBase: 'd'.repeat(40),
  kind: 'Integration', automaticRetryExhausted: true, preservedCommit: 'e'.repeat(40), refusalReason: null,
} };

test('exhausted-base recovery preview retains exact evidence required by explicit request', () => {
  assert.deepEqual(conflictPreview(preview), preview);
  for (const patch of [{ preservedCommit: null }, { automaticRetryExhausted: 'true' }, { kind: 'Codex' }, { workerExecutionId: 42 }])
    assert.throws(() => conflictPreview({ ...preview, candidate: { ...preview.candidate, ...patch } }));
  assert.throws(() => conflictPreview({ ...preview, workerId: null }));
  assert.throws(() => conflictPreview({ ...preview, candidate: null }));
});

test('refusals can report unavailable identity and commit without authorizing a request', () => {
  const refusal = { executionId: preview.executionId, eligible: false, reason: 'Original Worker is offline.', workerId: null, candidate: null };
  assert.deepEqual(conflictPreview(refusal), refusal);
  assert.equal(conflictPreview({ ...preview, eligible: false, candidate: { ...preview.candidate, preservedCommit: null } }).eligible, false);
});

test('specific intent diagnostic and operator step survive the dashboard preview contract', () => {
  const reason = 'issue-parent-changed: Review the parent and retained history before recovery.';
  const refused = { ...preview, eligible: false, reasonCode: 'issue-parent-changed', reason,
    candidate: { ...preview.candidate, refusalCode: 'issue-parent-changed', refusalReason: reason } };
  assert.deepEqual(conflictPreview(refused), refused);
  assert.throws(() => conflictPreview({ ...refused, reasonCode: 42 }));
  assert.throws(() => conflictPreview({ ...refused, candidate: { ...refused.candidate, refusalCode: 42 } }));
});
