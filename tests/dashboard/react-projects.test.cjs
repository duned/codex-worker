const { test, after } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('../../src/CodexServer/worker-poc/node_modules/esbuild');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, resolve } = require('node:path');
const dir = mkdtempSync(join(tmpdir(), 'projects-regression-'));
const entry = resolve('src/CodexServer/worker-poc/src');
buildSync({ stdin: { contents: `export * from '${entry}/features/projects/model'; export * from '${entry}/features/projects/contracts'; export * from '${entry}/features/projects/Readiness'; export * from '${entry}/shared/api/runtime'; export * from '${entry}/shared/api/client';`, resolveDir: resolve('src/CodexServer/worker-poc') }, bundle: true, platform: 'node', format: 'cjs', outfile: join(dir, 'test.cjs'), alias: { '@': resolve(entry, 'untitled') } });
const { savedDefinition, sameDefinition, definitionOf, project, issue, readiness, matchesIssue, changeRequest, issueUrl, DashboardRuntime, HttpClient } = require(join(dir, 'test.cjs'));
after(() => rmSync(dir, { recursive: true, force: true }));
const p = { id: 'project-a', name: 'Sample project', repository: 'owner/repo', defaultBranch: 'main', description: '', requirements: [], revision: 2, enabled: true, issueReadyLabel: 'ready', issueBlockedLabel: 'blocked', automaticDiscovery: { enabled: false, intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 } };
const i = { number: 27, title: 'Fix parsing', body: 'Details', state: 'open', url: 'https://github.com/owner/repo/issues/27', labels: ['ready'], blockedBy: [{ number: 28, title: 'Prerequisite', state: 'open', url: 'https://github.com/owner/repo/issues/28' }], isEligible: false, eligibilityReasons: ['Blocked by #28'] };
test('current automatic-discovery and typed requirement contracts reject malformed evidence', () => {
  assert.deepEqual(project(p), p);
  for (const automaticDiscovery of [{ ...p.automaticDiscovery, pageSize: 101 }, { ...p.automaticDiscovery, enabled: 'true' }, { ...p.automaticDiscovery, deadlineSeconds: 0 }]) assert.throws(() => project({ ...p, automaticDiscovery }));
  assert.throws(() => project({ ...p, requirements: [{ type: 'tool', name: {}, version: null }] }));
  assert.throws(() => issue({ ...i, isEligible: 'yes' }));
  assert.throws(() => issue({ ...i, blockedBy: [{ ...i.blockedBy[0], number: 0 }] }));
});
test('lost project responses reconcile only matching definitions and the expected edit revision', () => {
  const desired = { ...definitionOf(p), description: 'Edited', automaticDiscovery: { enabled: true, intervalSeconds: 600, pageSize: 10, deadlineSeconds: 30 } };
  assert.equal(savedDefinition([{ ...p, ...desired, revision: 3 }], p, desired).state, 'applied');
  assert.equal(savedDefinition([{ ...p, ...desired, revision: 4 }], p, desired).state, 'conflict');
  assert.equal(savedDefinition([p], p, desired).state, 'not-applied');
  assert.equal(savedDefinition([], p, desired).state, 'conflict');
  assert.equal(savedDefinition([{ ...p, ...desired }], undefined, desired).state, 'applied');
  assert.equal(savedDefinition([], undefined, desired).state, 'not-applied');
  assert.equal(savedDefinition([p, { ...p, id: 'ambiguous' }], undefined, desired).state, 'conflict');
});
test('normalization retains advanced policy, custom descriptors, versions and authentication scopes', () => {
  assert.deepEqual(definitionOf(p).automaticDiscovery, p.automaticDiscovery, 'opening an edit draft preserves the separately managed discovery policy');
  const before = { ...p, requirements: [{ type: 'custom-tool', name: 'Builder', version: '>=010.00', scope: null }, { type: 'authentication', name: 'github-api', version: null, scope: 'Owner/Repo' }] };
  assert.ok(sameDefinition(before, { ...before, repository: 'OWNER/REPO', requirements: [{ ...before.requirements[0], name: 'builder', version: '>=10.0' }, { ...before.requirements[1], scope: 'owner/repo' }] }));
  assert.equal(sameDefinition(before, { ...before, automaticDiscovery: { ...before.automaticDiscovery, enabled: true } }), false);
});
test('native relationship and configured label reconciliation uses observable Issue state', () => {
  assert.ok(matchesIssue(i, { kind: 'dependency', blockerIssueNumber: 28, applied: true }));
  assert.equal(matchesIssue(i, { kind: 'dependency', blockerIssueNumber: 28, applied: false }), false);
  assert.ok(matchesIssue(i, { kind: 'label', label: 'READY', applied: true }));
  assert.ok(matchesIssue(i, { kind: 'edit', title: i.title, body: i.body }));
  assert.equal(matchesIssue(i, { kind: 'enqueue' }), false);
  assert.deepEqual(changeRequest(p.id, 27, { kind: 'dependency', blockerIssueNumber: 28, applied: false }), { path: '/api/v1/projects/project-a/github/issues/27/dependencies/blocked-by', method: 'PUT', body: { blockerIssueNumber: 28, applied: false } });
  assert.equal(issueUrl(p.repository, { ...i, url: 'javascript:private' }), 'https://github.com/owner/repo/issues/27');
});
test('Worker readiness retains stale/current revision, materialization and eligibility separately', () => {
  const d = { aiAgentReady: true, gitHubReady: false, gitReady: false, configurationSynchronization: 'synchronized', provisioningState: 'not-required', projects: [{ projectId: p.id, projectName: p.name, isEligible: true, missingRequirements: [], workerReportedRevision: 1, materializationState: 'failed', observationStatus: 'stale-heartbeat' }] };
  assert.deepEqual(readiness(d).projects, d.projects);
  for (const workerReportedRevision of ['2', 0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1])
    assert.throws(() => readiness({ ...d, projects: [{ ...d.projects[0], workerReportedRevision }] }));
  assert.deepEqual(readiness({ ...d, projects: undefined }).projects, []);
});
test('shared preview forces non-effect requests, keeps CSRF and rejects effects outside its allowlist', async () => {
  const calls = [];
  const runtime = new DashboardRuntime(undefined, new HttpClient(async (path, options) => {
    calls.push({ path, options });
    if (path.endsWith('/session')) return Response.json({ csrfToken: 'fixture-csrf', expiresAtUtc: new Date(Date.now() + 3600000).toISOString() });
    if (path.endsWith('/stream')) throw Error('No stream fixture');
    return Response.json({ previewOnly: true });
  }));
  try {
    await runtime.session('GET');
    const signal = new AbortController().signal;
    await runtime.preview('/api/v1/projects/project-a/github/issues', 'POST', { title: 'New', previewOnly: false }, signal, value => value);
    const sent = calls.find(c => c.path.endsWith('/issues'));
    assert.equal(JSON.parse(sent.options.body).previewOnly, true);
    assert.equal(sent.options.headers.get('X-Codex-CSRF'), 'fixture-csrf');
    await assert.rejects(runtime.preview('/api/v1/projects/project-a/lifecycle', 'PUT', { enabled: true }, signal, value => value), /Unsupported preview/);
    await assert.rejects(runtime.preview('/api/v1/projects/project-a/github/issues/27/enqueue', 'POST', {}, signal, value => value), /Unsupported preview/);
  } finally { runtime.dispose(); }
});
