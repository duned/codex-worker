import { useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { Dialog, DialogTrigger, Heading, Popover } from 'react-aria-components';
import { InfoCircle } from '@untitledui/icons';
import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useApiRead } from '../../shared/api/session';
import { workers } from '../../shared/api/validation';
import { PageHeading, Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import { TableCard } from '../../untitled/components/application/table/table';
import { ExternalLink } from '../../shared/Actions';
import { Button } from '../../untitled/components/base/buttons/button';
import { timestamp, duration, statusColor, isActiveExecutionState } from '../../model';
import { executionList, type Execution } from '../executions/model';
import type { ExecutionSummary } from '../../shared/api/contracts';
import { IssueEditor } from './Issues';
import { issue, project, type Issue, type Project } from './contracts';
import { issueDoneLabel, issueHistoryPageSize, issueHistoryPath, issueExecutions, issueStatusPath, historyOffset, canEnqueueIssue, issuePageState, executionOutcome } from './issue-history-model.mjs';
import { issuePath, projectPath } from './model';
import { fenceKey, useProjects } from './Workspace';
import { IssueMarkdown } from './IssueMarkdown';
import { IssueDetailLayout } from './IssueDetailLayout';
import { IssueDetailsPanel } from './IssueDetailsPanel';

function validIssueNumber(value: string | undefined) {
  if (!value || !/^[1-9]\d*$/.test(value)) return undefined;
  const number = Number(value);
  return Number.isSafeInteger(number) && number <= 2147483647 ? number : undefined;
}

export function IssueHistoryRoute() {
  useLanguage();
  const { projectId, issueNumber: issueRouteNumber } = useParams();
  const number = validIssueNumber(issueRouteNumber);
  if (!projectId || number === undefined) return <ViewState title={t('issues.invalidIdentity')} error />;
  return <IssueHistoryPage key={`${projectId}:${number}`} projectId={projectId} issueNumber={number} />;
}

function IssueHistoryPage({ projectId, issueNumber }: { projectId: string; issueNumber: number }) {
  useLanguage();
  const location = useLocation(), [params, setParams] = useSearchParams(), workspace = useProjects();
  const offset = historyOffset(location.search);
  const projectRead = useApiRead(projectPath(projectId), project);
  const issueRead = useApiRead(issuePath(projectId, issueNumber), value => {
    const observed = issue(value);
    if (observed.number !== issueNumber) throw Error(t('issues.identityMismatch'));
    return observed;
  });
  const historyRead = useApiRead(issueHistoryPath(projectId, issueNumber, offset), executionList);
  const statusRead = useApiRead(issueStatusPath(projectId, issueNumber), executionList);
  const workerRead = useApiRead('/api/v1/workers', workers);
  const [refreshing, setRefreshing] = useState(false), [reconciling, setReconciling] = useState(false);
  const currentProject = projectRead.retainedData;
  const currentIssue = issueRead.retainedData;
  const retainedAttempt = workspace.attempts[fenceKey(projectId)];
  const historyItems: Execution[] | undefined = historyRead.retainedData
    ? issueExecutions(historyRead.retainedData, projectId, issueNumber)
    : undefined;
  const statusItems = statusRead.retainedData ?? [];
  const displayTitle = currentIssue?.title?.trim()
    || [...statusItems, ...(historyItems ?? [])].find(item => item.workReference?.title?.trim())?.workReference?.title;
  const current = issuePageState({
    projectAvailable: !!currentProject,
    projectEnabled: currentProject?.enabled === true,
    issueAvailable: !!currentIssue,
    serverAvailable: !!statusRead.retainedData,
    issueEligible: currentIssue?.isEligible === true,
    githubState: currentIssue?.state,
    labels: currentIssue?.labels ?? [],
    doneLabel: issueDoneLabel,
    items: statusItems,
    projectId,
    issueNumber
  });
  const enqueueAllowed = !!currentProject && !!currentIssue && canEnqueueIssue({
    projectEnabled: currentProject.enabled,
    issueEligible: currentIssue.isEligible,
    githubState: currentIssue.state,
    completed: current.completed,
    items: statusItems,
    projectId,
    issueNumber,
    available: !projectRead.error && !issueRead.error && !!statusRead.retainedData && !statusRead.error,
    locked: workspace.locked(projectId) || !!workspace.issueDraft &&
      (workspace.issueDraft.project.id !== projectId || workspace.issueDraft.before?.number !== issueNumber)
  });
  const issueDraftMatches = workspace.issueDraft?.project.id === projectId && workspace.issueDraft.before?.number === issueNumber;
  const repositoryUrl = currentProject && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(currentProject.repository)
    ? `https://github.com/${currentProject.repository}` : undefined;
  const issueUrl = currentIssue?.url;
  const newestRecord = [...current.matching].sort((left, right) => Date.parse(executionActivity(right)) - Date.parse(executionActivity(left)))[0];
  const latestActivity = newestRecord ? executionActivity(newestRecord) : undefined;

  async function refresh() {
    if (refreshing) return;
    setRefreshing(true);
    try { await Promise.all([issueRead.refetch(), projectRead.refetch(), statusRead.refetch(), historyRead.refetch()]); }
    finally { setRefreshing(false); }
  }
  function setHistoryOffset(nextOffset: number) {
    const next = new URLSearchParams(params);
    if (nextOffset) next.set('historyOffset', String(nextOffset));
    else next.delete('historyOffset');
    setParams(next);
  }
  function openIssueAction(kind: 'enqueue' | 'refresh') {
    if (!currentProject || !currentIssue) return;
    workspace.setIssueDraft({ project: currentProject, before: currentIssue, change: { kind }, open: true });
  }
  async function reconcileIssueAction() {
    setReconciling(true);
    try { await workspace.reconcile(fenceKey(projectId), issueNumber); }
    finally { setReconciling(false); }
  }

  return <>
    <PageHeading
      title={displayTitle ? t('issues.heading', { number: issueNumber, title: displayTitle }) : t('issues.issueNumber', { number: issueNumber })}
      breadcrumbs={[
        { label: t('issues.issues'), href: `/projects/${encodeURIComponent(projectId)}` },
        { label: currentProject?.name ?? projectId, href: `/projects/${encodeURIComponent(projectId)}` },
        { label: `#${issueNumber}` }
      ]}
      actions={<div className="flex flex-wrap gap-2">
        <Button isDisabled={!enqueueAllowed || !!workspace.issueDraft} onPress={() => openIssueAction('enqueue')}>{t('issues.enqueue')}</Button>
        {!current.completed && current.active?.state === 'Queued' && currentProject && currentIssue && <Button color="secondary" isDisabled={workspace.locked(projectId) || !!workspace.issueDraft} onPress={() => openIssueAction('refresh')}>{t('issues.refreshEligibility')}</Button>}
        <Button color="secondary" isDisabled={refreshing} onPress={() => { void refresh(); }}>{refreshing ? t('issues.refreshing') : t('issues.refreshStatus')}</Button>
      </div>}
    />
    <div className="mb-6 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-tertiary">
      {currentProject ? repositoryUrl ? <ExternalLink href={repositoryUrl}>{currentProject.repository}</ExternalLink> : <span>{currentProject.repository}</span> : <span>{projectRead.error ? t('issues.projectUnavailable') : t('issues.loadingProject')}</span>}
      <span aria-hidden="true">·</span>
      <span>{t('issues.sourcesSummary')}</span>
      {issueUrl && <ExternalLink href={issueUrl}>{t('issues.openGitHub')}</ExternalLink>}
    </div>

    <IssueDetailLayout
      description={<section aria-labelledby="issue-description-heading" className="max-h-[100vh] min-w-0 overflow-y-auto rounded-xl border border-secondary bg-primary p-5 shadow-xs">
        <h2 id="issue-description-heading" className="text-lg font-semibold text-primary">{t('issues.description')}</h2>
        {!currentIssue ? <p className="mt-4 text-sm text-tertiary">{issueRead.error ? t('issues.descriptionUnavailable') : t('issues.loadingGitHub')}</p> : <>
          {issueRead.error && <Notice error>{t('issues.githubStale')}</Notice>}
          <div className="mt-4">{currentIssue.body ? <IssueMarkdown source={currentIssue.body} /> : <p className="text-sm text-tertiary">{t('issues.noDescription')}</p>}</div>
        </>}
      </section>}
      status={<IssueStatusSummary
        issue={currentIssue}
        project={currentProject}
        state={current}
        issueError={issueRead.error}
        statusError={statusRead.error}
        statusUnavailable={!statusRead.retainedData}
        statusLoading={statusRead.loading}
        latestActivity={latestActivity}
        issueUrl={issueUrl}
        projectId={projectId}
        issueNumber={issueNumber}
        onRefreshGitHub={() => { void issueRead.refetch(); }}
        onRefreshServer={() => { void statusRead.refetch(); }}
      />}
      details={<IssueDetailsPanel
        issue={currentIssue}
        project={currentProject}
        projectId={projectId}
        issueError={issueRead.error}
        projectError={!!projectRead.error}
        onRetryProject={() => { void projectRead.refetch(); }}
      />}
    />

    <section aria-labelledby="issue-execution-history-heading" className="mt-8">
      <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
        <div><h2 id="issue-execution-history-heading" className="text-lg font-semibold text-primary">{t('issues.executionHistory')}</h2><p className="mt-1 text-sm text-tertiary">{t('issues.historyScope')}</p></div>
        <Link className="text-sm font-medium text-brand-secondary hover:underline" to={`/executions?project=${encodeURIComponent(projectId)}&issue=${issueNumber}`}>{t('issues.viewExecutions')}</Link>
      </div>
      {historyRead.error && !historyRead.retainedData ? <ViewState title={t('issues.historyUnavailable')} error><Button color="secondary" onPress={() => { void historyRead.refetch(); }}>{t('issues.retryHistory')}</Button></ViewState> : !historyItems ? <ViewState title={t('issues.loadingHistory')} /> : historyItems.length === 0 ? <ViewState title={offset ? t('issues.noHistoryOnPage') : t('issues.noHistory')} /> : <TableCard.Root>
        {historyRead.error && <div className="p-4"><Notice error>{t('issues.historyStale')}</Notice></div>}
        <div role="table" aria-label={t('issues.executionHistory')}>
          <div role="rowgroup" className="hidden border-b border-secondary bg-secondary px-4 py-3 text-xs font-semibold uppercase tracking-wide text-tertiary sm:block">
            <div role="row" className="grid grid-cols-[minmax(12rem,1.2fr)_minmax(9rem,0.9fr)_minmax(8rem,0.8fr)_minmax(9rem,0.8fr)_minmax(7rem,0.6fr)] gap-3">
              {[t('issues.execution'), t('issues.statusOutcome'), t('issues.worker'), t('issues.started'), t('issues.duration')].map(label => <span key={label} role="columnheader">{label}</span>)}
            </div>
          </div>
          <div role="rowgroup" className="divide-y divide-secondary">
          {historyItems.map(item => <ExecutionHistoryRow key={item.id} item={item} workers={workerRead.retainedData ?? []} />)}
          </div>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-secondary p-4">
          <p className="text-sm text-tertiary">{t('issues.showingPage', { from: offset + 1, to: offset + historyItems.length })}</p>
          <div className="flex flex-wrap gap-2">
            <Button color="secondary" isDisabled={offset === 0 || historyRead.loading} onPress={() => setHistoryOffset(Math.max(0, offset - issueHistoryPageSize))}>{t('issues.previousHistory')}</Button>
            <Button color="secondary" isDisabled={historyItems.length < issueHistoryPageSize || offset >= 10000 || historyRead.loading} onPress={() => setHistoryOffset(Math.min(10000, offset + issueHistoryPageSize))}>{t('issues.olderHistory')}</Button>
          </div>
        </div>
      </TableCard.Root>}
    </section>
    {workspace.issueDraft && !issueDraftMatches && <div className="mt-6"><Notice error>{t('issues.anotherIssueActionRetained')} <Link className="font-medium text-brand-secondary hover:underline" to="/projects">{t('issues.openProjects')}</Link></Notice></div>}
    {workspace.issueDraft && issueDraftMatches && !workspace.issueDraft.open && <div className="mt-6"><Notice>{t('issues.issueActionRetained')} <Button className="ml-2" color="secondary" onPress={() => workspace.setIssueDraft(draft => draft ? { ...draft, open: true } : draft)}>{t('issues.resumeAction')}</Button></Notice></div>}
    {retainedAttempt && <div className="mt-6"><Notice error><p>{t('issues.actionUnconfirmed')} {localizeText(retainedAttempt.kind)}</p><Button className="mt-3" color="secondary" isDisabled={reconciling} onPress={() => { void reconcileIssueAction(); }}>{reconciling ? t('issues.reconciling') : t('issues.reconcileAction')}</Button></Notice></div>}
    {workspace.issueDraft && issueDraftMatches && <IssueEditor key={`${projectId}-${issueNumber}-${workspace.issueDraft.change.kind}`} />}
  </>;
}

function IssueStatusSummary({ issue: item, project: currentProject, state, issueError, statusError, statusUnavailable, statusLoading,
  latestActivity, issueUrl, projectId, issueNumber, onRefreshGitHub, onRefreshServer }: {
  issue?: Issue; project?: Project; state: { state: string; active?: ExecutionSummary; matching: ExecutionSummary[]; completed: boolean; latest?: ExecutionSummary };
  issueError: unknown; statusError: unknown; statusUnavailable: boolean; statusLoading: boolean; latestActivity?: string;
  issueUrl?: string; projectId: string; issueNumber: number; onRefreshGitHub: () => void; onRefreshServer: () => void;
}) {
  useLanguage();
  const summaryText = issueStateExplanation(state.state, currentProject, item);
  return <aside aria-labelledby="issue-state-heading" className="min-w-0 rounded-xl border border-secondary bg-primary p-5 shadow-xs">
    <h2 id="issue-state-heading" className="text-lg font-semibold text-primary">{t('issues.stateSummary')}</h2>
    {item && !!issueError && <div className="mt-3"><Notice error>{t('issues.githubStale')}</Notice></div>}
    {!!statusError && statusUnavailable && <div className="mt-3"><Notice error>{t('issues.serverStatusUnavailable')}</Notice></div>}
    {!!statusError && !statusUnavailable && <div className="mt-3"><Notice error>{t('issues.serverStatusStale')}</Notice></div>}
    {!item ? <div className="mt-4"><ViewState title={issueError ? t('issues.githubUnavailable') : t('issues.loadingGitHub')} error={!!issueError}>
      {!!issueError && <Button color="secondary" onPress={onRefreshGitHub}>{t('issues.retryGitHub')}</Button>}
    </ViewState></div> : <div className="mt-4 space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <StatusBadge tone={issueStateTone(state.state)} active={state.state === 'running'}>{issueStateLabel(state.state)}</StatusBadge>
        <StatusBadge tone={item.state.toLowerCase() === 'open' ? 'success' : 'gray'} compact>{localizeText(item.state)}</StatusBadge>
      </div>
      <p className="text-sm text-secondary">{summaryText}</p>
      {state.state === 'completed' && <p className="text-sm text-success-primary">{t('issues.completedLabelPresent', { label: issueDoneLabel })}</p>}
      {state.state === 'closed' && <p className="text-sm text-warning-primary">{t('issues.closedDoneLabelMissing', { label: issueDoneLabel })}</p>}
      {state.state === 'blocked' && currentProject?.enabled && !item.isEligible && item.eligibilityReasons.length > 0 && <ul className="list-disc space-y-1 pl-5 text-sm text-warning-primary">{item.eligibilityReasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {state.active && state.state !== 'completed' && <p className="flex flex-wrap items-center gap-2 text-sm text-secondary"><span>{t('issues.serverExecution')}</span><StatusBadge tone={serverTone(state.active.state)} active={state.active.state === 'Running'} compact>{localizeText(state.active.state)}</StatusBadge><Link className="break-all font-medium text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(state.active.id)}`}>{t('issues.currentExecution', { id: state.active.id })}</Link></p>}
      {(state.state === 'ready' || state.state === 'failed') && <p className="flex flex-wrap items-center gap-2 text-sm text-secondary"><span>{t('issues.serverEligibility')}</span><StatusBadge tone="success" compact>{t('issues.eligible')}</StatusBadge></p>}
      {state.state === 'blocked' && <p className="flex flex-wrap items-center gap-2 text-sm text-secondary"><span>{t('issues.serverEligibility')}</span><StatusBadge tone="warning" compact>{!currentProject?.enabled ? t('issues.projectDisabledShort') : t('issues.ineligible')}</StatusBadge></p>}
      {state.state === 'closed' && !statusUnavailable && <p className="flex flex-wrap items-center gap-2 text-sm text-secondary"><span>{t('issues.serverEligibility')}</span><StatusBadge tone={item.isEligible ? 'success' : 'warning'} compact>{item.isEligible ? t('issues.eligible') : t('issues.ineligible')}</StatusBadge></p>}
      {state.state === 'failed' && state.latest && <p className="text-sm text-warning-primary">{t('issues.latestExecutionFailed')} <Link className="font-medium text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(state.latest.id)}`}>{t('issues.executionIdentity', { id: state.latest.id })}</Link></p>}
      {latestActivity && state.matching.length > 0 && <p className="text-xs text-tertiary">{t('issues.lastServerActivity', { time: timestamp(latestActivity) })}</p>}
      {state.state === 'unknown' && !statusError && <p className="text-sm text-tertiary">{statusUnavailable ? statusLoading ? t('issues.loadingServerStatus') : t('issues.serverStatusUnavailable') : t('issues.serverStateNeedsSources')}</p>}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-secondary pt-3 text-sm">
        {issueUrl && <ExternalLink href={issueUrl}>{t('issues.openGitHub')}</ExternalLink>}
        <button type="button" className="min-h-10 rounded px-2 font-medium text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" onClick={onRefreshGitHub}>{t('issues.refreshGitHub')}</button>
        {!!statusError && <button type="button" className="min-h-10 rounded px-2 font-medium text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" onClick={onRefreshServer}>{t('issues.retryServer')}</button>}
        <Link className="font-medium text-brand-secondary hover:underline" to={`/executions?project=${encodeURIComponent(projectId)}&issue=${issueNumber}`}>{t('issues.viewExecutions')}</Link>
      </div>
      <p className="text-xs text-tertiary">{t('issues.githubUpdated', { time: timestamp(item.updatedAtUtc) })}</p>
    </div>}
  </aside>;
}

function ExecutionHistoryRow({ item, workers: workerItems }: { item: Execution; workers: { workerId: string; displayName?: string }[] }) {
  const worker = workerItems.find(value => value.workerId === item.assignedWorkerId);
  const outcome = executionOutcome(item);
  return <div role="row" className="grid min-w-0 gap-3 p-4 sm:grid-cols-[minmax(12rem,1.2fr)_minmax(9rem,0.9fr)_minmax(8rem,0.8fr)_minmax(9rem,0.8fr)_minmax(7rem,0.6fr)] sm:items-center">
    <div role="cell" className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.execution')}</p><Link className="mt-1 block break-all font-medium text-brand-secondary hover:underline sm:mt-0" to={`/executions/${encodeURIComponent(item.id)}`}>{t('issues.executionIdentity', { id: item.id })}</Link><p className="mt-1 text-xs text-tertiary">{t('issues.executionRequestTime', { time: timestamp(item.createdAtUtc) })}</p></div>
    <div role="cell"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.statusOutcome')}</p><div className="mt-1 flex flex-wrap items-center gap-2 sm:mt-0"><StatusBadge tone={statusColor(item.state)} active={isActiveExecutionState(item.state)}>{localizeText(item.state)}</StatusBadge>{outcome ? <DialogTrigger>
      <Button color="tertiary" size="sm" className="!min-h-10 !min-w-10 !p-2 text-current" iconLeading={InfoCircle} aria-label={t('issues.viewOutcomeDetailsFor', { id: item.id })} />
      <Popover placement="bottom end" className="z-50 max-w-[min(24rem,calc(100vw-2rem))] rounded-xl border border-secondary bg-primary p-4 shadow-lg">
        <Dialog className="max-h-[min(70vh,28rem)] overflow-y-auto outline-none">
          <Heading slot="title" className="mb-3 text-sm font-semibold text-primary">{t('issues.executionOutcomeDetails')}</Heading>
          <p className="whitespace-pre-wrap break-words text-sm text-secondary">{localizeText(outcome)}</p>
        </Dialog>
      </Popover>
    </DialogTrigger> : <span className="text-xs text-tertiary">{t('issues.outcomeUnavailable')}</span>}</div></div>
    <div role="cell"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.worker')}</p><p className="mt-1 break-words text-sm text-secondary sm:mt-0">{worker?.displayName ?? item.assignedWorkerId ?? t('issues.workerUnassigned')}</p></div>
    <div role="cell"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.started')}</p><p className="mt-1 text-sm text-secondary sm:mt-0">{item.startedAtUtc ? timestamp(item.startedAtUtc) : t('issues.notStarted')}</p></div>
    <div role="cell"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.duration')}</p><p className="mt-1 text-sm text-secondary sm:mt-0">{duration(item, Date.now())}</p></div>
  </div>;
}

function executionActivity(item: ExecutionSummary) {
  return [item.createdAtUtc, item.assignedAtUtc, item.startedAtUtc, item.completedAtUtc]
    .filter((value): value is string => typeof value === 'string' && Number.isFinite(Date.parse(value)))
    .sort((left, right) => Date.parse(right) - Date.parse(left))[0] ?? item.createdAtUtc;
}

function issueStateTone(state: string): 'gray' | 'success' | 'warning' | 'error' | 'info' {
  if (state === 'completed' || state === 'ready') return 'success';
  if (state === 'failed') return 'error';
  if (state === 'blocked' || state === 'closed') return 'warning';
  if (state === 'running' || state === 'assigned' || state === 'queued') return 'info';
  return 'gray';
}

function issueStateLabel(state: string) {
  const labels: Record<string, string> = {
    running: t('issues.running'), assigned: t('issues.assigned'), queued: t('issues.queued'), blocked: t('issues.blocked'),
    ready: t('issues.ready'), failed: t('issues.failedRetryable'), closed: t('issues.closedNotDone'), completed: t('issues.completed'), unknown: t('issues.serverUnknown')
  };
  return labels[state] ?? t('issues.serverUnknown');
}

function issueStateExplanation(state: string, project: Project | undefined, item: Issue | undefined) {
  if (state === 'completed') return t('issues.completedSummary');
  if (state === 'closed') return t('issues.closedSummary');
  if (state === 'running' || state === 'assigned' || state === 'queued') return t('issues.activeExecutionState');
  if (state === 'blocked') return !project?.enabled ? t('issues.projectDisabled') : t('issues.issueIneligible');
  if (state === 'failed') return t('issues.failedRetryableSummary');
  if (state === 'ready') return t('issues.readyForScheduling');
  return item ? t('issues.serverStateUnavailable') : t('issues.githubUnavailable');
}

function serverTone(state: string): 'gray' | 'success' | 'warning' | 'error' | 'info' {
  return state === 'Running' || state === 'Assigned' || state === 'Queued' ? 'info' : state === 'Completed' ? 'success' : state === 'Failed' ? 'error' : 'gray';
}
