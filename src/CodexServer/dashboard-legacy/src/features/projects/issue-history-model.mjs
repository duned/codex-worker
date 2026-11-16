const activeStates = new Set(['Queued', 'Assigned', 'Running']);
const issueTypes = new Set(['issue', 'github-issue']);
const failedStates = new Set(['Failed']);
export const issueDoneLabel = 'codex-done';

export const issueHistoryPageSize = 25;

export function issueExecutionMatches(item, projectId, issueNumber) {
  const reference = item?.workReference;
  return item?.projectId === projectId && issueTypes.has(reference?.type) &&
    typeof reference.id === 'string' && /^\d+$/.test(reference.id) &&
    Number.isSafeInteger(Number(reference.id)) && Number(reference.id) > 0 && Number(reference.id) === issueNumber;
}

export function issueExecutions(items, projectId, issueNumber) {
  return items.filter(item => issueExecutionMatches(item, projectId, issueNumber));
}

export function executionOutcome(item) {
  return item.completionSummary?.trim() || item.failureClassification ||
    item.integrationResult || item.validationResult || undefined;
}

export function activeIssueExecution(items, projectId, issueNumber) {
  return issueExecutions(items, projectId, issueNumber).find(item => activeStates.has(item.state));
}

export function canEnqueueIssue({ projectEnabled, issueEligible, githubState = 'open', completed = false, items, projectId, issueNumber, locked = false, available = true }) {
  return available && projectEnabled && issueEligible && githubState?.toLowerCase() === 'open' && !completed && !locked &&
    !activeIssueExecution(items, projectId, issueNumber);
}

export function issuePageState({ projectAvailable, projectEnabled, issueAvailable, serverAvailable = true, issueEligible,
  githubState, labels = /** @type {string[]} */ ([]), doneLabel = issueDoneLabel, items, projectId, issueNumber }) {
  const matching = issueExecutions(items, projectId, issueNumber);
  const active = activeIssueExecution(matching, projectId, issueNumber);
  const closed = githubState?.toLowerCase() === 'closed';
  const completed = closed && !!doneLabel && labels.some(label => label.toLowerCase() === doneLabel.toLowerCase());
  if (completed) return { state: 'completed', active, matching, completed: true };
  if (closed) return { state: 'closed', active, matching, completed: false };
  if (!projectAvailable || !issueAvailable || !serverAvailable) return { state: 'unknown', active: undefined, matching, completed: false };
  if (active) return {
    state: active.state === 'Running' ? 'running' : active.state === 'Assigned' ? 'assigned' : 'queued',
    active, matching, completed: false
  };
  if (!projectEnabled || !issueEligible) return { state: 'blocked', active: undefined, matching, completed: false };
  const latest = latestExecution(matching);
  if (latest && failedStates.has(latest.state)) return { state: 'failed', active: undefined, matching, completed: false, latest };
  return { state: 'ready', active: undefined, matching, completed: false };
}

function latestExecution(items) {
  return [...items].sort((left, right) => Date.parse(executionActivity(right)) - Date.parse(executionActivity(left)))[0];
}

function executionActivity(item) {
  return [item.completedAtUtc, item.startedAtUtc, item.assignedAtUtc, item.createdAtUtc]
    .find(value => typeof value === 'string' && Number.isFinite(Date.parse(value))) ?? item.createdAtUtc;
}

export function issueHistoryPath(projectId, issueNumber, offset = 0, limit = issueHistoryPageSize) {
  const query = new URLSearchParams({
    projectId,
    workType: 'github-issue',
    workId: String(issueNumber),
    limit: String(limit),
    offset: String(offset)
  });
  return `/api/v1/executions?${query}`;
}

export function issueStatusPath(projectId, issueNumber) {
  return issueHistoryPath(projectId, issueNumber, 0, 100);
}

export function historyOffset(search) {
  const raw = new URLSearchParams(search).get('historyOffset') ?? '0';
  return /^\d+$/.test(raw) ? Math.min(10000, Number(raw)) : 0;
}
