const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const dir = mkdtempSync(join(tmpdir(), 'settings-contracts-'));
const entry = resolve('src/CodexServer/worker-poc/src');
buildSync({ stdin: { contents: `export * from '${entry}/features/settings/contracts'; export * from '${entry}/features/server/connection'; export * from '${entry}/features/server/readiness'; export * from '${entry}/features/provisioning/actions'; export * from '${entry}/shared/api/validation'; export * from '${entry}/shared/api/runtime'; export * from '${entry}/shared/api/client';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(dir, 'test.cjs') });
const { credential, credentialOutcome, connectionSnapshot, serverGitHubReadiness, commands, DashboardRuntime, HttpClient, queryKeys, activeCommand, expiredCommand } = require(join(dir, 'test.cjs'));
after(() => rmSync(dir, { recursive: true, force: true }));
const now = Date.parse('2026-01-01T00:00:00Z');
const c = { id: 'credential-a', provider: 'GitHub', type: 'PAT', secretReference: 'credential:credential-a', status: 'Ready', version: 2, createdAtUtc: new Date(now).toISOString(), updatedAtUtc: new Date(now).toISOString() };
const operation = { id: 'operation-a', createdAtUtc: new Date(now).toISOString(), status: 'Running', request: { nodeId: 'server', capabilityId: 'github-cli', action: 'Login' }, deadlineUtc: new Date(now + 10000).toISOString(), loginInstructions: { verificationUri: 'https://github.com/login/device', userCode: 'TEST-CODE' } };
const connection = { commands: [operation], provisioningEnabled: true, elevationAllowed: true };
test('metadata projections discard secret payloads and reject malformed evidence', () => {
  const projected = credential({ ...c, secret: 'private', value: 'private' });
  assert.equal(JSON.stringify(projected).includes('private'), false);
  for (const invalid of [{ ...c, version: '2' }, { ...c, assignedWorkerId: {} }, { ...c, secretReference: {} }]) assert.throws(() => credential(invalid));
});
test('device challenge requires running Login, a current deadline and fixed verification origin', () => {
  assert.equal(connectionSnapshot(connection, now).challenge.userCode, 'TEST-CODE');
  assert.equal(connectionSnapshot(connection, now + 10000).challenge, undefined);
  for (const changed of [{ status: 'Pending' }, { status: 'Succeeded' }, { request: { ...operation.request, action: 'Detect' } }, { deadlineUtc: 'invalid' }, { loginInstructions: { verificationUri: 'https://attacker.test', userCode: 'TEST-CODE' } }]) assert.equal(connectionSnapshot({ ...connection, commands: [{ ...operation, ...changed }] }, now).challenge, undefined);
  assert.equal(JSON.stringify(connectionSnapshot(connection, now).metadata).includes('TEST-CODE'), false);
  assert.equal(JSON.stringify(commands([operation])).includes('TEST-CODE'), false);
});
test('queued/running and expired operations remain distinct from successful authentication', () => {
  const node = { connectivity: 'connected', observationsStale: false, capabilities: [{ definition: { id: 'github-cli' }, state: { installation: 'Installed', health: 'Healthy', authentication: 'Satisfied' } }] };
  assert.equal(serverGitHubReadiness(node, connection).complete, false);
  assert.equal(serverGitHubReadiness(node, { ...connection, commands: [{ ...operation, status: 'Succeeded' }] }).complete, true);
  assert.equal(serverGitHubReadiness({ ...node, observationsStale: true }, { ...connection, commands: [] }).complete, false);
  assert.equal(serverGitHubReadiness({ ...node, capabilities: [{ ...node.capabilities[0], state: { ...node.capabilities[0].state, authentication: 'Missing' } }] }, { ...connection, commands: [{ ...operation, status: 'Succeeded' }] }).complete, false);
  assert.equal(activeCommand(operation), true);
  assert.equal(expiredCommand(operation, now + 10000), true);
  assert.equal(expiredCommand({ ...operation, status: 'Pending' }, now + 10000), false);
});
test('credential response-loss recovery requires observable matching metadata', () => {
  assert.equal(credentialOutcome({ kind: 'replace', before: c }, [c]), undefined);
  assert.equal(credentialOutcome({ kind: 'replace', before: c }, [{ ...c, version: 3 }]).version, 3);
  assert.equal(credentialOutcome({ kind: 'replace', before: c }, [{ ...c, version: 4 }]), undefined);
  assert.equal(credentialOutcome({ kind: 'assign', before: c, workerId: 'worker-a' }, [{ ...c, assignedWorkerId: 'worker-b' }]), undefined);
  assert.equal(credentialOutcome({ kind: 'assign', before: c, workerId: 'worker-a' }, [{ ...c, assignedWorkerId: 'worker-a' }]).id, c.id);
  assert.equal(credentialOutcome({ kind: 'revoke', before: c }, [{ ...c, status: 'Revoked' }]).status, 'Revoked');
  const create = { kind: 'create', provider: c.provider, type: c.type, existingIds: [c.id] };
  assert.equal(credentialOutcome(create, [c]), undefined);
  assert.equal(credentialOutcome(create, [{ ...c, id: 'new-a' }, { ...c, id: 'new-b' }]), undefined);
  assert.equal(credentialOutcome(create, [{ ...c, id: 'new-a' }]).id, 'new-a');
});
test('lost provisioning response fences retries through cache refresh/navigation until explicit fresh recovery', async () => {
  const calls = [];
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (path, options) => {
    calls.push({ path, method: options.method ?? 'GET' });
    if (path.endsWith('/session')) return Response.json({ csrfToken: 'fixture-csrf', expiresAtUtc: new Date(Date.now() + 3600000).toISOString() });
    if (path.endsWith('/stream') || options.method === 'POST') throw Error('Fixture response loss');
    return Response.json(connection);
  }));
  try {
    await runtime.session('GET');
    await assert.rejects(runtime.mutate('node:server', '/api/v1/provisioning/commands', 'POST', { nodeId: 'server', capabilityId: 'github-cli', action: 'Login' }, value => value, async () => {}));
    await runtime.read('/api/v1/nodes/server/github-connection', new AbortController().signal, value => connectionSnapshot(value, now).metadata);
    runtime.queries.setQueryData(queryKeys.read(runtime.snapshot().generation, '/api/v1/nodes/server/github-connection'), connectionSnapshot(connection, now).metadata);
    assert.equal(runtime.locked('node:server'), true);
    await assert.rejects(runtime.mutate('node:server', '/api/v1/provisioning/commands', 'POST', {}, value => value, async () => {}));
    assert.equal(calls.filter(c => c.method === 'POST').length, 1);
    await runtime.reconcile('node:server', signal => runtime.read('/api/v1/nodes/server/github-connection', signal, value => connectionSnapshot(value, now).metadata).then(() => {}));
    assert.equal(runtime.locked('node:server'), false);
    assert.equal(JSON.stringify(runtime.queries.getQueryCache().getAll().map(q => q.state.data)).includes('TEST-CODE'), false);
  } finally { runtime.dispose(); }
});
