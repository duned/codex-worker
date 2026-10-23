import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { executionTimeline } from '../src/features/workers/execution-timeline.mjs';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');

test('Codex timeline marks passed stages complete, Codex current, and later stages pending', () => {
  const timeline = executionTimeline('Codex');

  assert.deepEqual(timeline.map(stage => [stage.id, stage.state]), [
    ['claimed', 'completed'], ['preparing', 'completed'], ['codex', 'current'],
    ['validation', 'pending'], ['integration', 'pending'], ['reporting', 'pending']
  ]);
  assert.deepEqual(timeline.map(stage => stage.connectorComplete), [false, true, true, false, false, false]);
});

test('progress remains cumulative from Codex through validation to reporting', () => {
  const codex = executionTimeline('Codex');
  const validating = executionTimeline('Validating');
  const reporting = executionTimeline('Reporting');

  assert.deepEqual(codex.map(stage => stage.state), [
    'completed', 'completed', 'current', 'pending', 'pending', 'pending'
  ]);
  assert.deepEqual(validating.map(stage => stage.state), [
    'completed', 'completed', 'completed', 'current', 'pending', 'pending'
  ]);
  assert.deepEqual(reporting.map(stage => stage.state), [
    'completed', 'completed', 'completed', 'completed', 'completed', 'current'
  ]);
  assert.deepEqual(reporting.map(stage => stage.connectorComplete), [false, true, true, true, true, true]);
});

test('timeline recognizes other reported lifecycle stages without overstating integration recovery history', () => {
  assert.deepEqual(executionTimeline('Validation').map(stage => stage.state), [
    'completed', 'completed', 'completed', 'current', 'pending', 'pending'
  ]);
  assert.deepEqual(executionTimeline('Integration').map(stage => stage.state), [
    'completed', 'completed', 'unknown', 'unknown', 'current', 'pending'
  ]);
  assert.deepEqual(executionTimeline('Integration').map(stage => stage.connectorComplete), [
    false, true, false, false, false, false
  ]);
  assert.deepEqual(executionTimeline('Claiming').map(stage => stage.state), [
    'current', 'pending', 'pending', 'pending', 'pending', 'pending'
  ]);
  assert.deepEqual(executionTimeline('Implementing').map(stage => stage.state), [
    'completed', 'completed', 'current', 'pending', 'pending', 'pending'
  ]);
});

test('unknown and unrepresented reported stages do not infer timeline completion', () => {
  for (const value of [undefined, 'Unexpected']) {
    const timeline = executionTimeline(value);
    assert.deepEqual(timeline.map(stage => stage.state), Array(6).fill('unknown'), String(value));
    assert.deepEqual(timeline.map(stage => stage.connectorComplete), Array(6).fill(false), String(value));
  }
});

test('current execution metadata is Project, GitHub Issue, then Elapsed, with elapsed removed from footer', () => {
  const detail = read('detail.jsx');
  const currentExecution = detail.match(/function CurrentExecution\([\s\S]*?\n\}/)?.[0];
  assert.ok(currentExecution);

  const labels = [...currentExecution.matchAll(/<dt>\{t\('([^']+)'\)\}<\/dt>/g)].map(match => match[1]);
  assert.deepEqual(labels, ['executions.project', 'workers.gitHubIssue', 'shared.elapsed']);
  assert.match(detail, /<IssueTitle projectId=\{item\.projectId\} repository=\{project\?\.repository\} workReference=\{work\} \/>/);
  assert.match(currentExecution, /<dt>\{t\('shared\.elapsed'\)\}<\/dt>\s*<dd>\{duration\(item, now\)\}<\/dd>/);

  const footer = currentExecution.match(/<p className="poc-current-execution-timing">([\s\S]*?)<\/p>/)?.[1];
  assert.ok(footer);
  assert.match(footer, /t\('executions\.started'\).*relativeAge/);
  assert.match(footer, /t\('shared\.executionDetails'\)/);
  assert.doesNotMatch(footer, /duration\(|elapsed/);
});

test('timeline provides visible state text, completed checkmarks, a restrained active pulse, and reduced-motion support', () => {
  const detail = read('detail.jsx');
  const styles = read('poc.css');
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');

  assert.match(detail, /stage\.state === 'completed' \? t\('workers\.stageCompleted'\)/);
  assert.match(detail, /stage\.state === 'current' \? t\('workers\.currentStageReported'\)/);
  assert.match(detail, /stage\.state === 'pending' \? t\('workers\.stagePending'\)/);
  assert.match(detail, /const markerState = stage\.state === 'completed' \? 'is-complete' : stage\.state === 'current' \? 'is-current' : ''/);
  assert.match(detail, /className=\{`poc-stage-marker \$\{markerState\}`\}/);
  assert.match(detail, /<svg viewBox="0 0 16 16" focusable="false"><path d="m3\.5 8\.25 3 2\.75 6-6" \/><\/svg>/);
  assert.match(styles, /\.poc-stage-list \{[^}]*grid-template-columns: repeat\(6, minmax\(0, 1fr\)\)/);
  assert.match(styles, /\.poc-stage-active-dot \{[^}]*animation: poc-stage-dot-pulse 2s ease-in-out infinite/);
  assert.match(styles, /@media \(prefers-reduced-motion: reduce\) \{ \.poc-stage-active-dot \{ animation: none; \} \}/);
  assert.match(english, /"workers\.stageCompleted": "Completed"/);
  assert.match(english, /"workers\.stagePending": "Pending"/);
  assert.match(spanish, /"workers\.stageCompleted": "Completada"/);
  assert.match(spanish, /"workers\.stagePending": "Pendiente"/);
  assert.match(english, /"status\.reporting": "Reporting"/);
  assert.match(spanish, /"status\.reporting": "Informando"/);
});
