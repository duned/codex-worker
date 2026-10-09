const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'workers-migration-'));
const source = resolve('src/CodexServer/worker-poc/src');
buildSync({ stdin: { contents: `export * from '${source}/features/workers/enrollment'; export * from '${source}/features/nodes/provisioning'; export * from '${source}/shared/api/validation';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'contracts.cjs') });
const api = require(join(directory, 'contracts.cjs'));
after(() => rmSync(directory, { recursive: true, force: true }));
const request = { contractVersion: 1, workerId: 'a'.repeat(32), operation: 'associate', server: 'https://dashboard.test' };
test('enrollment restores only the exact public request; secrets, mismatched origin and unsupported operations are rejected', () => {
  const text = JSON.stringify(request);
  assert.deepEqual(api.readPairingRequest(text, 'associate', request.server), request);
  assert.deepEqual(api.restorePairingRequest(text, request.server), request);
  for (const item of [{ ...request, authorization: 'private' }, { ...request, contractVersion: 2 }, { ...request, operation: 'shell' }, { ...request, server: 'https://other.test' }])
    assert.equal(api.restorePairingRequest(JSON.stringify(item), request.server), undefined);
  assert.throws(() => api.readPairingRequest(text, 'enroll', request.server));
  assert.throws(() => api.readPairingRequest('invalid', 'associate', request.server));
  assert.equal(api.restorePairingRequest(null, request.server), undefined);
});
test('enrollment uses the explicit source version and bounds transient authorization lifetime', () => {
  assert.match(api.registrationInstruction('0.15.0', 'enroll', request.server), /--version 0.15.0/);
  assert.match(api.registrationInstruction('0.15.0', 'associate', request.server), /--operation associate --pair/);
  assert.match(api.registrationInstruction('$(command)', 'enroll', request.server), /required/);
  assert.match(api.registrationInstruction('0.15.0', 'enroll', 'http://dashboard.test'), /required/);
  assert.deepEqual(api.pairingAuthorization({ authorization: 'transient', lifetimeSeconds: 900 }), { authorization: 'transient', lifetimeSeconds: 900 });
  for (const lifetimeSeconds of [0, 901, NaN, '900']) assert.throws(() => api.pairingAuthorization({ authorization: 'private', lifetimeSeconds }));
});
test('pairing acknowledgement requires active authentication and an observed online heartbeat', () => {
  const worker = { workerId: request.workerId, availability: 'online', authenticationCredentialStatus: 'active', lastHeartbeatAtUtc: '2026-01-01T00:00:00Z' };
  assert.equal(api.pairingAcknowledged(worker), true);
  for (const item of [undefined, { ...worker, authenticationCredentialStatus: 'revoked' }, { ...worker, lastHeartbeatAtUtc: undefined }, { ...worker, lastHeartbeatAtUtc: 'invalid' }, ...['offline', 'stale', 'unknown', 'unavailable'].map(availability => ({ ...worker, availability }))])
    assert.equal(api.pairingAcknowledged(item), false);
});
test('typed operations reject unavailable, busy and withdrawn capability actions while permitting re-detection of stale observations', () => {
  const node = { connectivity: 'connected', observationsStale: true, provisioningReadiness: 'ready', capabilities: [{ definition: { id: 'git' }, availableActions: ['refresh', 'verifyrepositoryaccess'] }] };
  assert.equal(api.provisioningReason(node, [], 'git', 'refresh'), '');
  assert.match(api.provisioningReason(undefined, [], 'git', 'refresh'), /unavailable/);
  assert.match(api.provisioningReason(node, undefined, 'git', 'refresh'), /unavailable/);
  assert.match(api.provisioningReason({ ...node, connectivity: 'disconnected' }, [], 'git', 'refresh'), /disconnected/);
  assert.match(api.provisioningReason(node, [{ status: 'Running' }], 'git', 'refresh'), /pending/);
  for (const action of ['uninstall', '__proto__', 'constructor']) assert.match(api.provisioningReason(node, [], 'git', action), /advertised/);
});
test('command deadlines and device-login links use actual contract evidence without invented expiry or URLs', () => {
  const now = Date.parse('2026-01-01T00:02:30Z');
  assert.equal(api.commandExpired({ status: 'Running', deadlineUtc: '2026-01-01T00:01:00Z' }, now), true);
  for (const item of [{ status: 'Running' }, { status: 'Running', deadlineUtc: 'invalid' }, { status: 'Pending', deadlineUtc: '2020-01-01' }]) assert.equal(api.commandExpired(item, now), false);
  assert.equal(api.deviceLoginUrl('https://github.com/login/device'), 'https://github.com/login/device');
  assert.equal(api.deviceLoginUrl('javascript:alert(1)'), undefined);
  assert.equal(api.deviceLoginUrl('https://github.com.evil/login/device'), undefined);
});
test('Server-shaped command, readiness and delivery metadata validate nested presentation fields', () => {
  const command = { id: 'retained', createdAtUtc: '2026-01-01', status: 'Running', request: { nodeId: request.workerId, capabilityId: 'codex-cli', action: 'Login' }, deadlineUtc: '2026-01-02', loginInstructions: { userCode: 'CODE', verificationUri: 'https://auth.openai.com/codex/device' } };
  assert.equal(api.command(command), command);
  assert.throws(() => api.command({ ...command, loginInstructions: { userCode: {}, verificationUri: 'https://auth.openai.com/codex/device' } }));
  assert.deepEqual(api.deliveryAuthorization({ status: 'revoked', revokedAtUtc: '2026-01-01' }), { status: 'revoked', revokedAtUtc: '2026-01-01' });
  assert.throws(() => api.deliveryAuthorization({ status: 'active', revokedAtUtc: {} }));
  const diagnostics = { aiAgentReady: true, gitHubReady: false, gitReady: true, configurationSynchronization: 'synchronized', provisioningState: 'Running', capabilityObservationsCurrent: false, projects: [{ projectId: 'project', projectName: 'Project', isEligible: false, missingRequirements: ['Scoped GitHub access'], observationStatus: 'stale-revision' }] };
  assert.equal(api.diagnostics(diagnostics), diagnostics);
  assert.throws(() => api.diagnostics({ ...diagnostics, projects: [{ ...diagnostics.projects[0], missingRequirements: [{}] }] }));
  assert.throws(() => api.diagnostics({ ...diagnostics, recentOperationalError: {} }));
  const worker = { workerId: request.workerId, availability: 'online', platform: 'linux', firstRegisteredAtUtc: '2026-01-01', activeProjects: ['Project'] };
  assert.deepEqual(api.workers([worker]), [worker]);
  assert.throws(() => api.workers([{ ...worker, activeProjects: [{}] }]));
});
test('optional host observations preserve older Workers and reject unsafe resource evidence', () => {
  const worker = { workerId: request.workerId, availability: 'online', workerVersion: '0.15.0' };
  assert.deepEqual(api.workers([worker]), [worker]);
  const hostResources = { measuredAtUtc: '2026-01-01T00:00:00Z', logicalCpuCount: 8, totalMemoryBytes: 1024, usedMemoryBytes: 512, cpuUsagePercent: 25, memoryUsagePercent: 50, sampleSeconds: 1 };
  assert.deepEqual(api.workers([{ ...worker, hostResources }])[0].hostResources, hostResources);
  for (const invalid of [{ cpuUsagePercent: 101 }, { memoryUsagePercent: -1 }, { logicalCpuCount: {} }, { measuredAtUtc: 'invalid' }, { totalMemoryBytes: Infinity }])
    assert.throws(() => api.workers([{ ...worker, hostResources: { ...hostResources, ...invalid } }]));
});
