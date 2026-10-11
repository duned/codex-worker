import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';

const bundle = await build({ stdin: { contents: `
  export { saveWorkerSettings } from './src/features/workers/worker-settings';
  export { DashboardRuntime, queryKeys } from './src/shared/api/runtime';
  export { HttpClient, ApiError } from './src/shared/api/client';
`, resolveDir: new URL('..', import.meta.url).pathname }, bundle: true, platform: 'node', format: 'cjs', write: false });
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { saveWorkerSettings, DashboardRuntime, queryKeys, ApiError, HttpClient } = module.exports;
const path = '/api/v1/workers/worker-1';
const original = { workerId: 'worker-1', availability: 'online', displayName: 'Original', maximumCapacity: 3, configurationRevision: 1 };

async function fixture(t, mode = 'success') {
  let current = { ...original };
  const writes = [];
  const json = (value, status = 200) => Response.json(value, { status });
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (url, options) => {
    if (url.endsWith('/administration/session')) return json({ csrfToken: 'test-csrf', expiresAtUtc: new Date(Date.now() + 60_000).toISOString() });
    if (url.endsWith('/events/stream')) return new Response(new ReadableStream());
    if (options.method === 'PUT') {
      assert.equal(options.headers.get('X-Codex-CSRF'), 'test-csrf');
      assert.equal(options.credentials, 'same-origin');
      const request = JSON.parse(options.body);
      writes.push(request);
      if (mode === 'conflict') return json({}, 409);
      current = { ...current, displayName: request.displayName, configuredMaximumCapacity: request.maximumParallelExecutions, configurationRevision: 2 };
      if (mode === 'lost-response') throw new TypeError('Connection lost');
      return json(current);
    }
    return json(mode === 'preflight-conflict' ? { ...current, configurationRevision: 2 } : current);
  }));
  t.after(() => runtime.dispose());
  await runtime.session('GET');
  const generation = runtime.snapshot().generation;
  runtime.queries.setQueryData(queryKeys.read(generation, path), original);
  runtime.queries.setQueryData(queryKeys.read(generation, '/api/v1/workers'), [original, { ...original, workerId: 'other' }]);
  return { runtime, generation, writes };
}

test('editor save sends both settings and revision with CSRF, updates both caches and reads saved state', async t => {
  const { runtime, generation, writes } = await fixture(t);
  const saved = await saveWorkerSettings(runtime, 'worker-1', 'Operations', 1, 1);
  assert.deepEqual(writes, [{ displayName: 'Operations', maximumParallelExecutions: 1, expectedRevision: 1 }]);
  assert.equal(saved.maximumCapacity, 3);
  assert.equal(saved.configurationRevision, 2);
  assert.deepEqual(runtime.queries.getQueryData(queryKeys.read(generation, path)), saved);
  const list = runtime.queries.getQueryData(queryKeys.read(generation, '/api/v1/workers'));
  assert.deepEqual(list[0], saved);
  assert.equal(list[1].displayName, 'Original');
  assert.deepEqual(await runtime.read(path, new AbortController().signal, value => value), saved);
});

for (const mode of ['conflict', 'preflight-conflict', 'lost-response']) {
  test(`editor ${mode} preserves cached settings and requires reconciliation for submitted failures`, async t => {
    const { runtime, generation, writes } = await fixture(t, mode);
    await assert.rejects(saveWorkerSettings(runtime, 'worker-1', 'Edits retained', 1, 1), error => error instanceof ApiError && (mode !== 'conflict' || error.status === 409));
    assert.equal(writes.length, mode === 'preflight-conflict' ? 0 : 1);
    assert.deepEqual(runtime.queries.getQueryData(queryKeys.read(generation, path)), original);
    assert.equal(runtime.locked(path), mode !== 'preflight-conflict');
    await runtime.reconcile(path, signal => runtime.read(path, signal, value => value).then(() => {}));
    assert.equal(runtime.locked(path), false);
  });
}
