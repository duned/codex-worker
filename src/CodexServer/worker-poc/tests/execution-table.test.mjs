import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const directory = mkdtempSync(join(packageRoot, '.execution-table-test-'));
after(() => rmSync(directory, { recursive: true, force: true }));
const output = join(directory, 'render.cjs');
await build({ stdin: { contents: `
  import React from 'react';
  import { renderToStaticMarkup } from 'react-dom/server';
  import { MemoryRouter } from 'react-router-dom';
  import { ExecutionTable } from './src/features/executions/ExecutionTable';
  export function render(items, projects, workers) {
    return renderToStaticMarkup(React.createElement(MemoryRouter, null,
      React.createElement(ExecutionTable, { items, projects, workers })));
  }
`, resolveDir: packageRoot, sourcefile: 'execution-table-render.tsx' }, bundle: true, platform: 'node', format: 'cjs', packages: 'external', outfile: output, plugins: [{
  name: 'execution-table-api-read-stub',
  setup(build) {
    build.onResolve({ filter: /shared\/api\/session$/ }, () => ({ path: 'session-mock', namespace: 'session-mock' }));
    build.onLoad({ filter: /.*/, namespace: 'session-mock' }, () => ({ contents: 'export function useApiRead() { return { loading: true }; }', loader: 'js' }));
  }
}] });
const { render } = createRequire(import.meta.url)(output);

test('the shared execution table keeps Executions columns, formatting, status and row destinations', () => {
  const item = {
    id: '11111111111111111111111111111111', projectId: 'project one', state: 'Failed',
    createdAtUtc: '2026-01-15T12:34:00.000Z', completedAtUtc: '2026-01-15T13:45:00.000Z',
    assignedWorkerId: 'worker one', workReference: { type: 'github-issue', id: '42' }
  };
  const successfulItem = { ...item, id: '22222222222222222222222222222222', state: 'Completed' };
  const markup = render([successfulItem, item], [{ id: item.projectId, name: 'Example project', repository: 'example/tasks' }],
    [{ workerId: item.assignedWorkerId, displayName: 'Worker One' }]);

  const header = markup.match(/<thead[^>]*>(.*?)<\/thead>/s)?.[1];
  assert.ok(header);
  assert.deepEqual([...header.matchAll(/<th[^>]*>(.*?)<\/th>/gs)].map(([, text]) => text.replace(/<[^>]*>/g, '')),
    ['Exec. ID', 'Project', 'GitHub Issue', 'Created', 'State', 'Finished', 'Worker']);
  assert.match(markup, /href="\/executions\/11111111111111111111111111111111"/);
  assert.match(markup, /href="\/projects\/project%20one"[^>]*>Example project<\/a>/);
  assert.match(markup, /href="https:\/\/github\.com\/example\/tasks\/issues\/42"/);
  assert.match(markup, /href="\/workers\/worker%20one"[^>]*>Worker One<\/a>/);
  assert.match(markup, /aria-label="Created \d{2}\/\d{2}\/2026 at \d{2}:\d{2}"/);
  assert.match(markup, /aria-label="Finished \d{2}\/\d{2}\/2026 at \d{2}:\d{2}"/);
  assert.match(markup, />Completed</);
  assert.match(markup, />Failed</);
  assert.match(markup, /bg-utility-green-50 text-utility-green-700/);
  assert.match(markup, /bg-utility-red-50 text-utility-red-700/);
});

test('Home feeds only its latest-50 completed result set to the shared table and keeps the all-executions link', () => {
  const home = readFileSync(resolve(packageRoot, 'src/features/home/HomePage.tsx'), 'utf8');
  assert.match(home, /useApiRead\('\/api\/v1\/executions\?limit=50&offset=0', executions\)/);
  assert.match(home, /<ExecutionTable items=\{completed\} projects=\{catalog\.data \?\? \[\]\} workers=\{registry\.data \?\? \[\]\}/);
  assert.match(home, /href="\/executions" color="link-color"/);
});
