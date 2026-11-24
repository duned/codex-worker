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

test('execution actions offer only what can resolve the current state', () => {
  const failed = values => execution({ state: 'Failed', workReference: { type: 'github-issue', id: '60' }, ...values });
  assert.deepEqual(model.executionActions(execution({ state: 'Queued' }), false), ['cancel']);
  assert.deepEqual(model.executionActions(execution({ state: 'Running' }), false), []);
  assert.deepEqual(model.executionActions(execution({ state: 'Completed' }), false), []);
  assert.deepEqual(model.executionActions(failed({ recoveryState: 'LeaseExpiredUncertain' }), false), ['resolveUncertain']);
  assert.deepEqual(model.executionActions(failed({ failureClassification: 'IntegrationConflict', recoverable: true }), false), ['retryIntegration', 'retryFresh']);
  assert.deepEqual(model.executionActions(failed({ failureClassification: 'IntegrationConflict', recoverable: false }), false), ['retryFresh']);
  assert.deepEqual(model.executionActions(failed({ failureClassification: 'InfrastructureFailure', validationResult: 'passed' }), false), ['retryFresh', 'manualCompletion']);
  assert.deepEqual(model.executionActions(failed({ failureClassification: 'Failed' }), false), ['retryFresh']);
  assert.deepEqual(model.executionActions(failed({ recoveryState: 'RebaseConflictRequeued' }), false), []);
  assert.deepEqual(model.executionActions(failed({ failureClassification: 'Failed' }), true), []);
  const first = failed({ id: 'a', createdAtUtc: at(0) }), second = failed({ id: 'b', createdAtUtc: at(5), attemptNumber: 2 });
  assert.equal(model.newerAttempt(first, [first, second])?.id, 'b');
  assert.equal(model.newerAttempt(second, [first, second]), undefined);
});

test('tools signed in from a Worker terminal do not offer a dashboard sign-in that can never finish', () => {
  const claude = { definition: { id: 'claude-cli', displayName: 'Claude Code CLI' }, state: { installation: 'Installed', health: 'Healthy' }, availableActions: ['refresh', 'login', 'checkauthentication'] };
  assert.deepEqual(model.offeredToolActions(claude), ['checkauthentication', 'refresh']);
  assert.match(model.terminalSignIn['claude-cli'], /^sudo -u codex-worker -H sh -c '"\$HOME\/\.local\/bin\/claude" auth login'$/);
  const manual = { id: 'm', status: 'Failed', createdAtUtc: at(0), failureDetail: { code: 'ManualVendorCliCompletionRequired', description: 'x' }, request: { nodeId: 'n', capabilityId: 'claude-cli', action: 'Login' } };
  assert.equal(model.needsLocalCompletion(manual), true);
  assert.equal(model.needsLocalCompletion({ ...manual, failureDetail: { code: 'ProcessExited', description: 'x' } }), false);
});

test('a denied tool action points at the exact local policy setting', () => {
  const denied = (capabilityId, action, code) => ({ id: 'd', status: 'Failed', diagnostic: 'Denied', createdAtUtc: at(0), request: { nodeId: 'n', capabilityId, action },
    ...(code ? { failureDetail: { code, description: 'x' } } : {}) });
  assert.deepEqual(model.deniedPolicyHint(denied('dotnet-sdk', 'Update', 'PolicyActionNotAllowlisted')), { kind: 'allowlist', key: 'tool:dotnet-sdk:update', exact: true });
  assert.deepEqual(model.deniedPolicyHint(denied('github-cli', 'Login', 'PolicyActionDenied')), { kind: 'deniedList', key: 'authentication:github-cli:login' });
  assert.deepEqual(model.deniedPolicyHint(denied('dotnet-sdk', 'Update', 'PolicyProvisioningDisabled')), { kind: 'setting', setting: 'enabled', exact: true });
  assert.deepEqual(model.deniedPolicyHint(denied('dotnet-sdk', 'Update', 'ElevationNotRequested')), { kind: 'elevation' });
  assert.deepEqual(model.deniedPolicyHint(denied('git', 'GenerateSshKey', 'PolicyActionDenied')), { kind: 'deniedList', key: 'authentication:git:generatesshkey' });
  // Older Workers only say Denied: infer with the Worker's own rule.
  assert.deepEqual(model.deniedPolicyHint(denied('dotnet-sdk', 'Update')), { kind: 'allowlist', key: 'tool:dotnet-sdk:update', exact: false });
  assert.deepEqual(model.deniedPolicyHint(denied('github-cli', 'Login')), { kind: 'setting', setting: 'allowCredentials', exact: false });
  assert.equal(model.deniedPolicyHint({ ...denied('dotnet-sdk', 'Update'), diagnostic: 'ProcessFailed' }), undefined);
});

test('only integrated branches with an exact tip are offered for deletion', () => {
  assert.equal(model.deletableBranch({ branch: 'done/feature/1', decision: 'eligible', reason: 'integrated', tip: 'a'.repeat(40) }), true);
  assert.equal(model.deletableBranch({ branch: 'done/feature/1', decision: 'eligible', reason: 'integrated', tip: 'short' }), false);
  assert.equal(model.deletableBranch({ branch: 'done/feature/1', decision: 'recent', reason: 'newer-than-threshold', tip: 'a'.repeat(40) }), false);
});

test('branch deletions are split into Server-sized requests', () => {
  const items = Array.from({ length: 205 }, (_, index) => index);
  assert.deepEqual(model.branchBatches(items).map(batch => batch.length), [100, 100, 5]);
  assert.deepEqual(model.branchBatches([]), []);
});

test('the attention inbox groups problems by cause and lists each once', () => {
  const worker = values => ({ workerId: 'w', availability: 'online', lifecycleState: 'running', ...values });
  const paused = worker({ workerId: 'paused', managedDiagnostics: { projects: [
    { projectId: 'p', revision: 1, state: 'blocked', diagnosticCode: 'project-recovery-verification-failed', detail: 'GitHub failed.' },
    { projectId: 'q', revision: 1, state: 'blocked', diagnosticCode: 'project-capabilities-missing' }] } });
  const operation = worker({ workerId: 'operation', schedulingBlocker: 'worker-operation-unresolved' });
  const offline = worker({ workerId: 'offline', availability: 'offline' });
  const inbox = model.attentionInbox([
    execution({ id: 'uncertain', state: 'Failed', recoveryState: 'LeaseExpiredUncertain' }),
    execution({ id: 'failed', state: 'Failed', workReference: { type: 'github-issue', id: '8' } }),
    execution({ id: 'requeued', state: 'Failed', recoveryState: 'LeaseExpiredRequeued', workReference: { type: 'github-issue', id: '9' } }),
    execution({ id: 'ineligible', state: 'Queued', managedEligibilityState: 'blocked', workReference: { type: 'github-issue', id: '10' } }),
    execution({ id: 'eligible', state: 'Queued', managedEligibilityState: 'eligible', workReference: { type: 'github-issue', id: '11' } })
  ], [paused, operation, offline, worker({ workerId: 'healthy' })]);
  assert.deepEqual(inbox.pausedProjects.map(item => [item.worker.workerId, item.projectId, item.detail]), [['paused', 'p', 'GitHub failed.']]);
  assert.deepEqual(inbox.maintenance.map(item => item.workerId), ['operation']);
  assert.deepEqual(inbox.uncertain.map(item => item.id), ['uncertain']);
  assert.deepEqual(inbox.ineligible.map(item => item.id), ['ineligible']);
  assert.deepEqual(inbox.failed.map(item => item.id), ['failed']);
  assert.deepEqual(inbox.workers.map(item => item.workerId), ['offline']);
  assert.equal(model.attentionCount(inbox), 6);
});

test('each Worker has one health state with its most severe cause', () => {
  const worker = values => ({ workerId: 'w', availability: 'online', lifecycleState: 'running', workerExecutionSlots: 2, ...values });
  const paused = { managedDiagnostics: { projects: [{ projectId: 'p', revision: 1, state: 'blocked', diagnosticCode: 'project-recovery-verification-failed' }] } };
  const state = values => model.workerHealth(worker(values), id => `Project ${id}`);
  assert.equal(state({ availability: 'offline', schedulingBlocker: 'worker-operation-unresolved' }).state, 'offline');
  assert.equal(state({ schedulingBlocker: 'worker-operation-unresolved', schedulingPolicy: 'Disabled' }).state, 'blocked');
  assert.equal(state({ schedulingPolicy: 'Disabled', lifecycleState: 'drained' }).state, 'paused');
  assert.equal(state({ schedulingPolicy: 'Draining', lifecycleState: 'draining' }).state, 'pausing');
  assert.equal(state({ lifecycleState: 'not-ready' }).state, 'blocked');
  const degraded = state({ ...paused, activeExecutions: 1 });
  assert.deepEqual([degraded.state, degraded.tone, degraded.cause.values.list], ['degraded', 'warn', 'Project p']);
  assert.deepEqual(state({ activeExecutions: 1 }).cause, { key: 'health.cause.working', values: { used: 1, total: 2 } });
  assert.equal(state({}).state, 'ready');
});

test('a Worker clock more than a minute off the Server is a health warning and an inbox item', () => {
  const worker = values => ({ workerId: 'w', availability: 'online', lifecycleState: 'running', ...values });
  assert.equal(model.workerHealth(worker({ clockSkewSeconds: 60 })).state, 'ready');
  const ahead = model.workerHealth(worker({ clockSkewSeconds: 90 }));
  assert.deepEqual([ahead.state, ahead.cause.key, ahead.cause.values.seconds], ['clock', 'health.cause.clockAhead', 90]);
  assert.equal(model.workerHealth(worker({ clockSkewSeconds: -120 })).cause.key, 'health.cause.clockBehind');
  assert.equal(model.workerHealth(worker({ clockSkewSeconds: 600, availability: 'offline' })).state, 'offline');
  assert.deepEqual(model.attentionInbox([], [worker({ clockSkewSeconds: 600 }), worker({ workerId: 'ok' })]).clock.map(item => item.workerId), ['w']);
});

test('a preparation failure the Server retried by itself needs no attention', () => {
  const failed = execution({ state: 'Failed', failureClassification: 'PreparationFailed', recoveryState: 'PreparationRequeued' });
  assert.equal(model.executionStatus(failed).label, 'status.preparationRetried');
  assert.equal(model.explain(failed).todo, 'explain.preparationRetried.todo');
  assert.equal(model.needsAttention(failed), false);
  assert.deepEqual(model.executionActions(failed, false), []);
});
