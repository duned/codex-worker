const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'dashboard-infrastructure-'));
const entry = resolve('src/CodexServer/worker-poc/src/shared');
buildSync({ stdin: { contents: `export * from '${entry}/api/runtime'; export * from '${entry}/api/client'; export * from '${entry}/api/validation'; export * from '${entry}/preferences'; export * from '${entry}/../app/routes'; export { QueryObserver } from '@tanstack/react-query';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'infrastructure.cjs') });
const { DashboardRuntime, HttpClient, queryKeys, workers, createPreferences, QueryObserver, normalizeBookmark } = require(join(directory, 'infrastructure.cjs'));
after(() => rmSync(directory, { recursive: true, force: true }));
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const flush = () => new Promise(resolve => setImmediate(resolve));
const document = () => ({ csrfToken: 'fixture-csrf', expiresAtUtc: new Date(Date.now() + 3600000).toISOString() });
function setup(handler = async () => new Response('[]')) {
  const calls = [];
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (path, options) => {
    calls.push({ path, options });
    if (path === '/api/v1/administration/session' && options.method === 'GET') return Response.json(document());
    return handler(path, options);
  }));
  // Test cleanup owns cache lifetime; cancelled queries must not retain Node timers.
  runtime.queries.setQueryDefaults(['private'], { gcTime: Infinity });
  return { runtime, calls };
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
test('status polling stops on suspension and resumes with an explicit session restore', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const s = setup(async () => Response.json({ state: 'ready', startedAtUtc: '2026-01-01T00:00:00Z', persistenceAvailable: true, freshness: { projects: 1, workers: 1, executions: 1 } }));
  try {
    await s.runtime.session('GET');
    t.mock.timers.tick(0); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/status').length, 1);
    assert.equal(s.runtime.snapshot().live, 'Live · connected');
    s.runtime.suspend();
    t.mock.timers.tick(10000); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/status').length, 1);
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
    await s.runtime.session('GET');
    t.mock.timers.tick(0); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/status').length, 2);
    assert.equal(s.calls.filter(call => call.path === '/api/v1/events/stream').length, 0);
  } finally { s.runtime.dispose(); t.mock.timers.reset(); }
});
test('safe client diagnostics and response validation never expose response bodies', async () => {
  const http = new HttpClient(async () => new Response('private content', { status: 403, headers: { 'X-Codex-Administration-Error': 'administration-host-mismatch' } }));
  await assert.rejects(http.request('/api/v1/administration/session', { signal: new AbortController().signal, session: true }, workers), /Host mismatch/);
  const invalid = new HttpClient(async () => Response.json([{ workerId: {} }]));
  await assert.rejects(invalid.request('/api/v1/workers', { signal: new AbortController().signal }, workers), /Invalid Server data/);
});
test('preferences reload only versioned theme and language; corrupt/unavailable storage keeps a usable dark default', () => {
  let saved = null;
  const storage = { getItem: () => saved, setItem: (_key, value) => { saved = value; }, removeItem: () => {} };
  const first = createPreferences(storage); first.getState().setTheme('light');
  assert.deepEqual(JSON.parse(saved), { state: { theme: 'light', language: 'en' }, version: 1 });
  assert.equal(createPreferences(storage).getState().theme, 'light');
  saved = JSON.stringify({ state: { theme: 'light', csrf: 'private', project: 'secret' }, version: 1 });
  assert.deepEqual(Object.keys(createPreferences(storage).getState()).sort(), ['language', 'setLanguage', 'setTheme', 'theme']);
  saved = JSON.stringify({ state: { theme: 'light' }, version: 99 }); assert.equal(createPreferences(storage).getState().theme, 'dark');
  saved = JSON.stringify({ state: { theme: 'light' } }); assert.equal(createPreferences(storage).getState().theme, 'dark');
  saved = '{corrupt'; assert.equal(createPreferences(storage).getState().theme, 'dark');
  const unavailable = createPreferences({ getItem() { throw Error(); }, setItem() { throw Error(); }, removeItem() { throw Error(); } });
  unavailable.getState().setTheme('light'); assert.equal(unavailable.getState().theme, 'light');
});
test('expiration cancels status polling and clears cached observations without automatic sign-in', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const s = setup();
  try {
    await s.runtime.session('GET'); await flush();
    s.runtime.queries.setQueryData(queryKeys.read(s.runtime.snapshot().generation, '/api/v1/projects'), ['private']);
    t.mock.timers.tick(3600001); await flush();
    assert.equal(s.runtime.snapshot().authenticated, false);
    assert.equal(s.runtime.queries.getQueryCache().getAll().length, 0);
    const count = s.calls.length;
    t.mock.timers.tick(10000); await flush();
    assert.equal(s.calls.length, count);
    assert.equal(s.calls.filter(call => call.path === '/api/v1/administration/session').length, 1);
  } finally { s.runtime.dispose(); t.mock.timers.reset(); }
});
test('status authorization rejection clears the session and never reconnects or auto-logins', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const s = setup(async () => new Response(null, { status: 401 }));
  try {
    await s.runtime.session('GET');
    t.mock.timers.tick(0); await flush();
    assert.equal(s.runtime.snapshot().authenticated, false);
    t.mock.timers.tick(10000); await flush();
    assert.equal(s.calls.filter(call => call.path === '/api/status').length, 1);
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
  for (const path of ['/api/status?redirect=1', '/api/status#fragment', '/api/version?redirect=1', '//external.test/api/status']) {
    await assert.rejects(client.response(path, { signal }), /Unsupported API path/);
  }
  await assert.rejects(client.response('/api/status', { signal, method: 'POST' }), /Unsupported API path/);
  assert.equal(calls.length, 1);
});

test('pinned enrollment release uses the existing public version GET without broadening public API writes', async () => {
  const http = new HttpClient(async () => Response.json({ version: '0.15.0' }));
  const signal = new AbortController().signal;
  assert.deepEqual(await http.request('/api/version', { signal }, value => value), { version: '0.15.0' });
  await assert.rejects(http.request('/api/version', { signal, method: 'POST' }, value => value), /Unsupported API path/);
});

test('canonical root and supported hash bookmarks normalize once without accepting foreign or unknown routes', () => {
  assert.equal(normalizeBookmark('/', '?node=server', ''), '/home?node=server');
  assert.equal(normalizeBookmark('/home', '', '#/projects/project-a?issue=27&label=review'), '/projects/project-a?issue=27&label=review');
  assert.equal(normalizeBookmark('/workers/a', '?step=project', ''), null);
  for (const hash of ['#/api/v1/workers', '#/home/a', '#/projects/a/extra', '#//evil.test/projects', '#/settings/a#extra', '#//[invalid'])
    assert.equal(normalizeBookmark('/home', '', hash), null);
});
