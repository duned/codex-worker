import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';

const require = createRequire(import.meta.url);
let reads = {}, language = 'en';
async function bundle(path) {
  const result = await build({
    entryPoints: [new URL(path, import.meta.url).pathname], bundle: true, write: false,
    format: 'cjs', platform: 'node', jsx: 'automatic',
    alias: { '@': new URL('../src/untitled', import.meta.url).pathname },
    external: ['react', 'react/jsx-runtime', 'react-dom', 'react-router-dom', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
    plugins: [{ name: 'fixtures', setup(build) {
      build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session', namespace: 'fixture' }));
      build.onResolve({ filter: /useServerReadiness$/ }, () => ({ path: 'readiness', namespace: 'fixture' }));
      build.onResolve({ filter: /(?:^|\/)i18n$/ }, () => ({ path: 'i18n', namespace: 'fixture' }));
      build.onLoad({ filter: /.*/, namespace: 'fixture' }, ({ path }) => ({ contents: path === 'readiness'
        ? 'export function useServerReadiness() { return { inventory: {} }; }'
        : path === 'session' ? `
          export function useApiRead(path) { return globalThis.read(path); }
          export function useRuntime() { return { locked() { return false; } }; }
          export function useSession() { return { authenticated: true, generation: 1 }; }
          export function useApiMutation() { return {}; }
        ` : `
          import { en } from '${new URL('../src/shared/locales/en.ts', import.meta.url).pathname}';
          import { es } from '${new URL('../src/shared/locales/es.ts', import.meta.url).pathname}';
          export function useLanguage() {}
          export function t(key, values = {}) { return ((globalThis.language() === 'es' ? es : en)[key] ?? key).replace(/\\{(\\w+)\\}/g, (_, key) => values[key] ?? key); }
          export function localizeText(value) { return value; }
          export function statusLabel(value) { return value; }
        `, loader: 'js', resolveDir: new URL('../src', import.meta.url).pathname }));
    } }]
  });
  const module = { exports: {} };
  vm.runInNewContext(result.outputFiles[0].text, { module, exports: module.exports, require, URL, Date, setTimeout, clearTimeout,
    language: () => language, read: path => reads[path] ?? { data: [], loading: false, updatedAt: 1 } });
  return module.exports;
}
const homeModel = await bundle('../src/features/home/model.ts');
const observerModel = await bundle('../src/features/observers/model.ts');
const home = await bundle('../src/features/home/HomePage.tsx');
const workers = await bundle('../src/features/workers/WorkersPage.tsx');
const id = 'obs_0123456789abcdef0123456789abcdef';
const observer = { observerId: id, name: 'Standalone north', registrationState: 'reporting', freshness: 'fresh',
  snapshotReceivedAtUtc: '2026-01-01T00:00:00Z', reportedLifecycle: 'running', reportedReadiness: 'not-ready',
  maximumCapacity: 4, activeExecutions: 1, version: '1.2.3' };
const managed = { workerId: 'managed', displayName: 'Managed south', availability: 'online', maximumCapacity: 2, activeExecutions: 1 };
const observation = { observedAtUtc: '2026-01-01T00:00:00Z', receivedAtUtc: '2026-01-01T00:00:00Z',
  execution: { localExecutionId: 'local', project: 'Local only', repository: 'owner/repo', issueNumber: 42, issueTitle: 'Local task',
    stage: 'Implementing', outcome: null, startedAtUtc: '2026-01-01T00:00:00Z', completedAtUtc: null } };
function inventory() {
  return { observers: [observer], executions: [{ observerId: id, observation }], total: 1, active: 1, totalCapacity: 4 };
}
function setup() {
  reads = {
    '/api/v1/workers': { data: [managed] },
    '/api/v1/observers/inventory': { data: inventory() },
    '/api/v1/executions?limit=50&offset=0': { data: [] },
    '/api/v1/projects': { data: [] }
  };
  for (const read of Object.values(reads)) read.updatedAt = 1;
  language = 'en';
}
function render(component, route = '/') {
  return renderToStaticMarkup(createElement(MemoryRouter, { initialEntries: [route] }, createElement(component)));
}
test('mixed worker totals preserve freshness, unknown capacity and all-observer totals', () => {
  let summary = homeModel.homeWorkerSummary([managed], [observer]);
  assert.equal(summary.total, 2);
  assert.equal(summary.active, 2);
  assert.equal(summary.totalSlots, 6);
  summary = homeModel.homeWorkerSummary([managed], [{ ...observer, freshness: 'stale', maximumCapacity: null }]);
  assert.equal(summary.active, 1);
  assert.equal(summary.totalSlots, undefined);
  assert.equal(homeModel.homeWorkerSummary(undefined, []), undefined);
  assert.equal(homeModel.homeWorkerSummary([], undefined), undefined);
  summary = homeModel.homeWorkerSummary([managed], [observer], true, { total: 150, active: 120, totalCapacity: 600 });
  assert.equal(summary.active, undefined);
  assert.equal(summary.total, 151);
  assert.equal(summary.totalSlots, undefined);
});
test('execution projections deduplicate by observer and local identity and retain latest terminal outcomes', () => {
  const data = inventory();
  const terminal = { ...observation, observedAtUtc: '2026-01-02T00:00:00Z', execution: { ...observation.execution, stage: 'Blocked', outcome: 'blocked' } };
  const other = 'obs_1123456789abcdef0123456789abcdef';
  data.observers.push({ ...observer, observerId: other }, { ...observer, observerId: 'revoked', registrationState: 'revoked' });
  data.executions.push({ observerId: id, observation: terminal }, { observerId: other, observation }, { observerId: 'revoked', observation });
  const items = observerModel.observerExecutions(data);
  assert.equal(items.length, 2);
  assert.equal(items[0].observation.execution.stage, 'Blocked');
  assert.equal(observerModel.observerTerminal(items[0].observation.execution), true);
  assert.equal(observerModel.observerTerminal(items[1].observation.execution), false);
});
test('Home and Workers render the same distinct read-only observers alongside managed workers in EN/ES', () => {
  for (const lang of ['en', 'es']) {
    setup(); language = lang;
    for (const component of [home.HomePage, workers.WorkersPage]) {
      const html = render(component);
      assert.match(html, /Standalone north/);
      assert.match(html, /Managed south/);
      assert.match(html, /observer=obs_0123456789abcdef0123456789abcdef/);
      assert.match(html, lang === 'en' ? /Read-only/ : /Solo lectura/);
      assert.match(html, lang === 'en' ? /Standalone/ : /Independiente/);
      assert.match(html, /1.2.3/);
      assert.doesNotMatch(html, /workers\/obs_|projects\/Local/);
    }
    const html = render(home.HomePage);
    assert.match(html, /2 \/ 2/);
    assert.match(html, /1 \/ 6/);
    assert.match(html, /Local task/);
    assert.doesNotMatch(html, /Revoke reporting|Reiniciar|Restart|Drain|Provision/);
  }
});
test('Home terminal reports count once today, preserve outcomes and leave projects unmanaged', () => {
  setup();
  const data = reads['/api/v1/observers/inventory'].data;
  const completed = { ...observation, observedAtUtc: new Date().toISOString(), execution: { ...observation.execution,
    stage: 'Completed', outcome: 'success', completedAtUtc: new Date().toISOString() } };
  data.executions.push({ observerId: id, observation: completed });
  const html = render(home.HomePage);
  assert.equal((html.match(/Local task/g) ?? []).length, 1);
  assert.match(html, /success/);
  assert.match(html, /Recent completed executions.*?1/);
  assert.match(html, /aria-label="Completed today"[\s\S]*?<p[^>]*>1<\/p>/);
  assert.doesNotMatch(html, /projects\/Local/);
});
test('loading, empty, stale and error states do not invent healthy observer status or empty totals', () => {
  setup();
  reads['/api/v1/observers/inventory'] = { loading: true };
  let html = render(home.HomePage);
  assert.match(html, /Loading/);
  assert.doesNotMatch(html, /2 \/ 2/);
  setup();
  reads['/api/v1/observers/inventory'].error = 'unavailable';
  html = render(home.HomePage);
  assert.match(html, /Retained observation/);
  assert.match(html, /Data unavailable/);
  assert.match(html, /Unknown \/ 2/);
  assert.doesNotMatch(html, /aria-label="Running executions"[\s\S]*?<p[^>]*>1 \/ 6<\/p>/);
  setup();
  reads['/api/v1/observers/inventory'].data.observers[0] = { ...observer, freshness: 'stale' };
  assert.match(render(home.HomePage), /Stale/);
  setup();
  reads['/api/v1/workers'].data = [];
  reads['/api/v1/observers/inventory'].data = { observers: [], executions: [], total: 0, active: 0, totalCapacity: 0 };
  assert.match(render(home.HomePage), /No Workers registered/);
  setup();
  reads['/api/v1/observers/inventory'].data.observers[0] = { ...observer, name: null, maximumCapacity: null };
  html = render(home.HomePage);
  assert.match(html, /obs_…89abcdef/);
  assert.match(html, /Unknown/);
});

test('Workers filters keep stale and unknown observers out of connected results and distinguish loading and errors', () => {
  setup();
  let html = render(workers.WorkersPage, '/workers?filter=connected');
  assert.match(html, /Standalone north/);
  reads['/api/v1/observers/inventory'].data.observers[0] = { ...observer, freshness: 'stale' };
  html = render(workers.WorkersPage, '/workers?filter=connected');
  assert.doesNotMatch(html, /Standalone north/);
  html = render(workers.WorkersPage, '/workers?filter=disconnected');
  assert.match(html, /Standalone north/);
  assert.doesNotMatch(html, /Managed south/);
  setup();
  reads['/api/v1/workers'] = { loading: true };
  reads['/api/v1/observers/inventory'] = { loading: true };
  html = render(workers.WorkersPage);
  assert.match(html, /Loading/);
  assert.doesNotMatch(html, /No Workers registered|0 registered workers/);
  reads['/api/v1/observers/inventory'] = { error: 'unavailable' };
  assert.match(render(workers.WorkersPage), /Observations could not be refreshed/);
});

test('Home preserves reported timing, receipt freshness and issue links without a live progress estimate in EN/ES', () => {
  for (const lang of ['en', 'es']) {
    setup(); language = lang;
    const data = reads['/api/v1/observers/inventory'].data;
    data.executions[0].observation = { ...observation, observedAtUtc: '2026-01-01T00:05:00Z', receivedAtUtc: '2026-01-02T00:00:00Z' };
    const html = render(home.HomePage);
    assert.match(html, /https:\/\/github.com\/owner\/repo\/issues\/42/);
    assert.match(html, /Implementing/);
    assert.match(html, /5m 0s/);
    assert.match(html, lang === 'en' ? /Elapsed at report/ : /Tiempo transcurrido al informar/);
    assert.match(html, lang === 'en' ? /Received: 02\/01\/2026/ : /Recibido: 02\/01\/2026/);
    assert.doesNotMatch(html, /executions\/local/);
  }
});
test('terminal observations cannot revert to running, including equal observation timestamps', () => {
  const terminal = { ...observation, execution: { ...observation.execution, stage: 'Blocked', outcome: 'blocked' } };
  const laterRunning = { ...observation, observedAtUtc: '2026-01-02T00:00:00Z' };
  for (const reports of [[observation, terminal, laterRunning], [terminal, laterRunning, observation]]) {
    const data = inventory();
    data.executions = reports.map(observation => ({ observerId: id, observation }));
    assert.equal(observerModel.observerExecutions(data)[0].observation.execution.stage, 'Blocked');
  }
});
test('unknown snapshot freshness remains unknown in Home and Workers in both languages', () => {
  for (const lang of ['en', 'es']) {
    setup(); language = lang;
    reads['/api/v1/observers/inventory'].data.observers[0] = { ...observer, freshness: 'unknown' };
    for (const component of [home.HomePage, workers.WorkersPage]) {
      const html = render(component);
      assert.match(html, lang === 'en' ? /Unknown freshness/ : /Vigencia desconocida/);
      assert.doesNotMatch(html, />Connected<|>Conectado</);
    }
  }
});
test('Workers uses all accepted observer totals while labeling its bounded displayed inventory', () => {
  setup();
  reads['/api/v1/observers/inventory'].data.total = 150;
  const html = render(workers.WorkersPage);
  assert.match(html, /151 registered workers/);
  assert.match(html, /Showing 2 of 151/);
  assert.match(html, /Up to 100 standalone entries shown/);
});
test('failed managed reads retain rows while leaving combined connection and capacity metrics unknown', () => {
  setup();
  reads['/api/v1/workers'].error = 'unavailable';
  const html = render(home.HomePage);
  assert.match(html, /Unknown \/ 2/);
  assert.match(html, /1 \/ Unknown/);
  assert.match(html, /Managed south/);
});

test('terminal observations without completion timing retain an unavailable duration', () => {
  setup();
  reads['/api/v1/observers/inventory'].data.executions[0].observation = {
    ...observation, observedAtUtc: '2026-01-01T00:05:00Z',
    execution: { ...observation.execution, stage: 'Blocked', outcome: 'blocked' }
  };
  const html = render(home.HomePage);
  assert.match(html, /blocked/);
  assert.doesNotMatch(html, /5m 0s/);
  assert.match(html, /Duration unavailable/);
});
