import { useState, type ReactNode } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
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
import { issueHistoryPageSize, issueHistoryPath, issueExecutions, issueStatusPath, historyOffset, canEnqueueIssue, issueServerState, executionOutcome } from './issue-history-model.mjs';
import { issuePath, projectPath } from './model';
import { fenceKey, useProjects } from './Workspace';

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
  const current = issueServerState({
    projectAvailable: !!currentProject,
    projectEnabled: currentProject?.enabled === true,
    issueAvailable: !!currentIssue,
    issueEligible: currentIssue?.isEligible === true,
    items: statusItems,
    projectId,
    issueNumber
  });
  const enqueueAllowed = !!currentProject && !!currentIssue && canEnqueueIssue({
    projectEnabled: currentProject.enabled,
    issueEligible: currentIssue.isEligible,
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
      title={currentIssue ? t('issues.heading', { number: issueNumber, title: currentIssue.title }) : t('issues.issueNumber', { number: issueNumber })}
      breadcrumbs={[
        { label: t('issues.issues'), href: `/projects/${encodeURIComponent(projectId)}` },
        { label: currentProject?.name ?? projectId, href: `/projects/${encodeURIComponent(projectId)}` },
        { label: `#${issueNumber}` }
      ]}
      actions={<div className="flex flex-wrap gap-2">
        <Button isDisabled={!enqueueAllowed || !!workspace.issueDraft} onPress={() => openIssueAction('enqueue')}>{t('issues.enqueue')}</Button>
        {current.active?.state === 'Queued' && currentProject && currentIssue && <Button color="secondary" isDisabled={workspace.locked(projectId) || !!workspace.issueDraft} onPress={() => openIssueAction('refresh')}>{t('issues.refreshEligibility')}</Button>}
        <Button color="secondary" isDisabled={refreshing} onPress={() => { void refresh(); }}>{refreshing ? t('issues.refreshing') : t('issues.refreshStatus')}</Button>
      </div>}
    />
    <div className="mb-6 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-tertiary">
      {currentProject ? repositoryUrl ? <ExternalLink href={repositoryUrl}>{currentProject.repository}</ExternalLink> : <span>{currentProject.repository}</span> : <span>{projectRead.error ? t('issues.projectUnavailable') : t('issues.loadingProject')}</span>}
      <span aria-hidden="true">·</span>
      <span>{t('issues.sourcesSummary')}</span>
      {issueUrl && <ExternalLink href={issueUrl}>{t('issues.openGitHub')}</ExternalLink>}
    </div>

    <div className="grid gap-4 lg:grid-cols-2">
      <SourcePanel title={t('issues.githubIssue')} source={t('issues.githubSource')}>
        {!currentIssue ? <ViewState title={issueRead.error ? t('issues.githubUnavailable') : t('issues.loadingGitHub')} error={!!issueRead.error}>
          {issueRead.error && <Button color="secondary" onPress={() => { void issueRead.refetch(); }}>{t('issues.retryGitHub')}</Button>}
        </ViewState> : <div className="space-y-4 rounded-xl border border-secondary bg-primary p-5 shadow-xs">
          {issueRead.error && <Notice error>{t('issues.githubStale')}</Notice>}
          <div className="flex flex-wrap gap-2">
            <StatusBadge tone={currentIssue.state.toLowerCase() === 'open' ? 'success' : 'gray'}>{localizeText(currentIssue.state)}</StatusBadge>
            {currentIssue.labels.map(label => <StatusBadge key={label} tone="purple" compact>{label}</StatusBadge>)}
          </div>
          <p className="text-sm text-secondary">{t('issues.githubUpdated', { time: timestamp(currentIssue.updatedAtUtc) })}</p>
          {currentIssue.eligibilityReasons.length > 0 && <ul className="list-disc space-y-1 pl-5 text-sm text-warning-primary">{currentIssue.eligibilityReasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
          <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span className="text-tertiary">{t('issues.githubIsSource')}</span>
            <div className="flex flex-wrap gap-3">
              {issueUrl && <ExternalLink href={issueUrl}>{t('issues.openGitHub')}</ExternalLink>}
              <button type="button" className="min-h-10 rounded px-2 font-medium text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" onClick={() => { void issueRead.refetch(); }}>{t('issues.refreshGitHub')}</button>
            </div>
          </div>
        </div>}
      </SourcePanel>

      <SourcePanel title={t('issues.codexServer')} source={t('issues.serverSource')}>
        {statusRead.error && !statusRead.retainedData ? <ViewState title={t('issues.serverStatusUnavailable')} error>
          <Button color="secondary" onPress={() => { void statusRead.refetch(); }}>{t('issues.retryServer')}</Button>
        </ViewState> : !statusRead.retainedData ? <ViewState title={t('issues.loadingServerStatus')} /> : <div className="space-y-4 rounded-xl border border-secondary bg-primary p-5 shadow-xs">
          {statusRead.error && <Notice error>{t('issues.serverStatusStale')}</Notice>}
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge tone={serverTone(current.state)} active={current.state === 'running'}>{serverLabel(current.state)}</StatusBadge>
            {current.active && <Link className="break-all text-sm font-medium text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(current.active.id)}`}>{t('issues.currentExecution', { id: current.active.id })}</Link>}
          </div>
          {currentIssue && <p className="flex flex-wrap items-center gap-2 text-sm text-secondary">{t('issues.serverEligibility')}<StatusBadge tone={currentIssue.isEligible ? 'success' : 'warning'} compact>{currentIssue.isEligible ? t('issues.eligible') : t('issues.ineligible')}</StatusBadge></p>}
          <p className="text-sm text-secondary">{serverExplanation(current.state, currentProject, currentIssue)}</p>
          <p className="text-sm text-tertiary">{latestActivity ? t('issues.lastServerActivity', { time: timestamp(latestActivity) }) : current.matching.length ? t('issues.serverActivityUnavailable') : t('issues.noServerUpdate')}</p>
          {current.state === 'unknown' && (projectRead.error || issueRead.error) && <p className="text-sm text-warning-primary">{t('issues.serverStateNeedsSources')}</p>}
          <Link className="text-sm font-medium text-brand-secondary hover:underline" to={`/executions?project=${encodeURIComponent(projectId)}&issue=${issueNumber}`}>{t('issues.viewExecutions')}</Link>
        </div>}
      </SourcePanel>
    </div>

    <div className="mt-6 grid gap-6 lg:grid-cols-[minmax(0,1.7fr)_minmax(18rem,1fr)]">
      <section aria-labelledby="issue-description-heading" className="min-w-0 rounded-xl border border-secondary bg-primary p-5 shadow-xs">
        <h2 id="issue-description-heading" className="text-lg font-semibold text-primary">{t('issues.description')}</h2>
        {!currentIssue ? <p className="mt-4 text-sm text-tertiary">{issueRead.error ? t('issues.descriptionUnavailable') : t('issues.loadingGitHub')}</p> : <>
          {issueRead.error && <Notice error>{t('issues.githubStale')}</Notice>}
          <div className="mt-4 whitespace-pre-wrap break-words text-sm leading-6 text-secondary">{currentIssue.body || t('issues.noDescription')}</div>
          <div className="mt-5 border-t border-secondary pt-4">
            <h3 className="text-sm font-semibold text-primary">{t('issues.labels')}</h3>
            <div className="mt-2 flex flex-wrap gap-2">{currentIssue.labels.length ? currentIssue.labels.map(label => <StatusBadge key={label} tone="purple" compact>{label}</StatusBadge>) : <span className="text-sm text-tertiary">{t('issues.none')}</span>}</div>
          </div>
        </>}
      </section>
      <section aria-labelledby="issue-details-heading" className="min-w-0 rounded-xl border border-secondary bg-primary p-5 shadow-xs">
        <h2 id="issue-details-heading" className="text-lg font-semibold text-primary">{t('issues.issueDetails')}</h2>
        {currentIssue ? <dl className="mt-4 divide-y divide-secondary text-sm">
          <DetailField label={t('issues.author')} value={currentIssue.author || t('issues.notReported')} />
          <DetailField label={t('issues.assignees')} value={currentIssue.assignees.length ? currentIssue.assignees.join(', ') : t('issues.unassigned')} />
          <DetailField label={t('issues.milestone')} value={currentIssue.milestone || t('issues.none')} />
          <DetailField label={t('issues.project')} value={currentProject?.name ?? projectId} />
          <DetailField label={t('issues.issueNumber')} value={`#${issueNumber}`} />
          <DetailField label={t('issues.created')} value={timestamp(currentIssue.createdAtUtc)} />
          <DetailField label={t('issues.updated')} value={timestamp(currentIssue.updatedAtUtc)} />
        </dl> : <p className="mt-4 text-sm text-tertiary">{issueRead.error ? t('issues.metadataUnavailable') : t('issues.loadingGitHub')}</p>}
        {projectRead.error && <div className="mt-4"><Notice error>{t('issues.projectUnavailable')}</Notice><Button className="mt-2" color="secondary" onPress={() => { void projectRead.refetch(); }}>{t('issues.retryProject')}</Button></div>}
      </section>
    </div>

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

function SourcePanel({ title, source, children }: { title: string; source: string; children: ReactNode }) {
  useLanguage();
  return <section aria-label={title} className="min-w-0"><h2 className="mb-3 text-lg font-semibold text-primary">{title}<span className="ml-2 text-xs font-normal uppercase tracking-wide text-tertiary">{source}</span></h2>{children}</section>;
}

function DetailField({ label, value }: { label: string; value: string }) {
  return <div className="grid gap-1 py-3 sm:grid-cols-[minmax(7rem,0.7fr)_minmax(0,1.3fr)]"><dt className="text-tertiary">{label}</dt><dd className="break-words font-medium text-secondary">{value}</dd></div>;
}

function ExecutionHistoryRow({ item, workers: workerItems }: { item: Execution; workers: { workerId: string; displayName?: string }[] }) {
  const worker = workerItems.find(value => value.workerId === item.assignedWorkerId);
  const outcome = executionOutcome(item);
  return <div role="row" className="grid min-w-0 gap-3 p-4 sm:grid-cols-[minmax(12rem,1.2fr)_minmax(9rem,0.9fr)_minmax(8rem,0.8fr)_minmax(9rem,0.8fr)_minmax(7rem,0.6fr)] sm:items-center">
    <div role="cell" className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.execution')}</p><Link className="mt-1 block break-all font-medium text-brand-secondary hover:underline sm:mt-0" to={`/executions/${encodeURIComponent(item.id)}`}>{t('issues.executionIdentity', { id: item.id })}</Link><p className="mt-1 text-xs text-tertiary">{t('issues.executionRequestTime', { time: timestamp(item.createdAtUtc) })}</p></div>
    <div role="cell"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary sm:hidden">{t('issues.statusOutcome')}</p><div className="mt-1 flex flex-wrap items-center gap-2 sm:mt-0"><StatusBadge tone={statusColor(item.state)} active={isActiveExecutionState(item.state)}>{localizeText(item.state)}</StatusBadge><span className="text-sm text-secondary">{outcome ? localizeText(outcome) : t('issues.outcomeUnavailable')}</span></div></div>
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

function serverTone(state: string): 'gray' | 'success' | 'warning' | 'error' | 'info' {
  return state === 'running' || state === 'assigned' || state === 'queued' ? 'info' : state === 'ready' ? 'success' : state === 'blocked' ? 'warning' : 'gray';
}

function serverLabel(state: string) {
  const labels: Record<string, string> = {
    running: t('issues.running'), assigned: t('issues.assigned'), queued: t('issues.queued'), blocked: t('issues.blocked'),
    ready: t('issues.ready'), 'no-record': t('issues.noServerRecord'), unknown: t('issues.serverUnknown')
  };
  return labels[state] ?? t('issues.serverUnknown');
}

function serverExplanation(state: string, project: Project | undefined, item: Issue | undefined) {
  if (state === 'running' || state === 'assigned' || state === 'queued') return t('issues.activeExecutionState');
  if (state === 'blocked') return !project?.enabled ? t('issues.projectDisabled') : t('issues.issueIneligible');
  if (state === 'ready') return t('issues.readyForScheduling');
  if (state === 'no-record') return t('issues.noServerRecordReady', { eligibility: item?.isEligible ? t('issues.eligible') : t('issues.ineligible') });
  return t('issues.serverStateUnavailable');
}
