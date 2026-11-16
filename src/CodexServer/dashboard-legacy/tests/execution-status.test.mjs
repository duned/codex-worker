import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { isActiveExecutionState, statusColor } from '../src/shared/status-model.mjs';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');

test('running and working execution states use the blue active treatment', () => {
  for (const state of ['Running', 'running', 'Working', 'working']) {
    assert.equal(statusColor(state), 'info');
    assert.equal(isActiveExecutionState(state), true);
  }
});

test('queued, assigned, blocked and terminal states do not use the active treatment', () => {
  const expected = new Map([
    ['Queued', 'gray'],
    ['Assigned', 'warning'],
    ['Blocked', 'gray'],
    ['Completed', 'success'],
    ['Failed', 'error'],
    ['Cancelled', 'gray'],
    ['Idle', 'gray']
  ]);

  for (const [state, tone] of expected) {
    assert.equal(statusColor(state), tone, state);
    assert.equal(isActiveExecutionState(state), false, state);
  }
});

test('only explicitly active badges animate, and reduced-motion users keep the static indicator', () => {
  const presentation = read('shared/Presentation.tsx');
  const styles = read('poc.css');

  assert.match(presentation, /active \? ' dashboard-active-status' : ''/);
  assert.match(presentation, /return withDot \|\| active/);
  assert.match(styles, /\.dashboard-active-status > svg \{ animation: dashboard-status-pulse/);
  assert.match(styles, /@media \(prefers-reduced-motion: reduce\) \{ \.dashboard-active-status > svg \{ animation: none; \} \}/);
});
