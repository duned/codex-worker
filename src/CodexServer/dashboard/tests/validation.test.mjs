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
