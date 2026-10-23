const activeStates = new Set(['Queued', 'Assigned', 'Running']);
const issueTypes = new Set(['issue', 'github-issue']);

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

export function canEnqueueIssue({ projectEnabled, issueEligible, items, projectId, issueNumber, locked = false, available = true }) {
  return available && projectEnabled && issueEligible && !locked &&
    !activeIssueExecution(items, projectId, issueNumber);
}

export function issueServerState({ projectAvailable, projectEnabled, issueAvailable, issueEligible, items, projectId, issueNumber }) {
  const matching = issueExecutions(items, projectId, issueNumber);
  const active = activeIssueExecution(matching, projectId, issueNumber);
  if (active) return {
    state: active.state === 'Running' ? 'running' : active.state === 'Assigned' ? 'assigned' : 'queued',
    active, matching
  };
  if (!projectAvailable || !issueAvailable) return { state: 'unknown', active: undefined, matching };
  if (!projectEnabled || !issueEligible) return { state: 'blocked', active: undefined, matching };
  return { state: matching.length ? 'ready' : 'no-record', active: undefined, matching };
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
