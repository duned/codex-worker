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
    contents: "export { IssueTitle } from './src/features/projects/IssueTitle.tsx';",
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
  external: ['react', 'react/jsx-runtime', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'],
  plugins: [{
    name: 'mock-issue-title-read',
    setup(build) {
      build.onResolve({ filter: /^\.\.\/\.\.\/shared\/api\/session$/ }, () => ({ path: 'session-mock', namespace: 'test' }));
      build.onLoad({ filter: /.*/, namespace: 'test' }, () => ({
        contents: 'export function useApiRead() { return globalThis.__issueTitleRead ?? {}; }',
        loader: 'js'
      }));
    }
  }]
});
const output = outputFiles[0].text;

const moduleResult = { exports: {} };
const context = { module: moduleResult, exports: moduleResult.exports, require: frontendRequire, URL };
vm.runInNewContext(output, context);
const { IssueTitle } = moduleResult.exports;
const workReference = { type: 'github-issue', id: '27', url: 'https://github.com/owner/repo/issues/27' };
const render = query => {
  context.__issueTitleRead = query;
  return renderToStaticMarkup(createElement(IssueTitle, {
    projectId: 'project-a', repository: 'owner/repo', workReference
  }));
};

test('running Issue title shows the title, Issue number, and existing GitHub URL', () => {
  const html = render({ data: { number: 27, title: 'Keep execution progress visible' } });

  assert.match(html, /href="https:\/\/github\.com\/owner\/repo\/issues\/27"/);
  assert.match(html, /#27 · Keep execution progress visible/);
});

test('missing or blank Issue titles keep the Issue number and localized fallback', () => {
  for (const query of [
    { error: 'Issue read unavailable' },
    { data: { number: 27, title: '   ' } }
  ]) {
    const html = render(query);
    assert.match(html, /#27 · title unavailable/);
    assert.match(html, /href="https:\/\/github\.com\/owner\/repo\/issues\/27"/);
  }
});
