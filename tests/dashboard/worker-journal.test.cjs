const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const directory = mkdtempSync(join(tmpdir(), 'worker-journal-'));
buildSync({ stdin: { contents: `export { renderToStaticMarkup } from 'react-dom/server'; export { createElement } from 'react'; export * from './src/features/workers/WorkerJournal';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(directory, 'journal.cjs') });
const { renderToStaticMarkup, createElement, JournalContent, journal, journalPath } = require(join(directory, 'journal.cjs'));
after(() => rmSync(directory, { recursive: true, force: true }));
const render = props => renderToStaticMarkup(createElement(JournalContent, props));
test('journal loading, empty, unavailable and error states are readable', () => {
  assert.match(render({}), /Loading recent/);
  assert.match(render({ data: { state: 'ready', lines: [] } }), /No journal entries/);
  assert.match(render({ data: { state: 'unsupported', lines: [] } }), /systemd/);
  assert.match(render({ data: { state: 'unavailable', lines: [] } }), /unavailable/);
  assert.match(render({ error: 'Retrieval failed' }), /Retrieval failed/);
  assert.match(render({ timedOut: true }), /did not respond/);
});
test('journal reads request exactly 100 lines and refresh through the Server request protocol', () => {
  assert.equal(journalPath('worker/a'), '/api/v1/workers/worker%2Fa/journal?lines=100');
  assert.equal(journalPath('worker/a', true), '/api/v1/workers/worker%2Fa/journal?lines=100&refresh=true');
});
test('journal preserves chronological text, wraps, scrolls and escapes markup', () => {
  const html = render({ data: journal({ state: 'ready', lines: ['01 first', '02 <script>'] }) });
  assert.match(html, /01 first\n02 &lt;script&gt;/);
  assert.match(html, /poc-worker-journal-output/);
  assert.throws(() => journal({ state: 'ready', lines: ['x'.repeat(1001)] }));
});
