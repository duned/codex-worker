import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { readFile } from 'node:fs/promises';

const require = createRequire(import.meta.url);
async function bundle(path, plugins = []) {
  const result = await build({ entryPoints: [new URL(path, import.meta.url).pathname], bundle: true, write: false,
    format: 'cjs', platform: 'node', jsx: 'automatic',
    alias: { '@': new URL('../src/untitled', import.meta.url).pathname },
    external: ['react', 'react/jsx-runtime', 'react-dom', 'react-router-dom', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
    plugins });
  const module = { exports: {} };
  vm.runInNewContext(result.outputFiles[0].text, { module, exports: module.exports, require: name => name === 'observer-fixture' ? {
    authenticated: () => authenticated, language: () => language,
    read: path => ({ data: path.includes('/history') ? [] : path.includes('/obs_') ? data && { observer: data[0], currentExecutions: [] } : data, error, refetch() {} })
  } : require(name), URL, Date, setTimeout, clearTimeout });
  return module.exports;
}
const { observerConnection, observerIssueUrl } = await bundle('../src/features/observers/model.ts');
const { readObserverRequest, observerAuthorization } = await bundle('../src/shared/api/observers.ts');
const observer = { observerId: 'obs_0123456789abcdef0123456789abcdef', mode: 'standalone-observer',
  name: 'Observer one', registrationState: 'awaiting-first-report', snapshotReceivedAtUtc: null,
  lastReceivedAtUtc: null, freshness: 'unknown', reportedLifecycle: null, reportedReadiness: null };
test('receipt connectivity stays separate from reported readiness and does not infer offline from stale data', () => {
  assert.equal(observerConnection(observer), 'awaiting');
  assert.equal(observerConnection({ ...observer, lastReceivedAtUtc: '2040-01-01T00:00:00Z' }), 'awaiting');
  const fresh = { ...observer, registrationState: 'reporting', freshness: 'fresh', snapshotReceivedAtUtc: '2040-01-01T00:00:00Z', reportedReadiness: 'not-ready' };
  assert.equal(observerConnection(fresh), 'connected');
  assert.equal(observerConnection({ ...fresh, freshness: 'stale', reportedLifecycle: 'running' }), 'stale');
  assert.equal(observerConnection({ ...fresh, freshness: 'stale', reportedLifecycle: 'stopped' }), 'stale');
  assert.equal(observerConnection({ ...fresh, reportedLifecycle: 'stopped' }), 'offline');
  assert.equal(observerConnection({ ...fresh, registrationState: 'revoked' }), 'revoked');
  assert.equal(observerConnection({ ...observer, registrationState: 'pairing-pending' }), 'pending');
  assert.equal(observerConnection({ ...observer, registrationState: 'pairing-expired' }), 'expired');
});
test('pairing accepts only the public standalone request for this HTTPS Server', () => {
  const request = { contractVersion: 1, mode: observer.mode, observerId: observer.observerId, server: 'https://server.example' };
  assert.equal(readObserverRequest(JSON.stringify(request), request.server).observerId, observer.observerId);
  for (const invalid of [{ ...request, server: 'https://other.example' }, { ...request, mode: 'managed' },
    { ...request, observerId: 'managed-id' }, { ...request, authorization: 'secret' }, { ...request, contractVersion: 2 }]) {
    assert.throws(() => readObserverRequest(JSON.stringify(invalid), request.server));
  }
  assert.throws(() => observerAuthorization({ authorization: 'invalid' }));
  assert.equal(observerIssueUrl('owner/repo', 42), 'https://github.com/owner/repo/issues/42');
  assert.equal(observerIssueUrl('../repo', 42), undefined);
  assert.equal(observerIssueUrl('owner/repo?evil', 42), undefined);
});
let data, language = 'en', error, authenticated = true;
const views = await bundle('../src/features/observers/Observers.tsx', [{
  name: 'fixtures', setup(build) {
    build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session', namespace: 'fixture' }));
    build.onResolve({ filter: /(?:^|\/)i18n$/ }, args => ({ path: 'i18n', namespace: 'fixture' }));
    build.onLoad({ filter: /.*/, namespace: 'fixture' }, args => ({ contents: args.path === 'session' ? `
      export function useApiRead(path) { return require('observer-fixture').read(path); }
      export function useSession() { return { authenticated: require('observer-fixture').authenticated(), generation: 1 }; }
      export function useRuntime() { return { locked() { return false; } }; }
    ` : `
      import { en } from '${new URL('../src/shared/locales/en.ts', import.meta.url).pathname}';
      import { es } from '${new URL('../src/shared/locales/es.ts', import.meta.url).pathname}';
      export function useLanguage() {}
      export function t(key, values = {}) { return ((require('observer-fixture').language() === 'es' ? es : en)[key] ?? key).replace(/\\{(\\w+)\\}/g, (_, key) => values[key] ?? key); }
      export function localizeText(value) { return value; }
    `, loader: 'js', resolveDir: new URL('../src', import.meta.url).pathname }));
  }
}].map(plugin => ({ ...plugin, setup(build) {
  plugin.setup(build);
  build.onResolve({ filter: /^observer-fixture$/ }, () => ({ path: 'observer-fixture', external: true }));
} })));

test('standalone inventory renders read-only status and EN/ES text without managed lifecycle controls', () => {
  for (const lang of ['en', 'es']) {
    language = lang; data = [observer]; error = undefined;
    const html = renderToStaticMarkup(createElement(views.ObserversSection));
    assert.match(html, lang === 'en' ? /Read-only/ : /Solo lectura/);
    assert.match(html, lang === 'en' ? /Paired · awaiting first report/ : /Vinculado · esperando primer informe/);
    assert.match(html, lang === 'en' ? /Add standalone Worker/ : /Añadir Worker independiente/);
    assert.match(html, /observer=obs_0123456789abcdef0123456789abcdef/);
    assert.doesNotMatch(html, />Connected</);
    assert.doesNotMatch(html, />Restart|>Assign|>Provision|>Drain/);
  }
});
test('loading, empty, failed reads and retained observations remain distinct', () => {
  language = 'en'; data = undefined; error = undefined;
  assert.match(renderToStaticMarkup(createElement(views.ObserversSection)), /Loading standalone observations/);
  data = [];
  assert.match(renderToStaticMarkup(createElement(views.ObserversSection)), /No standalone Workers in this page/);
  error = 'unavailable'; data = [{ ...observer, freshness: 'fresh', lastReceivedAtUtc: '2040-01-01T00:00:00Z', snapshotReceivedAtUtc: '2040-01-01T00:00:00Z' }];
  const html = renderToStaticMarkup(createElement(views.ObserversSection));
  assert.match(html, /Observations could not be refreshed/);
  assert.match(html, /Retained observation; current status is unavailable/);
  assert.match(html, /2040/);
  assert.doesNotMatch(html, />Connected</);
  authenticated = false;
  assert.equal(renderToStaticMarkup(createElement(views.ObserversSection)), '');
  authenticated = true; error = undefined;
});
test('observer detail has bounded history, refresh and revocation with no host controls', () => {
  data = [observer];
  const html = renderToStaticMarkup(createElement(views.ObserverDetailPage, { id: observer.observerId }));
  assert.match(html, /Recent execution observations · up to 20/);
  assert.match(html, /Reported current work/);
  assert.match(html, /Revoke reporting/);
  assert.doesNotMatch(html, />Restart|>Assign|>Provision|>Drain/);
});
test('pairing secrets remain transient and never enter browser storage or query cache', async () => {
  const source = await readFile(new URL('../src/features/observers/PairingDialog.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /localStorage|sessionStorage|useApiMutation|setQueryData/);
  assert.match(source, /clipboard.writeText\(secret.authorization\)/);
  assert.match(source, /setTimeout\(\(\) => setSecret\(undefined\)/);
  assert.match(source, /runtime.snapshot\(\).generation === generation/);
});

test('pairing and revocation states render clear next steps in both languages', () => {
  const states = [
    ['pairing-pending', 'Pairing pending', 'Vinculación pendiente'],
    ['pairing-expired', 'Pairing expired / unavailable', 'Vinculación caducada / no disponible'],
    ['revoked', 'Revoked / unavailable', 'Revocado / no disponible']
  ];
  for (const lang of ['en', 'es']) {
    language = lang;
    for (const [registrationState, en, es] of states) {
      data = [{ ...observer, registrationState, pairingExpiresAtUtc: '2040-01-01T00:00:00Z' }];
      const html = renderToStaticMarkup(createElement(views.ObserversSection));
      assert.ok(html.includes(lang === 'en' ? en : es));
      assert.doesNotMatch(html, /href="\/workers\?observer=/);
      if (registrationState === 'revoked') {
        assert.doesNotMatch(html, /Revoke reporting|Revocar informes/);
        assert.match(html, /reporting connect/);
      }
    }
  }
  language = 'en'; data = [observer];
});
