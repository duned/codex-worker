import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  activeIssueExecution, canEnqueueIssue, historyOffset, issueExecutionMatches,
  issueHistoryPageSize, issueHistoryPath, issueServerState, issueStatusPath, executionOutcome
} from '../src/features/projects/issue-history-model.mjs';

const projectId = 'project-alpha';
const execution = (id, state, project = projectId, issue = '42') => ({
  id, projectId: project, state, workReference: { type: 'github-issue', id: issue }
});

test('issue history identity combines project ID and numeric GitHub issue number', () => {
  assert.equal(issueExecutionMatches(execution('a', 'Failed'), projectId, 42), true);
  assert.equal(issueExecutionMatches(execution('b', 'Failed', 'project-beta'), projectId, 42), false);
  assert.equal(issueExecutionMatches(execution('c', 'Failed', projectId, '43'), projectId, 42), false);
  assert.equal(issueExecutionMatches({ ...execution('d', 'Failed'), workReference: { type: 'commit', id: '42' } }, projectId, 42), false);
  assert.equal(issueExecutionMatches(execution('e', 'Failed', projectId, '042'), projectId, 42), true);
});

test('history request is scoped to the project and issue and exposes bounded older pages', () => {
  assert.equal(issueHistoryPageSize, 25);
  const path = issueHistoryPath('project with spaces', 42, 25);
  const query = new URLSearchParams(path.slice(path.indexOf('?')));
  assert.equal(query.get('projectId'), 'project with spaces');
  assert.equal(query.get('workType'), 'github-issue');
  assert.equal(query.get('workId'), '42');
  assert.equal(query.get('offset'), '25');
  assert.equal(query.get('limit'), '25');
  assert.equal(new URLSearchParams(issueStatusPath(projectId, 42).split('?')[1]).get('limit'), '100');
  assert.equal(historyOffset('?historyOffset=50'), 50);
  assert.equal(historyOffset('?historyOffset=-5'), 0);
});

test('enqueue eligibility blocks duplicate queued or active work and keeps project scope', () => {
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [], projectId, issueNumber: 42 }), true);
  assert.equal(canEnqueueIssue({ projectEnabled: false, issueEligible: true, items: [], projectId, issueNumber: 42 }), false);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: false, items: [], projectId, issueNumber: 42 }), false);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [execution('q', 'Queued')], projectId, issueNumber: 42 }), false);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [execution('r', 'Running')], projectId, issueNumber: 42 }), false);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [execution('other', 'Running', 'project-beta')], projectId, issueNumber: 42 }), true);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [], projectId, issueNumber: 42, locked: true }), false);
  assert.equal(canEnqueueIssue({ projectEnabled: true, issueEligible: true, items: [], projectId, issueNumber: 42, available: false }), false);
});

test('history outcome prefers completion summary, then failure and validation evidence', () => {
  assert.equal(executionOutcome({ completionSummary: '  Done  ', failureClassification: 'Task' }), 'Done');
  assert.equal(executionOutcome({ failureClassification: 'InfrastructureFailure' }), 'InfrastructureFailure');
  assert.equal(executionOutcome({ integrationResult: 'integrated' }), 'integrated');
  assert.equal(executionOutcome({ validationResult: 'passed' }), 'passed');
  assert.equal(executionOutcome({}), undefined);
});

test('GitHub issue state and Codex Server scheduling state remain independent', () => {
  const queued = [execution('q', 'Queued')];
  assert.equal(activeIssueExecution(queued, projectId, 42).state, 'Queued');
  assert.equal(issueServerState({ projectAvailable: true, projectEnabled: true, issueAvailable: true,
    issueEligible: true, items: queued, projectId, issueNumber: 42 }).state, 'queued');
  assert.equal(issueServerState({ projectAvailable: true, projectEnabled: true, issueAvailable: true,
    issueEligible: true, items: [execution('a', 'Assigned')], projectId, issueNumber: 42 }).state, 'assigned');
  assert.equal(issueServerState({ projectAvailable: true, projectEnabled: true, issueAvailable: true,
    issueEligible: false, items: [], projectId, issueNumber: 42 }).state, 'blocked');
  assert.equal(issueServerState({ projectAvailable: true, projectEnabled: true, issueAvailable: true,
    issueEligible: true, items: [], projectId, issueNumber: 42 }).state, 'no-record');
  assert.equal(issueServerState({ projectAvailable: false, projectEnabled: false, issueAvailable: true,
    issueEligible: true, items: [], projectId, issueNumber: 42 }).state, 'unknown');
  assert.equal(issueServerState({ projectAvailable: true, projectEnabled: true, issueAvailable: true,
    issueEligible: true, items: [execution('r', 'Running')], projectId, issueNumber: 42 }).state, 'running');
});

test('Issue detail renders GitHub state and Codex execution state from their separate sources', () => {
  const page = readFileSync(resolve(import.meta.dirname, '../src/features/projects/IssueHistoryPage.tsx'), 'utf8');
  assert.match(page, /<SourcePanel title=\{t\('issues\.githubIssue'\)\}/);
  assert.match(page, /<StatusBadge tone=\{currentIssue\.state\.toLowerCase\(\) === 'open' \? 'success' : 'gray'\}>\{localizeText\(currentIssue\.state\)\}/);
  assert.match(page, /<SourcePanel title=\{t\('issues\.codexServer'\)\}/);
  assert.match(page, /<StatusBadge tone=\{serverTone\(current\.state\)\} active=\{current\.state === 'running'\}>\{serverLabel\(current\.state\)\}/);
  assert.match(page, /issueRead\.error && <Notice error>/);
  assert.match(page, /statusRead\.error && <Notice error>/);
  assert.match(page, /executionOutcome\(item\)/);
});
