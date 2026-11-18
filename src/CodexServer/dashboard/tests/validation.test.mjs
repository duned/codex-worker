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

test('branch cleanup targets follow the Server projection and reject inconsistent eligibility', () => {
  const target = { workerId: 'a'.repeat(32), displayName: 'w1', mode: 'managed', eligible: true, reason: null, canApply: false, applyReason: 'worker-drain-required' };
  const targets = { projectId: 'p', revision: 3, project: 'demo', repository: 'o/r', targets: [target] };
  assert.equal(validation.branchTargets(targets).targets.length, 1);
  assert.throws(() => validation.branchTargets({ ...targets, targets: [{ ...target, canApply: true }] }));
  assert.throws(() => validation.branchTargets({ ...targets, targets: [{ ...target, workerId: 'obs_x' }] }));
  assert.throws(() => validation.branchTargets({ ...targets, targets: [target, target] }));
});

test('branch operations must echo the request and list only archived branches that were selected', () => {
  const request = { operationId: 'b'.repeat(32), workerId: 'a'.repeat(32), project: 'demo', repository: 'o/r', projectId: 'p', projectRevision: 3,
    action: 'archived-branches', apply: true, olderThanDays: 30, limit: 100, offset: 0, timeoutSeconds: 300, branches: [{ branch: 'done/feature/1-x', expectedTip: 'c'.repeat(40) }] };
  const operation = { status: 'succeeded', request, report: { outcome: 'succeeded', branches: [{ branch: 'done/feature/1-x', decision: 'deleted', reason: 'Removed.' }] } };
  const validate = validation.branchOperation(request);
  assert.equal(validate({ operation }).status, 'succeeded');
  assert.throws(() => validate({ operation: { ...operation, request: { ...request, olderThanDays: 1 } } }));
  assert.throws(() => validate({ operation: { ...operation, request: { ...request, branches: [] } } }));
  assert.throws(() => validate({ operation: { ...operation, report: { branches: [{ branch: 'main', decision: 'deleted', reason: '' }] } } }));
  assert.throws(() => validate({ operation: { ...operation, report: { branches: [{ branch: 'done/feature/2-y', decision: 'deleted', reason: '' }] } } }));
});
