import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';

const bundle = await build({ stdin: { contents: `export { provisioningReason } from './src/features/nodes/provisioning';`, resolveDir: new URL('..', import.meta.url).pathname }, bundle: true, platform: 'node', format: 'cjs', write: false });
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { provisioningReason } = module.exports;
const node = { id: 'node', connectivity: 'connected', provisioningReadiness: 'ready', capabilities: [
  { definition: { id: 'claude-cli' }, availableActions: ['login', 'refresh', 'checkauthentication'], state: { health: 'Error' } },
  { definition: { id: 'git' }, availableActions: ['refresh'] },
] };
const command = status => ({ request: { nodeId: 'node', capabilityId: 'claude-cli', action: 'Login' }, status,
  deadlineUtc: '2000-01-01T00:00:00Z', failureDetail: { code: 'ManualVendorCliCompletionRequired' } });

test('terminal Claude login permits advertised retry and inspection despite probe error', () => {
  for (const status of ['Failed', 'Succeeded', 'Cancelled', 'TimedOut']) {
    for (const action of ['login', 'refresh', 'checkauthentication']) assert.equal(provisioningReason(node, [command(status)], 'claude-cli', action), '');
    assert.equal(provisioningReason(node, [command(status)], 'git', 'refresh'), '');
  }
});
test('active and expired uncertain commands block only their node capability', () => {
  for (const status of ['Pending', 'Running']) {
    const busy = { ...node, provisioningReadiness: 'busy' };
    assert.ok(provisioningReason(busy, [command(status)], 'claude-cli', 'login'));
    assert.equal(provisioningReason(busy, [command(status)], 'git', 'refresh'), '');
    assert.equal(provisioningReason(node, [{ ...command(status), request: { ...command(status).request, nodeId: 'other' } }], 'claude-cli', 'login'), '');
  }
  assert.ok(provisioningReason(node, undefined, 'claude-cli', 'login'));
  assert.ok(provisioningReason({ ...node, connectivity: 'disconnected' }, [], 'claude-cli', 'login'));
  assert.ok(provisioningReason(node, [], 'claude-cli', 'logout'));
});
