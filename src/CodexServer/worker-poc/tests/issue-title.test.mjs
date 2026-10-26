import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const frontend = path.resolve(import.meta.dirname, '..');
const frontendRequire = createRequire(path.join(frontend, 'package.json'));
const { build } = frontendRequire('esbuild');
const { createElement } = frontendRequire('react');
const { MemoryRouter } = frontendRequire('react-router-dom');
const { renderToStaticMarkup } = frontendRequire('react-dom/server');
const { outputFiles } = await build({
  stdin: {
    contents: "export { IssueTitle } from './src/features/projects/IssueTitle.tsx'; export { issueDetailRoute, issueLink } from './src/model.js';",
    resolveDir: frontend,
    sourcefile: 'issue-title-test.tsx',
    loader: 'tsx'
  },
  bundle: true,
  write: false,
  format: 'cjs',
  platform: 'node',
  jsx: 'automatic',
  alias: { '@': path.join(frontend, 'src/untitled') },
  external: ['react-router-dom', 'react', 'react/jsx-runtime', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
  plugins: [{
    name: 'mock-issue-title-read',
    setup(build) {
      build.onResolve({ filter: /^\.\.\/\.\.\/shared\/api\/session$/ }, () => ({ path: 'session-mock', namespace: 'test' }));
      build.onLoad({ filter: /.*/, namespace: 'test' }, () => ({
        contents: 'export function useApiRead() { throw Error("Execution titles must not fetch Issues"); }',
        loader: 'js'
      }));
    }
  }]
});
const output = outputFiles[0].text;

const moduleResult = { exports: {} };
const context = { module: moduleResult, exports: moduleResult.exports, require: frontendRequire, URL };
vm.runInNewContext(output, context);
const { IssueTitle, issueDetailRoute, issueLink } = moduleResult.exports;
const workReference = { type: 'github-issue', id: '27', url: 'https://github.com/owner/repo/issues/27' };
const render = (query, projectId = 'project-a') => {
  context.__issueTitleRead = query;
  return renderToStaticMarkup(createElement(MemoryRouter, null, createElement(IssueTitle, {
    projectId, repository: 'owner/repo', workReference: { ...workReference, title: query.data?.title }
  })));
};

test('running Issue title shows the title, Issue number, and internal Issue URL', () => {
  const html = render({ data: { number: 27, title: 'Keep execution progress visible' } });

  assert.match(html, /href="\/projects\/project-a\/issues\/27"/);
  assert.match(html, /#27 · Keep execution progress visible/);
});

test('missing or blank Issue titles keep the Issue number and localized fallback', () => {
  for (const query of [
    { error: 'Issue read unavailable' },
    { data: { number: 27, title: '   ' } }
  ]) {
    const html = render(query);
    assert.match(html, /#27 · title unavailable/);
    assert.match(html, /href="\/projects\/project-a\/issues\/27"/);
  }
});

test('Issue routes use encoded project identity and reject missing or invalid identity', () => {
  assert.equal(issueDetailRoute('project / a', 27), '/projects/project%20%2F%20a/issues/27');
  assert.notEqual(issueLink(workReference, 'project-a'), issueLink(workReference, 'project-b'));
  for (const projectId of [undefined, null, '', '   ']) assert.equal(issueLink(workReference, projectId), null);
  for (const number of [undefined, null, 0, -1, '01', 'invalid', 2147483648]) assert.equal(issueDetailRoute('project-a', number), null);
  assert.equal(issueLink({ type: 'other', id: '27' }, 'project-a'), null);
});

test('the same Issue number renders a distinct destination for each configured project', () => {
  const html = render({ data: { number: 27, title: 'Another repository' } }, 'project-b');
  assert.match(html, /href="\/projects\/project-b\/issues\/27"/);
  assert.doesNotMatch(html, /github.com|target="_blank"/);
});

test('missing project identity renders unavailable text without an external fallback', () => {
  const html = render({}, '');
  assert.match(html, /#27 · title unavailable/);
  assert.doesNotMatch(html, /href=/);
});

test('repeated and concurrent execution list rendering never requests Issue details', async () => {
  for (let refresh = 0; refresh < 5; refresh++) {
    await Promise.all(Array.from({ length: 50 }, () => Promise.resolve().then(() => {
      assert.match(render({ data: { title: 'Snapshot title' } }), /Snapshot title/);
    })));
  }
});
