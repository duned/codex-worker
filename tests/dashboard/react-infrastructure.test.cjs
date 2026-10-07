const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'dashboard-infrastructure-'));
const entry = resolve('src/CodexServer/worker-poc/src/shared');
buildSync({ stdin: { contents: `export * from '${entry}/api/runtime'; export * from '${entry}/api/client'; export * from '${entry}/api/validation'; export * from '${entry}/preferences'; export { QueryObserver } from '@tanstack/react-query';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'infrastructure.cjs') });
const { DashboardRuntime, HttpClient, queryKeys, workers, createPreferences, QueryObserver } = require(join(directory, 'infrastructure.cjs'));
after(() => rmSync(directory, { recursive: true, force: true }));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const flush = () => new Promise(resolve => setImmediate(resolve));
const document = () => ({ csrfToken: 'fixture-csrf', expiresAtUtc: new Date(Date.now() + 3600000).toISOString() });
function setup(handler = async () => new Response('[]')) {
  const calls = [], streamRequests = [];
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (path, options) => {
    calls.push({ path, options });
    if (path === '/api/v1/events/stream') {
      const request = deferred(); streamRequests.push({ ...request, options });
      options.signal.addEventListener('abort', () => request.reject(new DOMException('Aborted', 'AbortError')), { once: true });
      return request.promise;
    }
    if (path === '/api/v1/administration/session' && options.method === 'GET') return Response.json(document());
    return handler(path, options);
  }));
  return { runtime, calls, streamRequests };
}
test('logout cancels reads, clears private cache and fences late bodies across relogin', async () => {
  const read = deferred();
  const s = setup(async (path, options) => options.method === 'DELETE' ? new Response(null, { status: 204 }) : read.promise);
  try {
    await s.runtime.session('GET');
    const generation = s.runtime.snapshot().generation;
    s.runtime.queries.setQueryData(queryKeys.read(generation, '/api/v1/projects'), [{ id: 'private' }]);
    const pending = s.runtime.read('/api/v1/workers', new AbortController().signal, workers);
    const rejected = assert.rejects(pending, { name: 'AbortError' });
    await s.runtime.session('DELETE'); await s.runtime.session('GET');
    read.resolve(Response.json([{ workerId: 'old-private', availability: 'Available' }]));
    await rejected;
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
    assert.equal(s.calls.find(call => call.path === '/api/v1/workers').options.signal.aborted, true);
    assert.equal(s.calls.find(call => call.options.method === 'DELETE').options.headers.get('X-Codex-CSRF'), 'fixture-csrf');
  } finally { s.runtime.dispose(); }
});
test('a superseded sign-in never publishes after logout and tokens remain transient', async () => {
  const signIn = deferred(); const s = setup(async (_path, options) => options.method === 'POST' ? signIn.promise : new Response(null, { status: 204 }));
  try {
    const pending = s.runtime.session('POST', 'fixture-token');
    await s.runtime.session('DELETE'); signIn.resolve(Response.json(document())); await pending;
    assert.equal(s.runtime.snapshot().authenticated, false);
    assert.ok(!JSON.stringify(s.runtime.snapshot()).includes('fixture-token'));
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
  } finally { s.runtime.dispose(); }
});
test('logout response loss preserves only in-memory CSRF for explicit retry', async () => {
  let attempts = 0;
  const s = setup(async () => { if (++attempts === 1) throw new TypeError('private exception'); return new Response(null, { status: 204 }); });
  try {
    await s.runtime.session('GET'); await s.runtime.session('DELETE');
    assert.equal(s.runtime.snapshot().logoutAvailable, true);
    assert.match(s.runtime.snapshot().message, /Retry sign out/);
    await s.runtime.session('DELETE');
    assert.equal(s.runtime.snapshot().logoutAvailable, false);
    assert.deepEqual(s.calls.filter(call => call.options.method === 'DELETE').map(call => call.options.headers.get('X-Codex-CSRF')), ['fixture-csrf', 'fixture-csrf']);
  } finally { s.runtime.dispose(); }
});
test('response loss locks mutations across cache refresh; only explicit successful reconciliation unlocks', async () => {
  const s = setup(async () => { throw new TypeError('private response details'); });
  let checks = 0;
  try {
    await s.runtime.session('GET');
    const submit = () => s.runtime.mutate('worker-a', '/api/v1/workers/worker-a/scheduling', 'PUT', { policy: 'Enabled' }, workers, async () => { checks++; });
    await assert.rejects(submit(), /connection unavailable/);
    assert.equal(s.runtime.locked('worker-a'), true);
    s.runtime.queries.setQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers'), []);
    await assert.rejects(submit(), /Refresh authoritative/);
    await assert.rejects(s.runtime.reconcile('worker-a', async () => { throw Error('read failed'); }));
    assert.equal(s.runtime.locked('worker-a'), true);
    await s.runtime.reconcile('worker-a', async signal => { assert.equal(signal.aborted, false); });
    assert.equal(s.runtime.locked('worker-a'), false);
    assert.equal(checks, 1);
    assert.equal(s.calls.filter(call => call.options.method === 'PUT').length, 1);
  } finally { s.runtime.dispose(); }
});
test('current authoritative checks are required even with cached observations; rejected checks never submit', async () => {
  const s = setup();
  try {
    await s.runtime.session('GET');
    s.runtime.queries.setQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers'), [{ workerId: 'worker-a', canActivate: true }]);
    await assert.rejects(s.runtime.mutate('worker-a', '/api/v1/workers/worker-a', 'PUT', {}, workers, async () => { throw Error('Current readiness unavailable'); }));
    assert.equal(s.runtime.locked('worker-a'), false);
    assert.equal(s.calls.filter(call => call.options.method === 'PUT').length, 0);
  } finally { s.runtime.dispose(); }
});
test('Query deduplicates observers, cancels unused reads, and never retries rejected authorization', async () => {
  const read = deferred(); const s = setup(() => read.promise);
  try {
    await s.runtime.session('GET');
    const options = { queryKey: queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers'), queryFn: ({ signal }) => s.runtime.read('/api/v1/workers', signal, workers) };
    const a = new QueryObserver(s.runtime.queries, options), b = new QueryObserver(s.runtime.queries, options);
    const offA = a.subscribe(() => {}), offB = b.subscribe(() => {});
    assert.equal(s.calls.filter(call => call.path === '/api/v1/workers').length, 1);
    offA(); offB();
    assert.equal(s.calls.find(call => call.path === '/api/v1/workers').options.signal.aborted, true);
    read.resolve(Response.json([])); await flush();
    const rejected = setup(async () => new Response('private challenge', { status: 403 }));
    try {
      await rejected.runtime.session('GET');
      await assert.rejects(rejected.runtime.queries.fetchQuery({ queryKey: queryKeys.read(rejected.runtime.snapshot().generation, '/api/v1/workers'), queryFn: ({ signal }) => rejected.runtime.read('/api/v1/workers', signal, workers) }));
      assert.equal(rejected.runtime.snapshot().authenticated, false);
      assert.equal(rejected.calls.filter(call => call.path === '/api/v1/workers').length, 1);
    } finally { rejected.runtime.dispose(); }
  } finally { s.runtime.dispose(); }
});
test('one stream validates snapshots, releases reader on page suspension and serializes replacements', async () => {
  const s = setup(); let streamController, cancelled = 0;
  try {
    await s.runtime.session('GET'); await flush();
    assert.equal(s.streamRequests.length, 1);
    s.streamRequests[0].resolve(new Response(new ReadableStream({ start(controller) { streamController = controller; }, cancel() { cancelled++; } })));
    await flush();
    streamController.enqueue(new TextEncoder().encode('event: workers\ndata: [{"workerId":"worker-a","availability":"Available"}]\n\n'));
    await flush();
    assert.equal(s.runtime.queries.getQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers'))[0].workerId, 'worker-a');
    assert.equal(s.runtime.snapshot().live, 'Live · connected');
    s.runtime.suspend(); await flush();
    assert.equal(cancelled, 1);
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
    await s.runtime.session('GET'); await flush();
    assert.equal(s.streamRequests.length, 2);
    assert.equal(s.streamRequests[0].options.signal.aborted, true);
  } finally { s.runtime.dispose(); }
});
test('single fallback poll owner ends on suspension; disconnected streams do not cause overlapping reads', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const read = deferred(); const s = setup(() => read.promise);
  try {
    await s.runtime.session('GET');
    const query = new QueryObserver(s.runtime.queries, { queryKey: queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers'), queryFn: ({ signal }) => s.runtime.read('/api/v1/workers', signal, workers) });
    const off = query.subscribe(() => {});
    t.mock.timers.tick(5000); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/v1/workers').length, 1);
    s.runtime.suspend(); read.resolve(Response.json([])); await flush();
    off(); t.mock.timers.tick(10000); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/v1/workers').length, 1);
  } finally { s.runtime.dispose(); t.mock.timers.reset(); }
});
test('safe client diagnostics and response validation never expose response bodies', async () => {
  const http = new HttpClient(async () => new Response('private content', { status: 403, headers: { 'X-Codex-Administration-Error': 'administration-host-mismatch' } }));
  await assert.rejects(http.request('/api/v1/administration/session', { signal: new AbortController().signal, session: true }, workers), /Host mismatch/);
  const invalid = new HttpClient(async () => Response.json([{ workerId: {} }]));
  await assert.rejects(invalid.request('/api/v1/workers', { signal: new AbortController().signal }, workers), /Invalid Server data/);
});
test('preferences reload only versioned theme; corrupt/unavailable storage keeps a usable dark default', () => {
  let saved = null;
  const storage = { getItem: () => saved, setItem: (_key, value) => { saved = value; }, removeItem: () => {} };
  const first = createPreferences(storage); first.getState().setTheme('light');
  assert.deepEqual(JSON.parse(saved), { state: { theme: 'light' }, version: 1 });
  assert.equal(createPreferences(storage).getState().theme, 'light');
  saved = JSON.stringify({ state: { theme: 'light', csrf: 'private', project: 'secret' }, version: 1 });
  assert.deepEqual(Object.keys(createPreferences(storage).getState()).sort(), ['setTheme', 'theme']);
  saved = JSON.stringify({ state: { theme: 'light' }, version: 99 }); assert.equal(createPreferences(storage).getState().theme, 'dark');
  saved = JSON.stringify({ state: { theme: 'light' } }); assert.equal(createPreferences(storage).getState().theme, 'dark');
  saved = '{corrupt'; assert.equal(createPreferences(storage).getState().theme, 'dark');
  const unavailable = createPreferences({ getItem() { throw Error(); }, setItem() { throw Error(); }, removeItem() { throw Error(); } });
  unavailable.getState().setTheme('light'); assert.equal(unavailable.getState().theme, 'light');
});
test('expiration cancels the stream and clears cached observations without automatic sign-in', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const s = setup();
  try {
    await s.runtime.session('GET'); await flush();
    s.runtime.queries.setQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/projects'), ['private']);
    t.mock.timers.tick(3600001); await flush();
    assert.equal(s.runtime.snapshot().authenticated, false);
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
    assert.equal(s.streamRequests[0].options.signal.aborted, true);
    assert.equal(s.calls.filter(call => call.path === '/api/v1/administration/session').length, 1);
  } finally { s.runtime.dispose(); t.mock.timers.reset(); }
});
test('malformed and oversized SSE snapshots become unavailable without leaking event content', async () => {
  for (const event of ['data: {"credential":"private-event"}\n\n', 'x'.repeat(1024 * 1024 + 1)]) {
    const s = setup(); let controller;
    try {
      await s.runtime.session('GET'); await flush();
      s.streamRequests[0].resolve(new Response(new ReadableStream({ start(value) { controller = value; } })));
      await flush(); controller.enqueue(new TextEncoder().encode(event)); await flush();
      assert.match(s.runtime.snapshot().live, /processing failed/);
      assert.ok(!s.runtime.snapshot().live.includes('private-event'));
      assert.equal(s.runtime.queries.getQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers')), undefined);
    } finally { s.runtime.dispose(); }
  }
});
test('Worker stream replaces an older in-flight query and cannot be overwritten by its late response', async () => {
  const read = deferred(), s = setup(() => read.promise); let controller;
  try {
    await s.runtime.session('GET'); await flush();
    const key = queryKeys.read(s.runtime.snapshot().generation, '/api/v1/workers');
    const observer = new QueryObserver(s.runtime.queries, { queryKey: key, queryFn: ({ signal }) => s.runtime.read('/api/v1/workers', signal, workers) });
    const off = observer.subscribe(() => {});
    s.streamRequests[0].resolve(new Response(new ReadableStream({ start(value) { controller = value; } })));
    await flush(); controller.enqueue(new TextEncoder().encode('data: [{"workerId":"current","availability":"online"}]\n\n')); await flush();
    read.resolve(Response.json([{ workerId: 'old', availability: 'offline' }])); await flush();
    assert.equal(s.runtime.queries.getQueryData(key)[0].workerId, 'current'); off();
  } finally { s.runtime.dispose(); }
});
test('SSE authorization rejection clears the session and never reconnects or auto-logins', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] }); const s = setup();
  try {
    await s.runtime.session('GET'); await flush();
    s.streamRequests[0].resolve(new Response(null, { status: 401 })); await flush();
    assert.equal(s.runtime.snapshot().authenticated, false);
    t.mock.timers.tick(10000); await flush();
    assert.equal(s.streamRequests.length, 1);
    assert.equal(s.calls.filter(call => call.path === '/api/v1/administration/session').length, 1);
  } finally { s.runtime.dispose(); t.mock.timers.reset(); }
});

test('a timed-out body is a safe connection failure; caller cancellation stays cancellation', async () => {
  const signal = new AbortController();
  const http = new HttpClient(async () => new Response(new ReadableStream({ start(controller) { controller.error(new DOMException('private timeout', 'TimeoutError')); } })));
  await assert.rejects(http.request('/api/v1/workers', { signal: signal.signal }, workers), /connection unavailable/);
  signal.abort();
  await assert.rejects(http.request('/api/v1/workers', { signal: signal.signal }, workers), { name: 'AbortError' });
});

test('unknown session diagnostics cannot select inherited object properties', async () => {
  const http = new HttpClient(async () => new Response(null, { status: 403, headers: { 'X-Codex-Administration-Error': '__proto__' } }));
  await assert.rejects(http.request('/api/v1/administration/session', { signal: new AbortController().signal, session: true }, workers), /Access rejected without a Server diagnostic/);
});

test('existing public Server status is a narrow local read exception, never a write or arbitrary API path', async () => {
  const calls = [];
  const client = new HttpClient(async (path, options) => { calls.push({ path, method: options.method }); return Response.json({ state: 'running' }); });
  const signal = new AbortController().signal;
  await client.request('/api/status', { signal }, value => value);
  assert.deepEqual(calls, [{ path: '/api/status', method: 'GET' }]);
  for (const path of ['/api/status?redirect=1', '/api/status#fragment', '/api/version', '//external.test/api/status']) {
    await assert.rejects(client.response(path, { signal }), /Unsupported API path/);
  }
  await assert.rejects(client.response('/api/status', { signal, method: 'POST' }), /Unsupported API path/);
  assert.equal(calls.length, 1);
});
