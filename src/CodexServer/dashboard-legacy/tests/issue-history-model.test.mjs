import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  activeIssueExecution, canEnqueueIssue, historyOffset, issueDoneLabel, issueExecutionMatches,
  issueHistoryPageSize, issueHistoryPath, issuePageState, issueStatusPath, executionOutcome
} from '../src/features/projects/issue-history-model.mjs';

const projectId = 'project-alpha';
const execution = (id, state, project = projectId, issue = '42', dates = {}) => ({
  id, projectId: project, state, workReference: { type: 'github-issue', id: issue }, ...dates
});
const pageState = overrides => issuePageState({
  projectAvailable: true, projectEnabled: true, issueAvailable: true, serverAvailable: true, issueEligible: true,
  githubState: 'OPEN', labels: ['codex-ready'], doneLabel: issueDoneLabel, items: [], projectId, issueNumber: 42,
  ...overrides
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

test('enqueue is limited to open eligible Issues without duplicate active work', () => {
  const base = { projectEnabled: true, issueEligible: true, githubState: 'OPEN', items: [], projectId, issueNumber: 42 };
  assert.equal(canEnqueueIssue(base), true);
  assert.equal(canEnqueueIssue({ ...base, projectEnabled: false }), false);
  assert.equal(canEnqueueIssue({ ...base, issueEligible: false }), false);
  assert.equal(canEnqueueIssue({ ...base, githubState: 'CLOSED' }), false);
  assert.equal(canEnqueueIssue({ ...base, completed: true }), false);
  assert.equal(canEnqueueIssue({ ...base, items: [execution('q', 'Queued')] }), false);
  assert.equal(canEnqueueIssue({ ...base, items: [execution('r', 'Running')] }), false);
  assert.equal(canEnqueueIssue({ ...base, items: [execution('other', 'Running', 'project-beta')] }), true);
  assert.equal(canEnqueueIssue({ ...base, locked: true }), false);
  assert.equal(canEnqueueIssue({ ...base, available: false }), false);
});

test('history outcome prefers completion summary, then failure and validation evidence', () => {
  assert.equal(executionOutcome({ completionSummary: '  Done  ', failureClassification: 'Task' }), 'Done');
  assert.equal(executionOutcome({ failureClassification: 'InfrastructureFailure' }), 'InfrastructureFailure');
  assert.equal(executionOutcome({ integrationResult: 'integrated' }), 'integrated');
  assert.equal(executionOutcome({ validationResult: 'passed' }), 'passed');
  assert.equal(executionOutcome({}), undefined);
});

test('Issue status projection treats closed Issues with the configured done label as successful', () => {
  const completed = pageState({
    githubState: 'CLOSED', labels: ['triaged', 'CoDeX-DoNe'], issueEligible: false, projectEnabled: false,
    items: [execution('run-1', 'Failed')]
  });
  assert.equal(completed.state, 'completed');
  assert.equal(completed.completed, true);
  assert.equal(completed.active, undefined);
  assert.equal(issueDoneLabel, 'codex-done');

  const projectConfiguredLabel = pageState({ githubState: 'closed', doneLabel: 'shipped', labels: ['SHIPPED'] });
  assert.equal(projectConfiguredLabel.state, 'completed');
  assert.equal(projectConfiguredLabel.completed, true);
});

test('Issue status projection keeps open, queued, running and retryable states distinct', () => {
  assert.equal(pageState().state, 'ready');
  const queued = [execution('q', 'Queued')];
  assert.equal(activeIssueExecution(queued, projectId, 42).state, 'Queued');
  assert.equal(pageState({ items: queued }).state, 'queued');
  assert.equal(pageState({ items: [execution('a', 'Assigned')] }).state, 'assigned');
  assert.equal(pageState({ items: [execution('r', 'Running')] }).state, 'running');
  assert.equal(pageState({ items: [execution('f', 'Failed', projectId, '42', { createdAtUtc: '2026-01-01T00:00:00Z', completedAtUtc: '2026-01-01T00:01:00Z' })] }).state, 'failed');
  assert.equal(pageState({ items: [
    execution('f', 'Failed', projectId, '42', { createdAtUtc: '2026-01-01T00:00:00Z', completedAtUtc: '2026-01-01T00:01:00Z' }),
    execution('c', 'Completed', projectId, '42', { createdAtUtc: '2026-01-02T00:00:00Z', completedAtUtc: '2026-01-02T00:01:00Z' })
  ] }).state, 'ready');
});

test('Issue status projection distinguishes blocked, closed without done label, and unavailable states', () => {
  assert.equal(pageState({ issueEligible: false }).state, 'blocked');
  assert.equal(pageState({ projectEnabled: false }).state, 'blocked');
  const closed = pageState({ githubState: 'CLOSED', labels: ['codex-ready'] });
  assert.equal(closed.state, 'closed');
  assert.equal(closed.completed, false);
  assert.equal(pageState({ serverAvailable: false }).state, 'unknown');
});

test('Issue description renders safe Markdown blocks and excludes unsafe link protocols', async () => {
  const { parseIssueMarkdown, safeMarkdownHref } = await import('../src/features/projects/issue-markdown.mjs');
  const blocks = parseIssueMarkdown('# Summary\n\nA [safe link](https://example.com) and `code`.\n\n- First\n- Second\n\n1. Third\n2. Fourth\n\n> Quote\n\n```js\nalert(1)\n```');
  assert.deepEqual(blocks.map(block => block.type), ['heading', 'paragraph', 'list', 'list', 'quote', 'code']);
  assert.equal(blocks[1].children.find(token => token.type === 'link').href, 'https://example.com/');
  assert.equal(blocks[1].children.some(token => token.type === 'code'), true);
  assert.equal(blocks[2].items.length, 2);
  assert.equal(blocks[3].ordered, true);
  assert.equal(blocks[3].items.length, 2);
  assert.equal(blocks[4].children[0].type, 'paragraph');
  assert.equal(blocks[5].language, 'js');
  assert.equal(safeMarkdownHref('javascript:alert(1)'), undefined);
  assert.equal(safeMarkdownHref('data:text/html,<script>alert(1)</script>'), undefined);
  assert.equal(safeMarkdownHref('//evil.example/path'), undefined);
  assert.equal(safeMarkdownHref('/issues/42'), '/issues/42');
  assert.deepEqual(parseIssueMarkdown('<img src=x onerror=alert(1)>')[0].children, [{ type: 'text', value: '<img src=x onerror=alert(1)>' }]);

  const renderer = readFileSync(resolve(import.meta.dirname, '../src/features/projects/IssueMarkdown.tsx'), 'utf8');
  assert.match(renderer, /parseIssueMarkdown/);
  assert.doesNotMatch(renderer, /dangerouslySetInnerHTML/);
});

test('Issue page puts a viewport-scrolling description beside the compact status summary', () => {
  const page = readFileSync(resolve(import.meta.dirname, '../src/features/projects/IssueHistoryPage.tsx'), 'utf8');
  assert.match(page, /max-h-\[100vh\].*overflow-y-auto/);
  assert.match(page, /<IssueMarkdown source=\{currentIssue\.body\}/);
  assert.match(page, /<IssueStatusSummary/);
  assert.match(page, /current\.completed/);
  assert.match(page, /onRefreshGitHub/);
  assert.match(page, /t\('issues\.refreshGitHub'\)/);
  assert.match(page, /historyItems\.length === 0 \? <ViewState title=\{offset \? t\('issues\.noHistoryOnPage'\) : t\('issues\.noHistory'\)\}/);
});

test('execution history keeps outcomes behind a keyboard and touch accessible popover', () => {
  const page = readFileSync(resolve(import.meta.dirname, '../src/features/projects/IssueHistoryPage.tsx'), 'utf8');
  assert.match(page, /<StatusBadge tone=\{statusColor\(item\.state\)\}/);
  assert.match(page, /outcome \? <DialogTrigger>/);
  assert.match(page, /<Popover placement="bottom end"/);
  assert.match(page, /aria-label=\{t\('issues\.viewOutcomeDetailsFor'/);
  assert.match(page, /!min-h-10 !min-w-10/);
  assert.match(page, /t\('issues\.outcomeUnavailable'\)/);
});
