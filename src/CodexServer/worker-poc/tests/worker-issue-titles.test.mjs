import { test } from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';

const require = createRequire(import.meta.url);
const { outputFiles } = await build({
  entryPoints: [new URL('../src/features/workers/WorkerDetailPage.tsx', import.meta.url).pathname], bundle: true, write: false,
  format: 'cjs', platform: 'node', jsx: 'automatic',
  alias: { '@': new URL('../src/untitled', import.meta.url).pathname },
  external: ['react', 'react/jsx-runtime', 'react-dom', 'react-router-dom', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
  plugins: [{ name: 'observations', setup(build) {
    build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session', namespace: 'fixture' }));
    build.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `
      export function useApiRead(path) { return globalThis.read(path); }
      export function useRuntime() { throw Error('Unexpected runtime access'); }
      export function useSession() { throw Error('Unexpected session access'); }
      export function useApiMutation() { throw Error('Unexpected mutation access'); }
    `, loader: 'js' }));
  } }]
});
const module = { exports: {} };
const executions = ['Running', 'Completed'].map((state, index) => ({
  id: `execution-${index}`, projectId: 'project', assignedWorkerId: 'worker', state,
  createdAtUtc: '2026-01-01T00:00:00Z',
  workReference: { type: 'github-issue', id: String(27 + index), title: `Cached title ${index}` }
}));
const reads = [];
const context = { module, exports: module.exports, require, URL, URLSearchParams, read(path) {
  reads.push(path);
  if (path === '/api/v1/workers') return { data: [{ workerId: 'worker', displayName: 'Worker', activeExecutions: 1, maximumCapacity: 2 }] };
  if (path === '/api/v1/projects') return { data: [{ id: 'project', name: 'Project', repository: 'team/repo' }] };
  if (path === '/api/v1/executions?limit=50&offset=0') return { data: executions };
  if (path === '/api/v1/nodes' || path === '/api/v1/nodes/worker/commands') return { data: [] };
  if (path === '/api/v1/workers/worker/diagnostics') return { data: null };
  throw Error(`Unexpected Worker detail request: ${path}`);
} };
vm.runInNewContext(outputFiles[0].text, context);
const render = () => renderToStaticMarkup(createElement(MemoryRouter, null,
  createElement(module.exports.WorkerDetailPage, { workerId: 'worker' })));

test('Worker current and recent executions render cached titles and internal Issue links', () => {
  reads.length = 0;
  const html = render();
  assert.equal(reads.length, 5);
  assert.equal(reads.includes('/api/v1/nodes/worker/commands'), false);
  assert.equal(reads.some(path => path.includes('/issues')), false);
  assert.match(html, /#27 · Cached title 0/);
  assert.match(html, /#28 · Cached title 1/);
  assert.match(html, /href="\/projects\/project\/issues\/27"/);
  assert.match(html, /href="\/projects\/project\/issues\/28"/);
});

test('Worker title cache misses retain Issue numbers without additional requests on refresh', () => {
  for (const item of executions) item.workReference.title = undefined;
  for (let refresh = 0; refresh < 3; refresh++) {
    reads.length = 0;
    const html = render();
    assert.match(html, /#27 · title unavailable/);
    assert.match(html, /#28 · title unavailable/);
    assert.equal(reads.length, 5);
    assert.equal(reads.filter(path => path.includes('/executions')).length, 1);
    assert.equal(reads.some(path => path.includes('/issues')), false);
  }
});
