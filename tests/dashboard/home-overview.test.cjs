const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'home-overview-'));
const root = resolve('src/CodexServer/worker-poc/src');
buildSync({ stdin: { contents: `export * from '${root}/features/server/readiness'; export * from '${root}/features/home/model'; export * from '${root}/shared/api/validation';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'model.cjs') });
const { serverGitHubReadiness, preparedWorker, workerAttention, executionNeedsAttention, aggregateCapacity, serverGitHubConnection, executions } = require(join(directory, 'model.cjs'));
const { worker, node, capability, current } = require('./worker-poc-fixtures.cjs');
after(() => rmSync(directory, { recursive: true, force: true }));
const server = { ...node, id: 'server', kind: 'server', capabilities: [{ ...capability, definition: { ...capability.definition, id: 'github-cli' } }] };
const connection = { commands: [], provisioningEnabled: false, elevationAllowed: false };
test('empty and unavailable observations never complete setup or invent capacity', () => {
  assert.equal(serverGitHubReadiness(undefined, connection).complete, false);
  assert.equal(serverGitHubReadiness(server, undefined).complete, false);
  assert.equal(preparedWorker([]), undefined);
  assert.equal(aggregateCapacity(undefined, 'maximumCapacity'), 'Unavailable');
  assert.equal(aggregateCapacity([], 'maximumCapacity'), 0);
});
test('configured prerequisites are independent of scheduling and local provisioning policy', () => {
  assert.equal(serverGitHubReadiness(server, connection).complete, true);
  assert.equal(preparedWorker([node]).id, worker.workerId);
  assert.deepEqual(workerAttention(worker, [node]), []);
  assert.match(workerAttention({ ...worker, schedulingPolicy: 'Disabled' }, [node]).join(), /Scheduling: Disabled/);
  assert.equal(aggregateCapacity([worker, { ...worker, maximumCapacity: 8 }], 'maximumCapacity'), 10);
  assert.equal(aggregateCapacity([{ workerId: 'missing', availability: 'online' }], 'availableCapacity'), 'Unavailable');
});
test('stale, disconnected and unavailable inventories cannot complete readiness', () => {
  assert.equal(serverGitHubReadiness({ ...server, observationsStale: true }, connection).complete, false);
  assert.equal(serverGitHubReadiness({ ...server, connectivity: 'unavailable' }, connection).complete, false);
  assert.equal(preparedWorker([{ ...node, observationsStale: true }]), undefined);
  assert.match(workerAttention(worker, undefined).join(), /unavailable/);
  assert.match(workerAttention(worker, [{ ...node, observationsStale: true }]).join(), /stale/);
});
test('retained active and failed authentication operations override healthy capabilities', () => {
  for (const status of ['Pending', 'Running', 'Failed', 'TimedOut']) {
    const commands = [{ id: 'login', createdAtUtc: current.createdAtUtc, status, request: { nodeId: 'server', capabilityId: 'github-cli', action: 'Login' } }];
    assert.equal(serverGitHubReadiness(server, { ...connection, commands }).complete, false);
  }
  assert.equal(serverGitHubReadiness({ ...server, capabilities: [{ ...server.capabilities[0], state: { ...capability.state, operation: { state: 'Failed', action: 'checkauthentication' } } }] }, connection).complete, false);
});
test('recovery, pending work and eligibility remain distinct actionable evidence', () => {
  assert.equal(executionNeedsAttention(current), false);
  for (const evidence of [{ recoveryState: 'IntegrationUncertain' }, { pendingReason: 'No eligible Worker' }, { managedEligibilityState: 'blocked', managedEligibilityReasons: ['Dependency open'] }]) {
    const value = { ...current, ...evidence };
    assert.equal(executionNeedsAttention(executions([value])[0]), true);
  }
  assert.throws(() => executions([{ ...current, managedEligibilityReasons: [{}] }]));
  assert.throws(() => serverGitHubConnection({ ...connection, provisioningEnabled: 'true' }));
});
