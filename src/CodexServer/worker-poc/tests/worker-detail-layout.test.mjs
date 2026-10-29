import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import vm from 'node:vm';
import { build } from 'esbuild';
import { Children, cloneElement, createElement, Fragment } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';

const require = createRequire(import.meta.url);
const { outputFiles } = await build({
  entryPoints: [new URL('../src/detail.jsx', import.meta.url).pathname],
  bundle: true,
  write: false,
  format: 'cjs',
  platform: 'node',
  jsx: 'automatic',
  alias: { '@': new URL('../src/untitled', import.meta.url).pathname },
  external: ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/server', 'react-router-dom', 'react-aria-components', '@untitledui/icons', 'tailwind-merge']
});
const module = { exports: {} };
vm.runInNewContext(outputFiles[0].text, { module, exports: module.exports, require, URL, URLSearchParams });
const { WorkerDetail } = module.exports;
const openModule = { exports: {} };
const ariaComponents = require('react-aria-components');
const openStateComponents = {
  ...ariaComponents,
  MenuTrigger: ({ children }) => {
    const [trigger, content] = Children.toArray(children);
    return createElement(Fragment, null, cloneElement(trigger, { 'aria-expanded': true }), content);
  },
  Popover: ({ children, className }) => createElement('div', { className }, children),
  Menu: ({ children, 'aria-label': label }) => createElement('div', { role: 'menu', 'aria-label': label }, children),
  MenuSection: ({ children, 'aria-label': label }) => createElement('section', { 'aria-label': label }, children),
  Header: ({ children }) => createElement('h3', null, children),
  MenuItem: props => createElement('button', {
    id: props.id,
    role: 'menuitem',
    disabled: props.isDisabled,
    'aria-describedby': props['aria-describedby']
  }, props.children)
};
vm.runInNewContext(outputFiles[0].text, {
  module: openModule,
  exports: openModule.exports,
  require: specifier => specifier === 'react-aria-components' ? openStateComponents : require(specifier),
  URL,
  URLSearchParams
});
const { WorkerDetail: OpenWorkerDetail } = openModule.exports;

const now = Date.parse('2026-10-29T12:00:00.000Z');
const worker = {
  workerId: 'worker-1', displayName: 'Build worker', workerVersion: '0.29.7', platform: 'Ubuntu 24.04',
  availability: 'online', schedulingPolicy: 'Enabled', lastHeartbeatAtUtc: new Date(now - 18_000).toISOString(),
  activeExecutions: 0, activeAssignments: 0, maximumCapacity: 4, capacity: 4, availableCapacity: 4, capabilities: [],
  hostResources: { measuredAtUtc: new Date(now - 10_000).toISOString(), logicalCpuCount: 2 }
};
const node = { id: worker.workerId, kind: 'worker', connectivity: 'connected', executionReadiness: 'ready', observationsStale: false, capabilities: [] };
const diagnostics = {
  aiAgentReady: true, gitHubReady: true, gitReady: true, configurationSynchronization: 'synchronized',
  provisioningState: 'not-required', projects: []
};
const execution = {
  id: 'execution-1', projectId: 'project-1', assignedWorkerId: worker.workerId, state: 'Completed', createdAtUtc: '2026-10-29T11:00:00Z',
  startedAtUtc: '2026-10-29T11:00:00Z', completedAtUtc: '2026-10-29T11:01:00Z', durationMilliseconds: 60_000,
  workReference: { type: 'github-issue', id: '42', title: 'Use the cached Issue title' }
};
const actions = [
  { key: 'Enabled', label: 'Activate scheduling' }, { key: 'Draining', label: 'Drain worker' },
  { key: 'Disabled', label: 'Deactivate' }, { key: 'revoke-api', label: 'Revoke Worker API token' }
];
function props(overrides = {}) {
  const currentWorker = { ...worker, ...(overrides.worker ?? {}) };
  const currentDiagnostics = overrides.diagnostics === undefined ? diagnostics : overrides.diagnostics;
  const administration = overrides.administration === undefined ? {
    worker: currentWorker, actions: overrides.actions ?? actions, onAction() {}, onRefresh() {}
  } : overrides.administration;
  return {
    id: worker.workerId, observations: [currentWorker], nodes: [node], diagnostics: currentDiagnostics,
    executions: [execution], projects: [{ id: 'project-1', name: 'Project one', repository: 'team/repo' }],
    administration, now, readOnly: false,
    restart: overrides.restart ?? (administration ? createElement('div', null, 'Restart worker Check restart status This reads the existing result and current service status. It does not send another restart.') : undefined),
    onRefreshStatus: overrides.onRefreshStatus ?? (administration ? () => Promise.resolve() : undefined),
    statusUpdatedAt: now,
    statusMessage: overrides.statusMessage ?? null
  };
}
function render(overrides = {}) {
  return renderToStaticMarkup(createElement(MemoryRouter, null, createElement(WorkerDetail, props(overrides))));
}
function renderOpenMenu(overrides = {}) {
  return renderToStaticMarkup(createElement(MemoryRouter, null, createElement(OpenWorkerDetail, props(overrides))));
}

test('operational cards follow the approved order and keep known slot counts', () => {
  const html = render();
  const labels = ['Connection', 'Scheduling', 'Execution readiness', 'Managed settings', 'Work slots'];
  const positions = labels.map(label => html.indexOf(`>${label}</span>`));
  assert.ok(positions.every(position => position >= 0));
  assert.deepEqual(positions, [...positions].sort((left, right) => left - right));
  assert.match(html, /0 \/ 4/);
  assert.match(html, /4 available slots/);
});

test('heartbeat waveform follows the one-minute and five-minute boundaries and explains lost data', () => {
  for (const [age, tone] of [[59_999, 'success'], [60_000, 'warning'], [299_999, 'warning'], [300_000, 'error']]) {
    const html = render({ worker: { lastHeartbeatAtUtc: new Date(now - age).toISOString() } });
    assert.match(html, new RegExp(`poc-heartbeat-waveform tone-${tone}`));
  }
  const missing = render({ worker: { lastHeartbeatAtUtc: undefined } });
  assert.match(missing, /poc-heartbeat-waveform tone-error/);
  assert.match(missing, />Lost</);
  assert.match(missing, /heartbeat unavailable/);
});

test('managed settings distinguish synchronized and standalone observations', () => {
  assert.match(render(), />Synced</);
  const standalone = render({ diagnostics: { ...diagnostics, configurationSynchronization: 'unknown' } });
  assert.match(standalone, />Standalone</);
  assert.doesNotMatch(standalone, />Synced</);
  assert.doesNotMatch(standalone, /Reload worker status|Restart worker/);
});

test('secondary worker controls stay out of the page until More actions opens', () => {
  const closed = render();
  assert.match(closed, /aria-expanded="false"/);
  assert.doesNotMatch(closed, /Scheduling controls|Pause new assignments|Restart worker|Check restart status|Reload worker status/);
  assert.ok(closed.indexOf('Operational status') < closed.indexOf('Current execution'));
  assert.doesNotMatch(closed, /poc-worker-rail|poc-control-rail/);

  const open = renderOpenMenu();
  assert.match(open, /aria-expanded="true"/);
  const labels = ['Scheduling controls', 'Pause new assignments', 'Restart worker', 'Check restart status', 'Worker status', 'Reload worker status'];
  const positions = labels.map(label => open.indexOf(label));
  assert.ok(positions.every(position => position >= 0));
  assert.deepEqual(positions, [...positions].sort((left, right) => left - right));
  assert.match(open, /Pause scheduling to stop new assignments\. Running executions continue to completion\./);
  assert.match(open, /This reads the existing result and current service status\. It does not send another restart\./);
});

test('closed-menu action results use an out-of-flow notification', async () => {
  const html = render({ statusMessage: { text: 'Worker status refreshed.', failed: false } });
  assert.match(html, /class="poc-worker-action-toast"/);
  assert.match(html, /Worker status refreshed\./);
  const css = await readFile(new URL('../src/poc.css', import.meta.url), 'utf8');
  assert.match(css, /\.poc-worker-action-toast\s*\{[^}]*position:\s*fixed/);
});

test('unknown capacity remains unavailable and scheduling controls preserve action guards', () => {
  const unknownCapacity = render({ worker: { maximumCapacity: undefined, capacity: undefined, availableCapacity: undefined } });
  assert.match(unknownCapacity, /0 \/ Unavailable/);
  assert.doesNotMatch(unknownCapacity, /0 \/ 0/);

  const enabled = renderOpenMenu();
  assert.match(enabled, /Pause new assignments/);
  assert.match(enabled, /Pause scheduling to stop new assignments\. Running executions continue to completion\./);
  assert.match(enabled, /Restart worker/);
  assert.match(enabled, /Reload worker status/);

  const blocked = renderOpenMenu({ actions: actions.map(action => action.key === 'Draining' ? { ...action, reason: 'operation pending' } : action) });
  assert.match(blocked, /Pause new assignments/);
  const pauseLabel = blocked.indexOf('Pause new assignments');
  const pauseButtonStart = blocked.lastIndexOf('<button', pauseLabel);
  const pauseButtonEnd = blocked.indexOf('>', pauseButtonStart);
  assert.match(blocked.slice(pauseButtonStart, pauseButtonEnd), /\sdisabled(?:="")?/);
  assert.match(blocked, /operation pending/);

  const disabled = renderOpenMenu({ worker: { schedulingPolicy: 'Disabled' } });
  assert.match(disabled, /Activate scheduling/);
});

test('recent executions use the cached title and retain the Issue link', () => {
  const html = render();
  assert.match(html, /#42 · Use the cached Issue title/);
  assert.match(html, /href="\/projects\/project-1\/issues\/42"/);
  assert.doesNotMatch(html, /\/issues\?/);
});

test('readiness and technical details stack at narrow widths', async () => {
  const html = render();
  const sections = ['Technical details', 'Capabilities &amp; provisioning', 'Readiness checks'].map(label => html.indexOf(label));
  assert.ok(sections.every(position => position >= 0));
  assert.deepEqual(sections, [...sections].sort((left, right) => left - right));
  const css = await readFile(new URL('../src/poc.css', import.meta.url), 'utf8');
  assert.match(css, /\.poc-worker-lower-grid\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\) minmax\(0, 1fr\)/);
  assert.match(css, /@media \(max-width: 1023px\)[\s\S]*?\.poc-worker-lower-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /@media \(max-width: 460px\)[\s\S]*?\.poc-worker-status-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /\.poc-service-grid\s*\{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(css, /@media \(max-width: 460px\)[\s\S]*?\.poc-service-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /\.poc-worker-layout\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
});

test('More actions uses a keyboard menu trigger with viewport bounds', async () => {
  const source = await readFile(new URL('../src/detail.jsx', import.meta.url), 'utf8');
  const css = await readFile(new URL('../src/poc.css', import.meta.url), 'utf8');
  assert.match(source, /<MenuTrigger isOpen=\{isOpen\} onOpenChange=\{onOpenChange\}>/);
  assert.match(source, /aria-haspopup="menu" aria-expanded=\{isOpen\}/);
  assert.doesNotMatch(source, /DialogTrigger|aria-haspopup="dialog"/);
  assert.match(source, /<Popover placement="bottom end"/);
  assert.match(source, /shouldCloseOnSelect=\{false\}/);
  assert.match(css, /\.poc-worker-actions-popover\s*\{[^}]*width:\s*min\(28rem, calc\(100vw - 2rem\)\)[^}]*max-height:\s*min\(80dvh, 44rem\)/);
});
