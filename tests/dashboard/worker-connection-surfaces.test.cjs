const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const path = require('node:path');
const Module = require('node:module');

const frontend = path.resolve(__dirname, '../../src/CodexServer/worker-poc');
const frontendRequire = createRequire(path.join(frontend, 'package.json'));
const { buildSync } = frontendRequire('esbuild');
const source = `
  import { HomePage } from './src/features/home/HomePage';
  import { WorkersPage } from './src/features/workers/WorkersPage';
  import { ProjectDetail } from './src/features/projects/Detail';
  import { WorkerDetail } from './src/detail.jsx';
  export { HomePage, WorkersPage, ProjectDetail, WorkerDetail };
`;
const bundle = buildSync({
  stdin: { contents: source, resolveDir: frontend, sourcefile: 'worker-connectivity-surfaces.tsx', loader: 'tsx' },
  bundle: true,
  write: false,
  platform: 'node',
  format: 'cjs',
  alias: { '@': path.join(frontend, 'src/untitled') },
  external: ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/server', 'react-aria-components', 'react-router-dom', '@untitledui/icons', 'tailwind-merge'],
  plugins: [{
    name: 'dashboard-read-fixtures',
    setup(build) {
      build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session', namespace: 'dashboard-fixture' }));
      build.onResolve({ filter: /server\/useServerReadiness$/ }, () => ({ path: 'server-readiness', namespace: 'dashboard-fixture' }));
      build.onLoad({ filter: /.*/, namespace: 'dashboard-fixture' }, args => ({
        contents: args.path === 'session'
          ? `export function useApiRead(path) { const reads = globalThis.__workerConnectivityReads || {}; return reads[path] || Object.entries(reads).find(([key]) => path.startsWith(key))?.[1] || { data: [], loading: false }; }`
          : `export function useServerReadiness() { const inventory = (globalThis.__workerConnectivityReads || {})['/api/v1/nodes'] || { data: [], loading: false }; return { inventory, connection: { data: undefined }, node: undefined, github: {} }; }`,
        loader: 'js'
      }));
    }
  }]
}).outputFiles[0].text;
const compiled = new Module(path.join(frontend, 'worker-connectivity-surfaces.cjs'));
compiled.paths = Module._nodeModulePaths(frontend);
compiled._compile(bundle, path.join(frontend, 'worker-connectivity-surfaces.cjs'));
const { HomePage, WorkersPage, ProjectDetail, WorkerDetail } = compiled.exports;
const { createElement } = frontendRequire('react');
const { renderToStaticMarkup } = frontendRequire('react-dom/server');
const { MemoryRouter } = frontendRequire('react-router-dom');
const { worker: baseWorker } = require('./worker-poc-fixtures.cjs');

const project = { id: 'project-a', name: 'Sample project', repository: 'owner/repo', defaultBranch: 'main', description: '', requirements: [], revision: 1, enabled: true, issueReadyLabel: 'ready', issueBlockedLabel: 'blocked', automaticDiscovery: { enabled: false } };
function renderScreen(screen, worker, node) {
  const nodes = node ? [{ id: worker.workerId, kind: 'worker', executionReadiness: 'not-ready', capabilities: [], ...node }] : [];
  const reads = {
    '/api/v1/workers': { data: [worker], loading: false },
    '/api/v1/nodes': { data: nodes, loading: false },
    '/api/v1/projects': { data: [project], loading: false },
    '/api/v1/executions': { data: [], loading: false }
  };
  globalThis.__workerConnectivityReads = reads;
  const element = screen === 'worker'
    ? createElement(WorkerDetail, { id: worker.workerId, observations: [worker], nodes, executions: [], projects: [], diagnostics: null, nodeCommands: [], now: Date.parse('2026-01-01T00:02:30Z') })
    : screen === 'project'
      ? createElement(ProjectDetail, { project, onEdit: () => {}, onAction: () => {}, locked: true, listHref: '/projects' })
      : createElement(screen === 'home' ? HomePage : WorkersPage);
  return renderToStaticMarkup(createElement(MemoryRouter, null, element));
}

function hasColoredStatusBadge(html, label, color) {
  let offset = 0;
  while (true) {
    const labelIndex = html.indexOf(label, offset);
    if (labelIndex < 0) return false;
    const badgeStart = html.lastIndexOf('<span class="', labelIndex);
    const badgeEnd = html.indexOf('</span>', labelIndex);
    if (badgeStart >= 0 && badgeEnd > labelIndex && html.slice(badgeStart, badgeEnd).includes(color)) return true;
    offset = labelIndex + label.length;
  }
}

test('Home, Workers list, Project detail, and Worker detail share the same status label and tone', () => {
  const cases = [
    { worker: { ...baseWorker, availability: 'stale' }, node: { connectivity: 'disconnected', observationsStale: true }, label: 'Disconnected', color: 'text-utility-red-500' },
    { worker: { ...baseWorker, availability: 'offline' }, node: { connectivity: 'disconnected', observationsStale: false, executionReadiness: 'ready' }, label: 'Disconnected', color: 'text-utility-red-500' },
    { worker: { ...baseWorker, availability: 'online' }, node: { connectivity: 'connected', observationsStale: true }, label: 'Stale', color: 'text-utility-yellow-500' },
    { worker: { ...baseWorker, availability: 'online' }, node: { connectivity: 'connected', observationsStale: false }, label: 'Connected', color: 'text-utility-green-500' },
    { worker: { ...baseWorker, availability: 'online' }, node: undefined, label: 'Unknown', color: 'text-utility-neutral-500' },
    { worker: { ...baseWorker, availability: 'stale' }, node: undefined, label: 'Unknown', color: 'text-utility-neutral-500' },
    { worker: { ...baseWorker, availability: 'offline' }, node: { connectivity: 'connected', observationsStale: false }, label: 'Unknown', color: 'text-utility-neutral-500' }
  ];
  for (const scenario of cases) {
    for (const screen of ['home', 'workers', 'project', 'worker']) {
      const html = renderScreen(screen, scenario.worker, scenario.node);
      assert.ok(html.includes(scenario.label), `${screen}: expected ${scenario.label}`);
      assert.ok(hasColoredStatusBadge(html, scenario.label, scenario.color), `${screen}: expected ${scenario.color} on ${scenario.label}`);
    }
  }
});
