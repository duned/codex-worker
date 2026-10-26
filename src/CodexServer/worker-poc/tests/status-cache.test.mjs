import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
const bundle = await build({ stdin: { contents: `
export { DashboardRuntime, queryKeys } from './src/shared/api/runtime';
export { HttpClient } from './src/shared/api/client';
export { QueryObserver } from '@tanstack/react-query';
`, resolveDir: new URL('..', import.meta.url).pathname }, bundle: true, platform: 'node', format: 'cjs', write: false });
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { DashboardRuntime, queryKeys, HttpClient, QueryObserver } = module.exports;
async function fixture(t) {
  const counts = new Map();
  const control = { fail: false, failProjects: false, startedAtUtc: '2026-01-01T00:00:00Z' };
  const freshness = { projects: 1, workers: 1, executions: 1 };
  const runtime = new DashboardRuntime(undefined, new HttpClient(async path => {
    counts.set(path, (counts.get(path) ?? 0) + 1);
    if (path.endsWith('/session')) return Response.json({ csrfToken: 'test', expiresAtUtc: new Date(Date.now() + 3600000).toISOString() });
    if (path === '/api/v1/projects' && control.failProjects) throw new Error('offline');
    if (path === '/api/status' && control.fail) throw new Error('offline');
    if (path === '/api/status') return Response.json({ state: 'ready', startedAtUtc: control.startedAtUtc, persistenceAvailable: true, freshness });
    return Response.json([]);
  }));
  await runtime.session('GET'); runtime.stopLive();
  const generation = runtime.snapshot().generation;
  const options = path => ({ queryKey: queryKeys.read(generation, path), queryFn: ({ signal }) => runtime.read(path, signal, value => value) });
  const subscriptions = [];
  const observe = path => { const observer = new QueryObserver(runtime.queries, options(path)); subscriptions.push(observer.subscribe(() => {})); return observer; };
  t.after(() => { subscriptions.forEach(stop => stop()); runtime.dispose(); });
  return { runtime, counts, freshness, options, observe, control };
}
test('unchanged status only fetches status; changed marker refreshes only its active collection', async t => {
  const f = await fixture(t);
  for (const path of ['/api/v1/projects', '/api/v1/workers', '/api/v1/executions?limit=50', '/api/v1/projects/p1']) {
    await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  }
  await f.runtime.checkStatus(); await f.runtime.checkStatus();
  assert.equal(f.counts.get('/api/status'), 2);
  assert.equal(f.counts.get('/api/v1/projects'), 1);
  f.freshness.projects++;
  await f.runtime.checkStatus();
  assert.equal(f.counts.get('/api/v1/projects'), 2);
  assert.equal(f.counts.get('/api/v1/workers'), 1);
  assert.equal(f.counts.get('/api/v1/executions?limit=50'), 1);
  assert.equal(f.counts.get('/api/v1/projects/p1'), 1);
  await f.runtime.queries.fetchQuery(f.options('/api/v1/projects/p1'));
  assert.equal(f.counts.get('/api/v1/projects/p1'), 2);
});
test('same-key requests deduplicate and navigation reuses fresh collection cache', async t => {
  const f = await fixture(t), options = f.options('/api/v1/workers');
  await Promise.all([f.runtime.queries.fetchQuery(options), f.runtime.queries.fetchQuery(options)]);
  f.observe('/api/v1/workers');
  await f.runtime.queries.fetchQuery(options);
  assert.equal(f.counts.get('/api/v1/workers'), 1);
});
test('mutation invalidates its affected projections without refetching unrelated projects or credentials', async t => {
  const f = await fixture(t);
  for (const path of ['/api/v1/projects', '/api/v1/credentials', '/api/v1/workers', '/api/v1/workers/w1', '/api/v1/workers/w2']) {
    await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  }
  await f.runtime.mutate('w1', '/api/v1/workers/w1', 'PUT', {}, value => value, async () => {});
  assert.equal(f.counts.get('/api/v1/projects'), 1);
  assert.equal(f.counts.get('/api/v1/credentials'), 1);
  assert.equal(f.counts.get('/api/v1/workers'), 2);
  assert.equal(f.counts.get('/api/v1/workers/w2'), 1);
  assert.equal(f.counts.get('/api/v1/workers/w1'), 3); // initial GET, PUT, refreshed GET
});

test('hidden tabs pause checks, visibility resumes, and failures back off without clearing retained data', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const f = await fixture(t);
  const priorDocument = globalThis.document;
  const document = new EventTarget();
  document.visibilityState = 'hidden';
  globalThis.document = document;
  t.after(() => { globalThis.document = priorDocument; });
  f.runtime.startLive();
  t.mock.timers.tick(60000);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.counts.get('/api/status'), undefined);
  document.visibilityState = 'visible'; document.dispatchEvent(new Event('visibilitychange'));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.counts.get('/api/status'), 1);
  const key = queryKeys.read(f.runtime.snapshot().generation, '/api/status');
  f.control.fail = true;
  t.mock.timers.tick(5000);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.counts.get('/api/status'), 2);
  assert.equal(f.runtime.queries.getQueryState(key).status, 'error');
  assert.equal(f.runtime.queries.getQueryData(key).state, 'ready');
  t.mock.timers.tick(7000); // first backoff is at least 8 seconds, including jitter
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(f.counts.get('/api/status'), 2);
  f.runtime.stopLive();
});

test('failed collection refresh retains data and retries on unchanged status', async t => {
  const f = await fixture(t), path = '/api/v1/projects';
  await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  await f.runtime.checkStatus();
  f.control.failProjects = true; f.freshness.projects++;
  await assert.rejects(f.runtime.checkStatus());
  assert.deepEqual(f.runtime.queries.getQueryData(f.options(path).queryKey), []);
  f.control.failProjects = false;
  await f.runtime.checkStatus();
  assert.equal(f.counts.get(path), 3);
  await f.runtime.checkStatus();
  assert.equal(f.counts.get(path), 3);
});

test('failed initial collection load retries on unchanged status and stops after recovery', async t => {
  const f = await fixture(t), path = '/api/v1/projects';
  f.control.failProjects = true;
  f.observe(path);
  await assert.rejects(f.runtime.queries.fetchQuery(f.options(path)));
  await assert.rejects(f.runtime.checkStatus());
  f.control.failProjects = false;
  await f.runtime.checkStatus();
  assert.deepEqual(f.runtime.queries.getQueryData(f.options(path).queryKey), []);
  assert.equal(f.counts.get(path), 3);
  await f.runtime.checkStatus();
  assert.equal(f.counts.get(path), 3);
});

test('one minute of unchanged Home status has no resource or GitHub detail fan-out', async t => {
  const f = await fixture(t);
  const paths = ['/api/v1/projects', '/api/v1/workers', '/api/v1/executions?limit=50&offset=0',
    '/api/v1/nodes', '/api/v1/nodes/server/github-connection'];
  for (const path of paths) {
    await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  }
  // Twelve completed status rounds represent the baseline's steady-state
  // one-minute budget, without wall-clock sleeps or network latency assumptions.
  for (let round = 0; round < 12; round++) await f.runtime.checkStatus();
  assert.equal(f.counts.get('/api/status'), 12);
  for (const path of paths) assert.equal(f.counts.get(path), 1);
  assert.equal([...f.counts.keys()].some(path => path.includes('/github/issues/')), false);
  assert.equal([...f.counts.values()].reduce((sum, count) => sum + count, 0), 18); // session + five mounts + twelve checks
});

test('execution changes refresh each active project scope once and preserve inactive cache invalidation', async t => {
  const f = await fixture(t);
  const paths = ['/api/v1/executions?projectId=p1&limit=50', '/api/v1/executions?projectId=p2&limit=50'];
  for (const path of paths) {
    await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  }
  const inactive = '/api/v1/executions?projectId=p3&limit=50';
  await f.runtime.queries.fetchQuery(f.options(inactive));
  await f.runtime.checkStatus();
  f.freshness.executions++;
  await Promise.all([f.runtime.checkStatus(), f.runtime.checkStatus()]);
  for (const path of paths) assert.equal(f.counts.get(path), 2);
  assert.equal(f.counts.get(inactive), 1);
  await f.runtime.queries.fetchQuery(f.options(inactive));
  assert.equal(f.counts.get(inactive), 2);
});


test('Server restart invalidates all collection markers even when counters repeat', async t => {
  const f = await fixture(t);
  const paths = ['/api/v1/projects', '/api/v1/workers', '/api/v1/executions?limit=50'];
  for (const path of paths) {
    await f.runtime.queries.fetchQuery(f.options(path)); f.observe(path);
  }
  await f.runtime.checkStatus();
  f.control.startedAtUtc = '2026-01-02T00:00:00Z';
  await f.runtime.checkStatus();
  for (const path of paths) assert.equal(f.counts.get(path), 2);
});
