import test from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';

// Bundle the pure model (no React) so these tests exercise the real status rules.
const { outputFiles } = await build({ entryPoints: [new URL('../src/shared/model.ts', import.meta.url).pathname], bundle: true, write: false, format: 'esm', platform: 'node' });
const model = await import('data:text/javascript;base64,' + Buffer.from(outputFiles[0].text).toString('base64'));
const at = minutes => new Date(Date.parse('2026-11-16T10:00:00Z') + minutes * 60000).toISOString();
const execution = values => ({ id: 'e', projectId: 'p', state: 'Running', createdAtUtc: at(0), workReference: { type: 'github-issue', id: '7' }, ...values });

test('running stages map to the step being worked on', () => {
  assert.equal(model.executionStatus(execution({ currentStage: 'Codex' })).label, 'status.implementing');
  assert.equal(model.executionStatus(execution({ currentStage: 'Validation' })).tone, 'run');
  const steps = model.stepProgress(execution({ currentStage: 'Validation' })).map(step => step.state);
  assert.deepEqual(steps, ['done', 'done', 'now', 'todo', 'todo']);
});

test('a failed execution marks the step it failed at, using timings when the stage is missing', () => {
  const failed = execution({ state: 'Failed', failureClassification: 'IntegrationConflict', integrationResult: 'rebase-conflict', completedAtUtc: at(50),
    stageStartedAtUtc: { claimed: at(1), codex: at(2), validation: at(40), integration: at(48) } });
  assert.deepEqual(model.stepProgress(failed).map(step => step.state), ['done', 'done', 'done', 'fail', 'todo']);
  assert.equal(model.stepProgress(failed)[1].durationMs, 38 * 60000);
  assert.equal(model.executionStatus(failed).label, 'status.conflict');
  assert.equal(model.explain(failed).todo, 'explain.conflict.todo');
});

test('a rejected push and an integration lost mid-flight get specific explanations', () => {
  const rejected = execution({ state: 'Failed', failureClassification: 'IntegrationConflict', currentStage: 'Integration',
    completionSummary: "GitHub rejected the push to 'main' because the change creates or updates GitHub Actions workflow files." });
  assert.equal(model.explain(rejected).what, 'explain.pushRejected.what');
  assert.equal(model.explain(rejected).todo, 'explain.pushRejected.todo');
  const conflict = execution({ state: 'Failed', failureClassification: 'IntegrationConflict', completionSummary: 'The integration base kept advancing.' });
  assert.equal(model.explain(conflict).what, 'explain.integration.what');
  const lostIntegrating = execution({ state: 'Failed', recoveryState: 'LeaseExpiredUncertain', currentStage: 'Integration' });
  assert.equal(model.explain(lostIntegrating).what, 'explain.integrationUncertain.what');
  const lostEarlier = execution({ state: 'Failed', recoveryState: 'LeaseExpiredUncertain', currentStage: 'Validation' });
  assert.equal(model.explain(lostEarlier).what, 'explain.workerLost.what');
});

test('automatically requeued failures do not need attention, and newer attempts supersede older failures', () => {
  const requeued = execution({ id: 'a', state: 'Failed', failureClassification: 'IntegrationConflict', recoveryState: 'RebaseConflictRequeued', createdAtUtc: at(0) });
  assert.equal(model.executionStatus(requeued).tone, 'warn');
  assert.deepEqual(model.attention([requeued]), []);
  const failed = execution({ id: 'b', state: 'Failed', createdAtUtc: at(1) });
  const retried = execution({ id: 'c', state: 'Queued', createdAtUtc: at(2) });
  assert.deepEqual(model.attention([failed]).map(item => item.id), ['b']);
  assert.deepEqual(model.attention([failed, retried]), []);
});

test('worker scheduling separates paused, pausing, blocked and accepting', () => {
  const base = { workerId: 'w', availability: 'online', lifecycleState: 'running', schedulingPolicy: 'Enabled' };
  assert.equal(model.scheduling(base), 'accepting');
  assert.equal(model.scheduling({ ...base, schedulingPolicy: 'Draining' }), 'pausing');
  assert.equal(model.scheduling({ ...base, schedulingPolicy: 'Disabled', availability: 'offline' }), 'paused');
  assert.equal(model.scheduling({ ...base, availability: 'offline' }), 'blocked');
  assert.equal(model.scheduling({ ...base, schedulingBlocker: 'Codex sign-in expired' }), 'blocked');
});

test('issue status prefers its latest execution, then dependencies and labels', () => {
  const issue = { number: 7, labels: ['codex-ready'], blockedBy: [], state: 'open' };
  assert.equal(model.issueStatus(issue, undefined).label, 'status.ready');
  assert.equal(model.issueStatus({ ...issue, blockedBy: [{ number: 3, state: 'open' }] }, undefined).label, 'status.waiting');
  assert.equal(model.issueStatus(issue, execution({ state: 'Queued' })).label, 'status.queued');
  const latest = model.latestByIssue([execution({ id: 'old', createdAtUtc: at(0), state: 'Failed' }), execution({ id: 'new', createdAtUtc: at(5), state: 'Completed' })]);
  assert.equal(latest.get(7).id, 'new');
});

test('tool actions follow what the node advertises and one active command blocks the tool', () => {
  const capability = { definition: { id: 'github-cli', displayName: 'GitHub CLI', requiresAuthentication: true, requiresConfiguration: false },
    state: { installation: 'Installed', health: 'Healthy' }, availableActions: ['refresh', 'login', 'logout', 'uninstall', 'checkauthentication'] };
  assert.deepEqual(model.offeredToolActions(capability), ['login', 'checkauthentication', 'refresh']);
  const command = (id, status, createdAtUtc, capabilityId = 'github-cli') => ({ id, status, createdAtUtc, request: { nodeId: 'n', capabilityId, action: 'Login' } });
  const commands = [command('old', 'Failed', at(1)), command('new', 'Running', at(2)), command('other', 'Running', at(3), 'codex-cli')];
  assert.equal(model.toolCommand(commands, 'n', 'github-cli').latest.id, 'new');
  assert.equal(model.toolCommand(commands, 'n', 'github-cli').busy, true);
  assert.equal(model.toolCommand([command('done', 'Succeeded', at(1))], 'n', 'github-cli').busy, false);
  assert.equal(model.deviceLoginUrl('https://github.com/login/device'), 'https://github.com/login/device');
  assert.equal(model.deviceLoginUrl('https://evil.example/login'), undefined);
});

test('tool progress shows a request until the Worker records it, then the command, then the node operation', () => {
  const capability = { definition: { id: 'github-cli', displayName: 'GitHub CLI' }, state: { installation: 'Installed', health: 'Healthy' }, availableActions: [] };
  const sent = Date.parse(at(10));
  const old = { id: 'o', status: 'Failed', createdAtUtc: at(1), request: { nodeId: 'n', capabilityId: 'github-cli', action: 'Login' } };
  const fresh = { ...old, id: 'f', status: 'Running', createdAtUtc: at(10) };
  assert.equal(model.toolProgress(capability, old, { action: 'Login', atUtc: sent }).kind, 'requested');
  const shown = model.toolProgress(capability, fresh, { action: 'Login', atUtc: sent });
  assert.equal(shown.kind, 'command');
  assert.equal(model.toolInFlight(shown), true);
  assert.equal(model.toolInFlight(model.toolProgress(capability, { ...fresh, status: 'Succeeded' }, undefined)), false);
  const running = { ...capability, state: { ...capability.state, operation: { state: 'Running', action: 'login' } } };
  assert.deepEqual(model.toolProgress(running, undefined, undefined), { kind: 'running', action: 'login' });
  assert.equal(model.toolActionCommand('login'), 'Login');
});
