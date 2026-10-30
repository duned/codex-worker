import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { build } from 'esbuild';

// Exercise the component's interactions with deterministic hook/API seams.
const { outputFiles } = await build({
  entryPoints: [new URL('../src/features/workers/WorkerRecovery.tsx', import.meta.url).pathname],
  bundle: true, write: false, format: 'cjs', platform: 'node', jsx: 'automatic',
  external: ['react', 'react/jsx-runtime'],
  plugins: [{ name: 'recovery-seams', setup(builder) {
    builder.onResolve({ filter: /shared\/api\/session|shared\/i18n|base\/buttons\/button|base\/checkbox\/checkbox/ }, args => ({ path: args.path, namespace: 'seam' }));
    builder.onLoad({ filter: /.*/, namespace: 'seam' }, args => ({ contents: args.path.includes('session')
      ? 'export const useApiRead = () => globalThis.harness.operation; export const useApiMutation = (...args) => globalThis.harness.mutation(...args); export const useRuntime = () => globalThis.harness.runtime;'
      : args.path.includes('i18n') ? 'export const t = key => key;'
        : args.path.includes('checkbox') ? 'export const Checkbox = "checkbox";' : 'export const Button = "button";' }));
  } }]
});
function setup({ eligible = false, reject = false } = {}) {
  let cursor = 0;
  const state = [];
  const calls = [];
  const operation = { data: { state: 'failed', recovery: {
    schedulingBlocked: true, checkedAtUtc: '2026-10-30T00:00:00Z', eligible,
    checks: [{ code: 'configuration-synchronized', satisfied: eligible, description: 'Configuration synchronized.', nextStep: 'Resolve configuration errors.' }]
  } }, refetch: async () => { calls.push('read'); } };
  const mutation = { isPending: false, locked: false,
    mutateAsync: async body => { harness.body = body; calls.push('reconcile'); if (reject) throw new Error('409'); return harness.result ?? { state: 'succeeded' }; },
    reconcile: async read => { calls.push('refresh'); await read(); }, reset: () => {} };
  const harness = { mutation: (...args) => { harness.path = args[1]; return mutation; },
    runtime: { queries: { invalidateQueries: async () => { calls.push('invalidate'); } } } };
  const module = { exports: {} };
  const jsx = (type, props) => ({ type, props });
  vm.runInNewContext(outputFiles[0].text, { module, exports: module.exports, harness, crypto: webcrypto,
    require(name) {
      if (name === 'react/jsx-runtime') return { jsx, jsxs: jsx };
      if (name === 'react') return { useState(initial) { const index = cursor++; if (!(index in state)) state[index] = initial; return [state[index], value => { state[index] = value; }]; },
        useRef(initial) { const index = cursor++; return state[index] ?? (state[index] = { current: initial }); } };
      throw new Error(name);
    }
  });
  const render = () => { cursor = 0; return module.exports.WorkerRecovery({ workerId: 'worker', operation, onRecovered: async () => { calls.push('recovered'); } }); };
  return { render, operation, mutation, calls, harness };
}
function nodes(tree) {
  if (!tree || typeof tree !== 'object') return [];
  return [tree, ...[tree.props?.children].flat(2).flatMap(nodes)];
}
function button(tree, label) { return nodes(tree).find(node => node.type === 'button' && node.props.children === label); }
const flush = () => new Promise(setImmediate);

test('blocked recovery shows the exact prerequisite and refreshes without mutation', async () => {
  const view = setup();
  const tree = view.render();
  assert.match(JSON.stringify(tree), /Resolve configuration errors/);
  assert.equal(button(tree, 'workers.recoveryAction').props.isDisabled, true);
  button(tree, 'workers.recoveryRefresh').props.onPress();
  await flush();
  assert.deepEqual(view.calls, ['refresh', 'read']);
});
test('eligible recovery requires quiescence confirmation and invokes reconciliation only once', async () => {
  const view = setup({ eligible: true });
  let tree = view.render();
  assert.equal(button(tree, 'workers.recoveryAction').props.isDisabled, true);
  nodes(tree).find(node => node.type === 'checkbox').props.onChange(true);
  tree = view.render();
  const action = button(tree, 'workers.recoveryAction');
  assert.equal(action.props.isDisabled, false);
  action.props.onPress();
  action.props.onPress();
  await flush();
  assert.equal(view.harness.path, '/api/v1/workers/worker/update/reconcile?nodeQuiescent=true');
  assert.deepEqual(view.calls, ['reconcile', 'invalidate', 'recovered']);
});
test('a rejected recovery refreshes authoritative blockers without replaying restart', async () => {
  const view = setup({ eligible: true, reject: true });
  nodes(view.render()).find(node => node.type === 'checkbox').props.onChange(true);
  button(view.render(), 'workers.recoveryAction').props.onPress();
  await flush();
  assert.deepEqual(view.calls, ['reconcile', 'read']);
  assert.match(JSON.stringify(view.render()), /workers.recoveryFailure/);
});

test('update mismatch displays all versions and submits one idempotent managed retry', async () => {
  const view = setup({ eligible: false });
  Object.assign(view.operation.data, { operationId: 'a'.repeat(32), restart: false, expectedVersion: '1.1.0', originalVersion: '1.0.0' });
  Object.assign(view.operation.data.recovery, { retryEligible: true, reportedVersion: '1.0.1' });
  let tree = view.render();
  assert.match(JSON.stringify(tree), /1\.1\.0/);
  assert.match(JSON.stringify(tree), /1\.0\.0/);
  assert.match(JSON.stringify(tree), /1\.0\.1/);
  assert.equal(button(tree, 'workers.recoveryRetryAction').props.isDisabled, true);
  nodes(tree).find(node => node.type === 'checkbox').props.onChange(true);
  tree = view.render();
  const action = button(tree, 'workers.recoveryRetryAction');
  action.props.onPress();
  action.props.onPress();
  await flush();
  assert.equal(view.harness.path, '/api/v1/workers/worker/update/retry?nodeQuiescent=true');
  assert.match(view.harness.body.operationId, /^[a-f0-9]{32}$/);
  assert.equal(view.calls.filter(call => call === 'reconcile').length, 1);
});
test('failed restart restores only Server policy with separate eligibility and confirmation', async () => {
  const view = setup({ eligible: false });
  Object.assign(view.operation.data, { operationId: 'a'.repeat(32), restart: true, expectedVersion: '1.0.0', originalVersion: '1.0.0' });
  Object.assign(view.operation.data.recovery, { reportedVersion: '1.1.0', releaseDrainEligible: false });
  view.operation.data.recovery.checks = [{ code: 'no-active-executions', satisfied: false, description: 'Executions unknown.', nextStep: 'Wait for an idle heartbeat.' }];
  assert.match(JSON.stringify(view.render()), /Wait for an idle heartbeat/);
  assert.equal(button(view.render(), 'workers.recoveryReleaseAction').props.isDisabled, true);
  view.operation.data.recovery.releaseDrainEligible = true;
  assert.equal(button(view.render(), 'workers.recoveryReleaseAction').props.isDisabled, true);
  nodes(view.render()).find(node => node.type === 'checkbox').props.onChange(true);
  const tree = view.render();
  assert.equal(button(tree, 'workers.recoveryReleaseAction').props.isDisabled, false);
  assert.match(JSON.stringify(tree), /1\.1\.0/);
  assert.match(JSON.stringify(tree), /workers.recoveryReleaseHelp/);
  view.harness.result = { state: 'failed', drainReleasedAtUtc: '2026-10-30T01:00:00Z' };
  button(tree, 'workers.recoveryReleaseAction').props.onPress();
  await flush();
  assert.equal(view.harness.path, '/api/v1/workers/worker/update/release-drain?nodeQuiescent=true');
  assert.equal(view.harness.body.operationId, 'a'.repeat(32));
  assert.deepEqual(view.calls, ['reconcile', 'invalidate', 'recovered']);
  assert.match(JSON.stringify(view.render()), /workers.recoveryReleaseSuccess/);
  delete view.operation.data.recovery.reportedVersion;
  assert.match(JSON.stringify(view.render()), /workers.recoveryUnavailable/);
  Object.assign(view.operation.data, { drainReleasedAtUtc: '2026-10-30T01:00:00Z', diagnostic: 'restart-failed' });
  assert.match(JSON.stringify(view.render()), /restart-failed/);
  assert.equal(button(view.render(), 'workers.recoveryReleaseAction'), undefined);
});
