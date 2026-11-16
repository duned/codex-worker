import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const frontend = path.resolve(import.meta.dirname, '..');
const frontendRequire = createRequire(path.join(frontend, 'package.json'));
const { build } = frontendRequire('esbuild');
const { createElement } = frontendRequire('react');
const { renderToStaticMarkup } = frontendRequire('react-dom/server');
const { outputFiles } = await build({
  stdin: {
    contents: "export { IssueDetailLayout } from './src/features/projects/IssueDetailLayout.tsx'; export { IssueDetailsPanel } from './src/features/projects/IssueDetailsPanel.tsx';",
    resolveDir: frontend,
    sourcefile: 'issue-detail-layout-test.tsx',
    loader: 'tsx'
  },
  bundle: true,
  write: false,
  format: 'cjs',
  platform: 'node',
  jsx: 'automatic',
  alias: { '@': path.join(frontend, 'src/untitled') },
  external: ['react', 'react/jsx-runtime', 'react-aria-components', '@untitledui/icons', 'tailwind-merge']
});
const moduleResult = { exports: {} };
vm.runInNewContext(outputFiles[0].text, { module: moduleResult, exports: moduleResult.exports, require: frontendRequire, URL });
const { IssueDetailLayout, IssueDetailsPanel } = moduleResult.exports;

const longText = 'long-metadata-value-'.repeat(8);
const issue = {
  number: 42,
  title: 'A responsive Issue',
  state: 'OPEN',
  url: 'https://github.com/example/project/issues/42',
  body: '',
  createdAtUtc: '2026-10-01T12:00:00Z',
  updatedAtUtc: '2026-10-02T12:00:00Z',
  author: longText,
  assignees: [longText],
  milestone: longText,
  labels: [longText],
  blockedBy: [],
  isEligible: true,
  eligibilityReasons: []
};

const renderPanel = overrides => renderToStaticMarkup(createElement(IssueDetailsPanel, {
  issue,
  project: { name: longText },
  projectId: 'project-a',
  issueError: undefined,
  projectError: undefined,
  onRetryProject: () => {},
  ...overrides
}));

test('Issue detail layout keeps the description in the main column and stacks status before details', () => {
  const html = renderToStaticMarkup(createElement(IssueDetailLayout, {
    description: createElement('section', null, 'Main Issue description'),
    status: createElement('aside', null, createElement('h2', null, 'Issue status')),
    details: createElement('section', null, createElement('h2', null, 'Issue details'))
  }));

  assert.ok(html.includes('lg:grid-cols-[minmax(0,1.7fr)_minmax(18rem,1fr)]'));
  assert.ok(html.includes('flex min-w-0 flex-col gap-4'));
  assert.ok(html.indexOf('Main Issue description') < html.indexOf('Issue status'));
  assert.ok(html.indexOf('Issue status') < html.indexOf('Issue details'));
});

test('Issue Details uses a narrow-column metadata stack and wraps long values, labels, and Issue labels', () => {
  const html = renderPanel({});

  assert.ok(html.includes('grid min-w-0 grid-cols-1 gap-y-3'));
  assert.ok(html.includes('break-words font-medium text-secondary [overflow-wrap:anywhere]'));
  assert.ok(html.includes('break-words text-xs text-tertiary [overflow-wrap:anywhere]'));
  assert.ok(html.includes('whitespace-normal break-words [overflow-wrap:anywhere]'));
  assert.ok(html.includes(longText));
  assert.ok(html.includes('Issue details'));
  assert.ok(html.includes('Opened by'));
  assert.ok(html.includes('Assignees'));
  assert.ok(html.includes('Milestone'));
  assert.ok(html.includes('Project'));
  assert.ok(html.includes('Labels'));
  assert.ok(!html.includes('lg:grid-cols-4'));
});

test('Issue Details keeps loading and unavailable metadata states visible', () => {
  assert.ok(renderPanel({ issue: undefined }).includes('Loading GitHub Issue'));
  assert.ok(renderPanel({ issue: undefined, issueError: new Error('read failed') }).includes('Issue metadata is unavailable until GitHub can be reached.'));
});
