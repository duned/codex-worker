import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { compareSemanticVersions, workerConfiguration, workerVersionState, workerUpdateBlockReason } from '../src/features/workers/worker-editor-model.mjs';

const sourceRoot = new URL('../src/', import.meta.url);
const read = path => readFileSync(new URL(path, sourceRoot), 'utf8');
const freshWorker = overrides => ({
  workerId: 'worker-1', displayName: 'Worker', availability: 'online', lifecycleState: 'running',
  workerVersion: '0.14.2', lastHeartbeatAtUtc: new Date().toISOString(), maximumCapacity: 4,
  ...overrides
});

test('Worker version comparison follows semantic ordering and ignores build metadata', () => {
  assert.equal(compareSemanticVersions('0.15.0', '0.14.9'), 1);
  assert.equal(compareSemanticVersions('1.9.0', '1.10.0'), -1);
  assert.equal(compareSemanticVersions('1.0.0-rc.2', '1.0.0-rc.10'), -1);
  assert.equal(compareSemanticVersions('1.0.0+worker.4', '1.0.0'), 0);
  assert.equal(compareSemanticVersions('01.0.0', '1.0.0'), undefined);
});

test('Worker version presentation distinguishes matching, older, newer and unavailable evidence', () => {
  assert.equal(workerVersionState(freshWorker(), '0.14.2').state, 'current');
  assert.equal(workerVersionState(freshWorker({ workerVersion: '0.14.9' }), '0.15.0').state, 'available');
  assert.equal(workerVersionState(freshWorker({ workerVersion: '0.16.0' }), '0.15.0').state, 'ahead');
  assert.equal(workerVersionState(freshWorker({ workerVersion: 'invalid' }), '0.15.0').state, 'unavailable');
  assert.equal(workerVersionState(freshWorker({ availability: 'stale' }), '0.15.0').state, 'unavailable');
  assert.equal(workerVersionState(freshWorker({ lastHeartbeatAtUtc: undefined }), '0.15.0').state, 'unavailable');
  assert.equal(workerVersionState(freshWorker(), undefined).state, 'unavailable');
  assert.equal(workerVersionState(freshWorker(), undefined, true).state, 'loading');
  assert.equal(workerVersionState(freshWorker(), '0.15.0', true).state, 'loading');
});

test('reported Worker update progress and failures take precedence over version comparison', () => {
  assert.equal(workerVersionState(freshWorker({ lifecycleState: 'updating' }), '0.15.0').state, 'in-progress');
  assert.equal(workerVersionState(freshWorker({ lifecycleState: 'updated' }), '0.15.0').state, 'in-progress');
  const failed = workerVersionState(freshWorker({ lifecycleState: 'update-failed' }), '0.15.0');
  assert.equal(failed.state, 'failed');
  assert.equal(failed.error, undefined);
  assert.equal(workerVersionState(freshWorker({ lifecycleState: 'restart-failed' }), '0.15.0').state, 'failed');
});

test('Worker editor keeps absent capacity unknown and prefers the saved Server limit', () => {
  assert.deepEqual(workerConfiguration({ displayName: 'Node' }), { displayName: 'Node', maximumParallelExecutions: '', workerExecutionSlots: '' });
  assert.deepEqual(workerConfiguration({ displayName: 'Node', maximumCapacity: 3 }), { displayName: 'Node', maximumParallelExecutions: '3', workerExecutionSlots: '3' });
  assert.deepEqual(workerConfiguration({ displayName: 'Node', maximumCapacity: 3, configuredMaximumCapacity: 2 }), { displayName: 'Node', maximumParallelExecutions: '2', workerExecutionSlots: '3' });
  assert.equal(workerConfiguration({ capacity: 0 }).maximumParallelExecutions, '');
});

test('Worker editor uses revision-checked persistence, native form validation and the shared accessible side panel', () => {
  const editor = read('features/workers/WorkerEditor.tsx');
  const dialog = read('shared/Dialogs.tsx');
  assert.match(editor, /variant="side-panel"/);
  assert.match(editor, /const resource = path/);
  assert.match(editor, /aria-labelledby="worker-editor-general"/);
  assert.match(editor, /aria-labelledby="worker-editor-capacity"/);
  const settings = read('features/workers/worker-settings.ts');
  assert.match(settings, /runtime\.mutate\(resource, `\$\{path\}\/settings`, 'PUT'/);
  assert.match(settings, /expectedRevision: revision/);
  assert.match(editor, /!refreshRequired && !refreshing/);
  assert.match(editor, /failure.status === 409/);
  assert.match(editor, /isRequired maxLength=\{200\}/);
  assert.match(editor, /min=\{1\} max=\{8\} isRequired/);
  assert.match(editor, /if \(guard\.current \|\| !canSubmit/);
  assert.match(editor, /onPress=\{close\}/);
  assert.match(dialog, /isDismissable=\{!pending\}/);
  assert.match(dialog, /isKeyboardDismissDisabled=\{pending\}/);
});

test('only an available Worker version exposes the Server update action', () => {
  const editor = read('features/workers/WorkerEditor.tsx');
  const english = read('shared/locales/en.ts');
  assert.match(editor, /version\.state === 'available'[\s\S]*?editorUpdateWorker/);
  assert.match(editor, /version\.state === 'in-progress'/);
  assert.match(editor, /version\.state === 'failed'/);
  assert.match(editor, /version\.state === 'ahead'/);
  assert.match(editor, /version\.state === 'unavailable'/);
  assert.doesNotMatch(editor, /navigator\.clipboard|sudo codex-worker update/);
  assert.match(editor, /runtime\.mutate\(`\$\{path\}\/update`/);
  assert.match(editor, /aria-describedby="worker-update-help"/);
  assert.match(editor, /aria-label=\{t\('workers\.editorUpdateWorkerFor'/);
  assert.match(editor, /loading: updateRead\.fetching/);
  assert.match(english, /waits for active executions to finish/);
});

test('Telegram editor uses shared Untitled UI controls and never renders the saved token', () => {
  const editor = read('features/workers/WorkerEditor.tsx');
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');
  assert.match(editor, /import \{ Checkbox \} from .*checkbox\/checkbox/);
  assert.equal((editor.match(/<Checkbox /g) ?? []).length, 2);
  assert.match(editor, /<Input label=\{t\('workers\.telegramChat'\)\}[^>]*hint=/);
  assert.match(editor, /<Input label=\{t\('workers\.telegramToken'\)\} type="password" autoComplete="new-password"/);
  assert.match(editor, /telegramTokenConfigured/);
  assert.match(editor, /telegramClearHelp/);
  assert.doesNotMatch(editor, /<label><input type="checkbox"/);
  for (const language of [english, spanish]) {
    assert.match(language, /"workers\.telegramChatHelp":/);
    assert.match(language, /"workers\.telegramClearHelp":/);
  }
});

test('Worker data refresh keeps the mounted editor, dirty draft and scroller available', () => {
  const page = read('features/workers/WorkerPocPage.tsx');
  const editor = read('features/workers/WorkerEditor.tsx');
  const styles = read('poc.css');
  assert.match(page, /const editingWorker = registry\.retainedData/);
  assert.match(page, /editing && editingWorker && <WorkerEditor key=\{id\}/);
  assert.match(editor, /const \[displayName, setDisplayName\] = useState\(baseline\.displayName\)/);
  assert.doesNotMatch(editor, /useEffect\([^)]*(setDisplayName|setTelegramChat|setCapacity)/);
  assert.match(editor, /className="worker-editor-body"/);
  assert.match(styles, /scrollbar-gutter: stable/);
  assert.match(styles, /overscroll-behavior: contain/);
});

test('Worker editor copy is complete in English and Spanish', () => {
  const english = read('shared/locales/en.ts');
  const spanish = read('shared/locales/es.ts');
  const keys = source => [...source.matchAll(/"(workers\.editor[^"]+)":/g)].map(match => match[1]).sort();
  assert.deepEqual(keys(spanish), keys(english));
});

test('configured slots are independent from assignment limit and occupancy', () => {
  assert.deepEqual(workerConfiguration({ displayName: 'Node', configuredMaximumCapacity: 1,
    workerExecutionSlots: 4, maximumCapacity: 2, activeExecutions: 2 }),
  { displayName: 'Node', maximumParallelExecutions: '1', workerExecutionSlots: '4' });
});

const updateWorker = overrides => freshWorker({ lifecycleState: 'not-ready',
  configurationSynchronization: 'error', managedDiagnostics: { diagnosticCode: 'managed-contract-invalid' },
  capabilities: [{ type: 'protocol', name: 'worker-self-update-v1' }], ...overrides });

test('invalid managed configuration does not hide or block an available controlled update', () => {
  assert.equal(workerVersionState(updateWorker(), '0.15.0').state, 'available');
  assert.equal(workerUpdateBlockReason(updateWorker()), undefined);
  assert.equal(workerVersionState(updateWorker({ workerVersion: '0.15.0' }), '0.15.0').state, 'current');
  assert.equal(workerVersionState(updateWorker({ workerVersion: '0.16.0' }), '0.15.0').state, 'ahead');
});

for (const [flag, reason] of Object.entries({ busy: 'busy', refreshing: 'refreshing', editorLocked: 'editor-locked', workerStatusError: 'worker-status-error', pending: 'pending', loading: 'loading', error: 'status-error', failed: 'failed', locked: 'locked' })) {
  test(`update explains ${reason} and has localized recovery text`, () => {
    assert.equal(workerUpdateBlockReason(updateWorker(), { [flag]: true }), reason);
    for (const language of ['en', 'es']) assert.ok(read(`shared/locales/${language}.ts`).includes(`"workers.editorUpdateBlock.${reason}"`));
  });
}
for (const [overrides, reason] of [[{ availability: 'offline' }, 'offline'], [{ availability: 'stale' }, 'stale'], [{ capabilities: [] }, 'unsupported'], [{ lifecycleState: 'stopped' }, 'lifecycle']]) {
  test(`update safely blocks ${reason}`, () => assert.equal(workerUpdateBlockReason(updateWorker(overrides)), reason));
}
