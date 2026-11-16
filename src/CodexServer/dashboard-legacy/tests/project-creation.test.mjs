import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import { build } from 'esbuild';

// Exercise the workspace owner with deterministic hook state, without a browser.
const bundle = await build({
  stdin: { contents: `export { useWorkspaceOwner } from './src/features/projects/Workspace'; export { DashboardRuntime } from './src/shared/api/runtime'; export { HttpClient } from './src/shared/api/client'; export { sameDefinition, definitionOf, newDefinition } from './src/features/projects/model'; export { project } from './src/features/projects/contracts';`, resolveDir: new URL('..', import.meta.url).pathname },
  bundle: true, platform: 'node', format: 'cjs', write: false,
  plugins: [{ name: 'workspace-hooks', setup(build) {
    build.onLoad({ filter: /projects\/Workspace.tsx$/ }, args => ({ contents: readFileSync(args.path, 'utf8') + '\nexport { useWorkspaceOwner };', loader: 'tsx' }));
    build.onResolve({ filter: /^react$/ }, () => ({ path: 'react', namespace: 'hooks' }));
    build.onLoad({ filter: /.*/, namespace: 'hooks' }, () => ({ contents: `export default {}; export const useState = value => globalThis.projectHooks.useState(value); export const useEffect = effect => globalThis.projectHooks.effects.push(effect); export const createContext = () => ({}); export const useContext = () => ({});` }));
    build.onLoad({ filter: /api\/session.tsx$/ }, () => ({ contents: `export const useRuntime = () => globalThis.projectHooks.runtime; export const useSession = () => ({});` }));
  } }],
});
const module = { exports: {} };
new Function('module', 'exports', 'require', bundle.outputFiles[0].text)(module, module.exports, createRequire(import.meta.url));
const { useWorkspaceOwner, DashboardRuntime, HttpClient, sameDefinition, definitionOf, newDefinition, project } = module.exports;
const definition = { validationCommands: ['build', 'test'], name: 'Sample', repository: 'owner/repo', defaultBranch: 'main', description: 'Keep my input', requirements: [], issueReadyLabel: null, issueBlockedLabel: null, maxParallelTasks: null,
  automaticDiscovery: { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120, enabled: true } };
const saved = { ...definition, id: 'sample', revision: 1, enabled: true,
  automaticDiscovery: { enabled: true, intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 } };

test('discovery comparison ignores JSON field order while retaining policy differences', () => {
  assert.equal(sameDefinition(saved, definition), true);
  for (const [key, value] of Object.entries({ enabled: false, intervalSeconds: 600, pageSize: 10, deadlineSeconds: 30 })) {
    assert.equal(sameDefinition(saved, { ...definition, automaticDiscovery: { ...definition.automaticDiscovery, [key]: value } }), false);
  }
});

for (const mode of ['success', 'failure', 'lost-response']) test(`project creation ${mode} manages the draft and notification`, async t => {
  let created = false;
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (url, options) => {
    if (url.endsWith('/session')) return Response.json({ csrfToken: 'fixture', expiresAtUtc: new Date(Date.now() + 60_000).toISOString() });
    if (url.endsWith('/stream')) return new Response(new ReadableStream());
    if (url.endsWith('/verify')) return Response.json({ repository: definition.repository, defaultBranch: 'main', repositoryReadable: true, branchExists: true, diagnostic: '' });
    if (options.method === 'POST' && !url.endsWith('/verify')) {
      assert.deepEqual(JSON.parse(options.body).validationCommands, definition.validationCommands);
      if (mode === 'failure') return Response.json({}, { status: 400 });
      created = true;
      if (mode === 'lost-response') throw new TypeError('Connection lost');
      return Response.json(saved, { status: 201 });
    }
    return Response.json(created ? [saved] : []);
  }));
  t.after(() => { runtime.dispose(); delete globalThis.projectHooks; });
  await runtime.session('GET');
  const states = []; let cursor = 0;
  globalThis.projectHooks = { runtime, effects: [], useState(initial) {
    const index = cursor++;
    if (!(index in states)) states[index] = initial;
    return [states[index], value => { states[index] = typeof value === 'function' ? value(states[index]) : value; }];
  } };
  const render = () => { cursor = 0; globalThis.projectHooks.effects = []; return useWorkspaceOwner(); };
  let owner = render();
  const draft = { definition, reviewed: definition, step: 3, open: true };
  owner.setDraft(draft); owner = render();
  if (mode === 'success') await owner.save(draft);
  else await assert.rejects(owner.save(draft));
  owner = render();
  assert.equal(created, mode !== 'failure');
  if (mode === 'success') {
    assert.equal(owner.draft, undefined, 'completed dialog is unmounted and no draft remains');
    assert.deepEqual(owner.attempts, {});
    assert.equal(owner.locked(), false);
    assert.match(owner.message, /Sample.*saved/i);
    assert.equal(owner.messageProjectId, saved.id);
    // No retained draft/attempt means no dirty-page warning is registered.
    assert.equal(globalThis.projectHooks.effects[0](), undefined);
  } else {
    assert.deepEqual(owner.draft, draft, 'dialog stays open with all values and review retained');
    assert.doesNotMatch(owner.message, /Sample.*saved/i);
    assert.match(owner.message, /check saved definition/i);
    assert.equal(owner.locked(), true, 'submitted failures retain the existing reconciliation fence');
    assert.ok(owner.attempts['project:new']);
    if (mode === 'lost-response') {
      await owner.reconcile('project:new'); owner = render();
      assert.equal(owner.draft, undefined);
      assert.deepEqual(owner.attempts, {});
      assert.equal(owner.locked(), false);
      assert.match(owner.message, /saved/i);
    }
  }
});

test('validation commands survive unrelated edits and ordered comparison detects changes', () => {
  assert.equal(sameDefinition(saved, { ...definition, validationCommands: ['test', 'build'] }), false);
  assert.equal(sameDefinition(saved, { ...definition, validationCommands: [] }), false);
  assert.equal(sameDefinition(saved, { ...definition, validationCommands: null }), false);
});

test('project edit sends ordered commands, preserves other fields and confirms the returned definition', async t => {
  const desired = { ...definition, validationCommands: ['test', 'build', 'check'] };
  const updated = { ...saved, ...desired, revision: 2 };
  let persisted = saved;
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (url, options) => {
    if (url.endsWith('/session')) return Response.json({ csrfToken: 'fixture', expiresAtUtc: new Date(Date.now() + 60_000).toISOString() });
    if (url.endsWith('/stream')) return new Response(new ReadableStream());
    if (url.endsWith('/verify')) return Response.json({ repository: definition.repository, defaultBranch: 'main', repositoryReadable: true, branchExists: true, diagnostic: '' });
    if (options.method === 'PUT') {
      assert.deepEqual(JSON.parse(options.body), { definition: desired, expectedRevision: 1 });
      persisted = updated;
      return Response.json(updated);
    }
    return Response.json(url.endsWith('/sample') ? persisted : [persisted]);
  }));
  t.after(() => { runtime.dispose(); delete globalThis.projectHooks; });
  await runtime.session('GET');
  const states = []; let cursor = 0;
  globalThis.projectHooks = { runtime, effects: [], useState(initial) {
    const index = cursor++;
    if (!(index in states)) states[index] = initial;
    return [states[index], value => { states[index] = typeof value === 'function' ? value(states[index]) : value; }];
  } };
  const render = () => { cursor = 0; return useWorkspaceOwner(); };
  let owner = render();
  const draft = { before: saved, definition: desired, reviewed: desired, step: 3, open: true };
  owner.setDraft(draft); owner = render();
  assert.deepEqual(await owner.save(draft), updated);
  owner = render();
  assert.equal(owner.draft, undefined);
  assert.deepEqual(persisted.validationCommands, desired.validationCommands);
  assert.equal(persisted.description, saved.description);
});

test('project command mapping preserves defaults, command text and unrelated fields', () => {
  assert.equal(newDefinition().validationCommands, null);
  assert.equal(definitionOf({ ...saved, validationCommands: undefined }).validationCommands, null);
  const mapped = definitionOf(saved);
  assert.deepEqual(mapped.validationCommands, definition.validationCommands);
  mapped.validationCommands.reverse();
  assert.deepEqual(saved.validationCommands, ['build', 'test'], 'draft list does not mutate saved state');
  assert.deepEqual(definitionOf({ ...saved, description: 'Changed' }).validationCommands, saved.validationCommands);
  assert.deepEqual(project({ ...saved, validationCommands: [] }).validationCommands, []);
  for (const commands of [[' '], [null], ['a'.repeat(4001)], Array(65).fill('test'), 'test']) {
    assert.throws(() => project({ ...saved, validationCommands: commands }));
  }
});
