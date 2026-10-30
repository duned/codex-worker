import test from 'node:test';
import assert from 'node:assert/strict';
import { restartAvailable, restartPending, recoveryAvailable, releaseRestartDrainAvailable } from '../src/features/workers/worker-restart-model.mjs';
const worker = { availability: 'online', capabilities: [{ type: 'protocol', name: 'worker-restart-v1' }] };
test('restart requires online Worker with the controlled restart protocol', () => {
  assert.equal(restartAvailable(worker, null), true);
  assert.equal(restartAvailable({ ...worker, availability: 'offline' }, null), false);
  assert.equal(restartAvailable({ ...worker, capabilities: [] }, null), false);
});
test('pending operations and failed operations prevent another dispatch', () => {
  for (const state of ['waiting-for-idle', 'updating', 'reconnecting']) {
    assert.equal(restartPending({ state }), true);
    assert.equal(restartAvailable(worker, { state }), false);
  }
  assert.equal(restartAvailable(worker, { state: 'failed' }), false);
  assert.equal(restartAvailable(worker, { state: 'succeeded' }), true);
});

test('recovery requires authoritative eligibility and confirmed node quiescence', () => {
  const operation = { state: 'failed', recovery: { eligible: true } };
  assert.equal(recoveryAvailable(operation, true), true);
  assert.equal(recoveryAvailable(operation, false), false);
  assert.equal(recoveryAvailable({ ...operation, recovery: { eligible: false } }, true), false);
  for (const state of ['waiting-for-idle', 'updating', 'reconnecting', 'succeeded']) {
    assert.equal(recoveryAvailable({ ...operation, state }, true), false);
  }
  assert.equal(recoveryAvailable(null, true), false);
});

test('Server drain release permits an unavailable service but needs its own authoritative checks', () => {
  const operation = { restart: true, state: 'failed', recovery: { eligible: false, releaseDrainEligible: true } };
  assert.equal(releaseRestartDrainAvailable(operation, true), true);
  assert.equal(releaseRestartDrainAvailable(operation, false), false);
  assert.equal(releaseRestartDrainAvailable({ ...operation, restart: false }, true), false);
  assert.equal(releaseRestartDrainAvailable({ ...operation, state: 'reconnecting' }, true), false);
  assert.equal(releaseRestartDrainAvailable({ ...operation, drainReleasedAtUtc: '2026-01-01' }, true), false);
  assert.equal(releaseRestartDrainAvailable({ ...operation, recovery: { eligible: true } }, true), false);
  assert.equal(releaseRestartDrainAvailable(null, true), false);
});
