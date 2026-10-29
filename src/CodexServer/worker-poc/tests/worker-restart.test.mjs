import test from 'node:test';
import assert from 'node:assert/strict';
import { restartAvailable, restartPending } from '../src/features/workers/worker-restart-model.mjs';
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
