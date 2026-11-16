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
  entryPoints: [new URL('../src/features/home/HomePage.tsx', import.meta.url).pathname], bundle: true, write: false,
  format: 'cjs', platform: 'node', jsx: 'automatic',
  alias: { '@': new URL('../src/untitled', import.meta.url).pathname },
  external: ['react', 'react/jsx-runtime', 'react-dom', 'react-router-dom', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
  plugins: [{ name: 'observations', setup(build) {
    build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session', namespace: 'fixture' }));
    build.onResolve({ filter: /useServerReadiness$/ }, () => ({ path: 'readiness', namespace: 'fixture' }));
    build.onLoad({ filter: /.*/, namespace: 'fixture' }, ({ path }) => ({ contents: path === 'readiness'
      ? 'export function useServerReadiness() { return { inventory: {} }; }'
      : 'export function useApiRead(path) { return globalThis.read(path); }', loader: 'js' }));
  } }]
});
const module = { exports: {} };
const execution = { id: 'execution', projectId: 'project', state: 'Running', createdAtUtc: '2026-01-01T00:00:00Z', workReference: { type: 'github-issue', id: '27', title: 'Cached Home title' } };
const context = { module, exports: module.exports, require, URL, read(path) {
  if (path.includes('/issues/')) throw Error('Home must not fetch Issues');
  return { data: path.includes('/executions') ? [execution] : path.includes('/projects') ? [{ id: 'project', name: 'Project', repository: 'team/repo', enabled: true }] : [], loading: false };
} };
vm.runInNewContext(outputFiles[0].text, context);
const render = () => renderToStaticMarkup(createElement(MemoryRouter, null, createElement(module.exports.HomePage)));
test('Home current work and recent project activity render the supplied title without Issue reads', () => {
  const html = render();
  assert.equal((html.match(/#27 · Cached Home title/g) ?? []).length, 2);
  assert.match(html, /href="\/projects\/project\/issues\/27"/);
});
test('Home retains the shared unavailable-title fallback', () => {
  execution.workReference.title = undefined;
  assert.equal((render().match(/#27 · title unavailable/g) ?? []).length, 2);
});
