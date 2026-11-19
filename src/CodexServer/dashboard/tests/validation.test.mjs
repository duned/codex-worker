import test from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';

const { outputFiles } = await build({ entryPoints: [new URL('../src/shared/api/validation.ts', import.meta.url).pathname], bundle: true, write: false, format: 'esm', platform: 'node' });
const validation = await import('data:text/javascript;base64,' + Buffer.from(outputFiles[0].text).toString('base64'));

test('node command history accepts a Worker Claude sign-in in progress and rejects a forged one', () => {
  const claudeLogin = { provider: 'anthropic', state: 'manual-vendor-cli-completion-required', instructionsUri: 'https://code.claude.com/docs/en/troubleshoot-install',
    requiresBrowserAction: true, requiresLocalCliCodeInput: true };
  const login = { id: 'a', createdAtUtc: '2026-11-18T10:00:00Z', status: 'Running', diagnostic: 'Executing',
    request: { nodeId: 'n', capabilityId: 'claude-cli', action: 'Login', timeoutSeconds: 600, allowElevation: false }, claudeLogin };
  const github = { id: 'b', createdAtUtc: '2026-11-18T10:00:00Z', status: 'Succeeded', diagnostic: 'Completed',
    request: { nodeId: 'n', capabilityId: 'github-cli', action: 'Login' } };
  assert.equal(validation.commands([login, github]).length, 2);
  assert.throws(() => validation.commands([{ ...login, claudeLogin: { ...claudeLogin, instructionsUri: 'https://evil.example' } }]));
});

test('node command history accepts failure details as the Server serializes them', () => {
  const failed = { id: 'c', createdAtUtc: '2026-11-18T10:00:00Z', status: 'Failed', diagnostic: 'ProcessFailed',
    request: { nodeId: 'n', capabilityId: 'github-cli', action: 'Login', timeoutSeconds: 600, allowElevation: false, repository: null },
    publicIdentity: null, loginInstructions: null,
    failureDetail: { code: 'ProcessExited', processExitCode: 1, providerStep: 'Unknown', description: 'The tool process exited with an error.' } };
  const running = { ...failed, id: 'd', status: 'Running', diagnostic: 'Executing', failureDetail: null,
    loginInstructions: { verificationUri: 'https://github.com/login/device', userCode: 'ABCD-1234' } };
  const parsed = validation.commands([failed, running]);
  assert.equal(parsed[0].failureDetail.code, 'ProcessExited');
  assert.equal(parsed[1].loginInstructions.userCode, 'ABCD-1234');
  assert.throws(() => validation.commands([{ ...failed, failureDetail: { code: 3, description: 'x' } }]));
});

const runtimeBundle = await build({ entryPoints: [new URL('../src/shared/api/runtime.ts', import.meta.url).pathname], bundle: true, write: false, format: 'esm', platform: 'node' });
const runtime = await import('data:text/javascript;base64,' + Buffer.from(runtimeBundle.outputFiles[0].text).toString('base64'));
test('Server markers refresh on-screen collection and detail reads, never GitHub-backed ones', () => {
  assert.equal(runtime.markerRefreshed('/api/v1/executions?limit=100&offset=0'), true);
  assert.equal(runtime.markerRefreshed('/api/v1/executions/abc'), true);
  assert.equal(runtime.markerRefreshed('/api/v1/workers/w1/diagnostics'), true);
  assert.equal(runtime.markerRefreshed('/api/v1/projects/p/github/issues'), false);
  assert.equal(runtime.markerRefreshed('/api/v1/nodes'), false);
});

test('archived branch pages must answer the project, age and offset this page asked for', () => {
  const row = { branch: 'done/feature/1-x', tip: 'c'.repeat(40), committedAtUtc: '2026-09-30T08:45:43Z', decision: 'eligible', reason: 'integrated' };
  const page = { projectId: 'p', repository: 'o/r', baseBranch: 'main', olderThanDays: 30, offset: 0, total: 1, branches: [row] };
  const validate = validation.archivedBranchPage('p', 30, 0);
  assert.equal(validate(page).branches.length, 1);
  assert.equal(validate({ ...page, branches: [{ ...row, branch: 'completed/2-y', decision: 'not-integrated', reason: 'not-in-base', committedAtUtc: null }] }).branches.length, 1);
  assert.throws(() => validate({ ...page, olderThanDays: 7 }));
  assert.throws(() => validate({ ...page, projectId: 'other' }));
  assert.throws(() => validate({ ...page, total: 0 }));
  assert.throws(() => validate({ ...page, branches: [{ ...row, branch: 'main' }] }));
  assert.throws(() => validate({ ...page, branches: [{ ...row, tip: 'xyz' }] }));
  assert.throws(() => validate({ ...page, branches: [{ ...row, committedAtUtc: null }] }));
  assert.throws(() => validate({ ...page, branches: [row, row], total: 2 }));
});

test('archived branch deletions must answer exactly the selected branches', () => {
  const selected = [{ branch: 'done/feature/1-x', expectedTip: 'c'.repeat(40) }];
  const result = { projectId: 'p', repository: 'o/r', branches: [{ branch: 'done/feature/1-x', outcome: 'deleted', reason: 'deleted' }] };
  const validate = validation.archivedBranchDeletion('p', selected);
  assert.equal(validate(result).branches[0].outcome, 'deleted');
  assert.throws(() => validate({ ...result, branches: [] }));
  assert.throws(() => validate({ ...result, branches: [{ branch: 'done/feature/2-y', outcome: 'deleted', reason: 'deleted' }] }));
  assert.throws(() => validate({ ...result, branches: [{ branch: 'done/feature/1-x', outcome: 'gone', reason: 'deleted' }] }));
});
