import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
const bundle = await build({ entryPoints: [new URL('../src/shared/api/observers.ts', import.meta.url).pathname],
  bundle: true, platform: 'node', format: 'cjs', write: false });
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { observers, observerDetail, observerHistory, observerInventory } = module.exports;
const observer = {
  observerId: 'obs_0123456789abcdef0123456789abcdef', mode: 'standalone-observer', registrationState: 'reporting',
  name: null, version: null, lastReceivedAtUtc: null, snapshotReceivedAtUtc: null, observedAtUtc: null,
  freshness: 'unknown', freshnessMeaning: 'No snapshot received.', staleAfterSeconds: 90,
  reportedLifecycle: null, reportedReadiness: null, maximumCapacity: null, activeExecutions: null
};
const observation = {
  observedAtUtc: '2040-01-01T00:00:00Z', receivedAtUtc: '2040-01-01T00:00:00Z',
  execution: { localExecutionId: '0123456789abcdef0123456789abcdef', project: 'Project', repository: 'owner/repo',
    issueNumber: 1, issueTitle: 'Issue', stage: 'Completed', outcome: 'success', validationResult: 'passed',
    startedAtUtc: '2039-12-31T23:59:00Z', completedAtUtc: '2040-01-01T00:00:00Z', durationMilliseconds: 60000 }
};
test('observer validators preserve missing metadata and independent freshness, lifecycle and outcomes', () => {
  assert.deepEqual(observers([]), []);
  assert.equal(observers([observer])[0].maximumCapacity, null);
  const stale = { ...observer, freshness: 'stale', reportedLifecycle: 'running', reportedReadiness: 'not-ready' };
  assert.equal(observerDetail({ observer: stale, currentExecutions: [] }).observer.reportedLifecycle, 'running');
  assert.equal(observerHistory([observation])[0].execution.outcome, 'success');
});
test('observer validators reject managed identities, invalid data and oversized responses', () => {
  assert.throws(() => observers([{ ...observer, mode: 'managed' }]));
  assert.throws(() => observers([{ ...observer, observerId: observer.observerId.slice(4) }]));
  assert.throws(() => observers([{ ...observer, freshness: 'ready' }]));
  assert.throws(() => observers([{ ...observer, maximumCapacity: -1 }]));
  assert.throws(() => observers([{ ...observer, lastReceivedAtUtc: 'invalid' }]));
  assert.throws(() => observers(Array(101).fill(observer)));
  assert.throws(() => observerHistory(Array(101).fill(observation)));
  assert.throws(() => observerHistory([{ ...observation, execution: { ...observation.execution, durationMilliseconds: -1 } }]));
  assert.throws(() => observerDetail({ observer, currentExecutions: Array(257).fill(observation) }));
});

test('detail reads preserve multiple nonterminal observations and unavailable optional execution data', () => {
  const active = { ...observation, execution: { ...observation.execution,
    stage: 'Implementing', outcome: null, validationResult: null, completedAtUtc: null, durationMilliseconds: null } };
  const second = { ...active, execution: { ...active.execution, localExecutionId: 'fedcba9876543210fedcba9876543210', issueNumber: 2, stage: 'Validating' } };
  const detail = observerDetail({ observer: { ...observer, freshness: 'stale' }, currentExecutions: [active, second] });
  assert.equal(detail.currentExecutions.length, 2);
  assert.equal(detail.currentExecutions[0].observedAtUtc, active.observedAtUtc);
  assert.equal(detail.currentExecutions[0].execution.durationMilliseconds, null);
  assert.equal(detail.currentExecutions[0].execution.validationResult, null);
  assert.equal(detail.currentExecutions[1].execution.stage, 'Validating');
  assert.equal(observerHistory([active, observation]).length, 2);
});

test('shared observer inventory validates bounded typed projections and global aggregate metadata', () => {
  const inventory = { observers: [observer], executions: [{ observerId: observer.observerId, observation }],
    total: 150, active: 25, totalCapacity: null };
  assert.equal(observerInventory(inventory).total, 150);
  assert.equal(observerInventory(inventory).totalCapacity, null);
  assert.equal(observerInventory(inventory).executions[0].observation.execution.outcome, 'success');
  assert.throws(() => observerInventory({ ...inventory, active: 151 }));
  assert.throws(() => observerInventory({ ...inventory, totalCapacity: -1 }));
  assert.throws(() => observerInventory({ ...inventory, executions: Array(101).fill(inventory.executions[0]) }));
  assert.throws(() => observerInventory({ ...inventory, executions: [{ observerId: 'managed', observation }] }));
});
