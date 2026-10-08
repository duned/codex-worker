const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const path = require('node:path');
const vm = require('node:vm');
const frontend = path.resolve('src/CodexServer/worker-poc');
const requireFrontend = createRequire(path.join(frontend, 'package.json'));
const { buildSync } = requireFrontend('esbuild');
const { createElement } = requireFrontend('react');
const { renderToStaticMarkup } = requireFrontend('react-dom/server');
function load(file) {
  const output = buildSync({ entryPoints: [path.join(frontend, 'src/shared', file)], bundle: true, write: false, format: 'cjs', platform: 'node', jsx: 'automatic', alias: { '@': path.join(frontend, 'src/untitled') }, external: ['react', 'react/jsx-runtime', 'react-aria-components', '@untitledui/icons', 'tailwind-merge'] }).outputFiles[0].text;
  const module = { exports: {} };
  vm.runInNewContext(output, { module, exports: module.exports, require: requireFrontend, URL, AbortController, setTimeout, clearTimeout });
  return module.exports;
}
const render = (component, props) => renderToStaticMarkup(createElement(component, props));
test('shared actions retain text, disabled deletion and safe external destination behavior', () => {
  const { DeleteAction, ExternalLink } = load('Actions.tsx');
  const deletion = render(DeleteAction, { children: 'Delete project', isDisabled: true });
  assert.match(deletion, /disabled/); assert.match(deletion, /Delete project/); assert.match(deletion, /<svg/);
  const link = render(ExternalLink, { href: 'https://github.com/owner/repo', children: 'Repository' });
  for (const text of ['target="_blank"', 'noopener noreferrer', 'opens in new window', 'Repository', '<svg']) assert.ok(link.includes(text));
});
test('Server status separates live, offline, interrupted, error and unknown observations', () => {
  const { ServerStatusView } = load('ServerStatus.tsx');
  const data = { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' };
  const updatedAt = Date.parse('2026-10-01T12:00:00Z');
  const show = props => render(ServerStatusView, { data, live: 'Live · connected', updatedAt, ...props });
  assert.match(show({}), /Server live/);
  assert.match(show({ data: { ...data, state: 'offline' } }), /Server offline/);
  assert.match(show({ live: 'Live · connection interrupted; retrying' }), /live updates unavailable/);
  const error = show({ data: undefined, error: 'Transport unavailable' });
  assert.match(error, /refresh failed/); assert.match(error, /2026-10-01T12:00:00.000Z/); assert.match(error, /stale/); assert.ok(!error.includes('Server offline'));
  const unknown = show({ data: undefined, updatedAt: 0 });
  assert.match(unknown, /Server status unknown/); assert.match(unknown, /Last updated: Unknown/); assert.ok(!unknown.includes('<time'));
});
test('theme control exposes an icon and action label and preference persists explicit choices', () => {
  const { ThemeControl } = load('ThemeControl.tsx');
  assert.match(render(ThemeControl, {}), /Use light mode/);
  assert.match(render(ThemeControl, {}), /<svg/);
  const { createPreferences } = load('preferences.ts');
  let saved = null;
  const storage = { getItem: () => saved, setItem: (_, value) => { saved = value; }, removeItem: () => { saved = null; } };
  const preferences = createPreferences(storage);
  assert.equal(preferences.getState().theme, 'dark');
  preferences.getState().setTheme('light');
  assert.equal(createPreferences(storage).getState().theme, 'light');
  preferences.getState().setTheme('dark');
  assert.equal(createPreferences(storage).getState().theme, 'dark');
});
