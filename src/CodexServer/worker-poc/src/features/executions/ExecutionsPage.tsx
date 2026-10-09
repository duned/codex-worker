import { t, useLanguage, statusLabel, localizeText, type TranslationKey } from '../../shared/i18n';
import { GuidDisplay } from '../../shared/GuidDisplay';
import { useEffect, useState, type ReactNode } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { ApiError } from '../../shared/api/client';
import { nodes, projects, worker as validateWorker, workers } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { PageHeading, ViewState, Notice, StatusBadge } from '../../shared/Presentation';
import { ConfirmationDialog, ActionDialog } from '../../shared/Dialogs';
import type { ExecutionMaintenanceDetail, WorkerObservation } from '../../shared/api/contracts';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../shared/Input';
import { TableCard } from '../../untitled/components/application/table/table';
import { timestamp, duration, statusColor, isActiveExecutionState } from '../../model';
import { archiveApplyAllowed, attentionAssessment, execution, executionList, maintenanceCommand, maintenanceCommands, maintenanceDetail, maintenanceInventoryScope, maintenanceReason, maintenanceScope, cancellationResult, reconciliationResult, query, offset, timeSince, states, presentation, canReconcile, validEvidence, type Execution } from './model';
import { IssueTitle } from '../projects/IssueTitle';
import { ExecutionMaintenancePage } from './ExecutionMaintenancePage';

export function ExecutionsPage() {
  useLanguage();
  const { resourceId } = useParams(), location = useLocation();
  // Resource keys discard dialog drafts when navigation changes identity.
  return resourceId ? <ExecutionDetail key={resourceId} id={resourceId} search={location.search} />
    : new URLSearchParams(location.search).get('view') === 'maintenance' ? <ExecutionMaintenancePage /> : <ExecutionList />;
}
function Timing({ item }: { item: Execution }) {
  useLanguage();
  return <div className="text-sm text-tertiary"><p>{t("executions.created")}{' '}{timestamp(item.createdAtUtc)}</p>{item.assignedAtUtc && <p>{t("executions.assigned")}{' '}{timestamp(item.assignedAtUtc)}</p>}{item.startedAtUtc && <p>{t("executions.started")}{' '}{timestamp(item.startedAtUtc)}</p>}{item.completedAtUtc && <p>{t("executions.completed")}{' '}{timestamp(item.completedAtUtc)}</p>}<p>{duration(item, Date.now())}</p></div>;
}
function State({ item }: { item: Execution }) {
  useLanguage(); const state = presentation(item); return <StatusBadge tone={state.tone} active={state.active}>{state.text}</StatusBadge>; }
function ExecutionStatus({ item }: { item: Execution }) {
  useLanguage(); return <StatusBadge tone={stateTone(item.state)} active={isActiveExecutionState(item.state)}>{statusLabel(item.state)}</StatusBadge>;
}
function ExecutionList() {
  useLanguage();
  const [params, setParams] = useSearchParams(), search = `?${params}`, read = useApiRead(query(search), executionList);
  const p = useApiRead('/api/v1/projects', projects);
  const workerRead = useApiRead('/api/v1/workers', workers);
  const nodeRead = useApiRead('/api/v1/nodes', nodes), attentionOnly = params.get('attention') === 'needed';
  const change = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); next.delete('offset'); setParams(next); };
  const page = (value: number) => { const next = new URLSearchParams(params); next.set('offset', String(value)); setParams(next); };
  const entries = (read.data ?? []).map(item => ({ item,
    assessment: attentionAssessment(item, workerRead.data?.find(value => value.workerId === item.assignedWorkerId), nodeRead.data?.find(value => value.id === item.assignedWorkerId), Date.now(),
      workerRead.loading ? 'loading' : workerRead.error ? 'unavailable' : 'current')
  }));
  const attentionEntries = entries.flatMap(entry => entry.assessment ? [{ item: entry.item, assessment: entry.assessment }] : []);
  const attentionParams = new URLSearchParams(params); attentionParams.delete('offset'); if (attentionOnly) attentionParams.delete('attention'); else attentionParams.set('attention', 'needed');
  const attentionSearch = attentionParams.toString();
  const maintenanceParams = new URLSearchParams(params); maintenanceParams.set('view', 'maintenance');
  return <><PageHeading title={t("executions.executions")} actions={<div className="flex flex-wrap gap-2"><Link className="inline-flex min-h-10 items-center rounded-lg border border-secondary px-3 text-sm font-semibold text-primary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={`/executions?${maintenanceParams}`}>{t('maintenance.executionMaintenance')}</Link><Link className="inline-flex min-h-10 items-center rounded-lg px-3 text-sm font-semibold text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={`/executions${attentionSearch ? `?${attentionSearch}` : ''}`}>{attentionOnly ? t('maintenance.allExecutions') : t('maintenance.attentionView')}</Link><Button color="secondary" onPress={() => { void read.refetch(); }}>{t("executions.refreshExecutions")}</Button></div>} />
    <TableCard.Root><div className="mb-6 flex flex-wrap items-end gap-4 p-4">
      <label className="text-sm text-secondary">{t("executions.project")}<select className="block max-w-full rounded-lg border border-secondary bg-primary p-2" value={params.get('project') ?? ''} onChange={e => change('project', e.target.value)}><option value="">{t("executions.allProjects")}</option>{params.get('project') && !p.data?.some(x => x.id === params.get('project')) && <option value={params.get('project') ?? ''}>{params.get('project')}</option>}{p.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      <label className="text-sm text-secondary">{t("executions.state")}<select className="block rounded-lg border border-secondary bg-primary p-2" value={params.get('state') ?? ''} onChange={e => change('state', e.target.value)}><option value="">{t("executions.allStates")}</option>{states.map(x => <option key={x} value={x}>{statusLabel(x)}</option>)}</select></label>
      <Input label={t("executions.gitHubIssueNumber")} value={params.get('issue') ?? ''} onChange={value => change('issue', value)} />
      <label className="flex min-h-10 items-center gap-2 rounded-lg border border-secondary px-3 text-sm text-secondary"><input type="checkbox" checked={attentionOnly} onChange={event => change('attention', event.target.checked ? 'needed' : '')} />{t('maintenance.attentionNeeded')}</label>
    </div></TableCard.Root>
    <p className="mb-4 text-sm text-tertiary">{attentionOnly ? t('maintenance.attentionPageBound') : t("executions.upTo50RequestsPerPageTotalHistoryCountIsUnavailableExecution")}</p>
    {!read.data ? <ViewState title={read.error ? t("executions.executionsUnavailableRefreshToRetrieveCurrentData") : t("executions.loadingExecutions")} error={!!read.error} /> : !read.data.length ? <ViewState title={t("executions.noMatchingExecutionRequests")} /> : attentionOnly && !attentionEntries.length ? <ViewState title={t('maintenance.noAttentionOnPage')} /> : attentionOnly ? <AttentionList entries={attentionEntries} search={search} projects={p.data ?? []} workers={workerRead.data ?? []} /> : <TableCard.Root><div className="overflow-x-auto"><table className="w-full min-w-[1080px] table-fixed text-left"><colgroup><col className="w-[28%]"/><col className="w-[19%]"/><col className="w-[20%]"/><col className="w-[15%]"/><col className="w-[18%]"/></colgroup><thead className="bg-secondary text-xs uppercase tracking-wide text-tertiary"><tr><th className="px-5 py-4 font-semibold">{t("executions.execution")}</th><th className="px-5 py-4 font-semibold">{t("executions.project")}</th><th className="px-5 py-4 font-semibold">{t("executions.state")}</th><th className="px-5 py-4 font-semibold">{t("workers.worker")}</th><th className="px-5 py-4 font-semibold">{t("executions.created")}</th></tr></thead><tbody className="divide-y divide-secondary">{read.data.map(item => { const project = p.data?.find(value => value.id === item.projectId), worker = workerRead.data?.find(value => value.workerId === item.assignedWorkerId); return <tr key={item.id} className="align-top"><td className="px-5 py-5"><Link className="font-medium text-primary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}>{item.workReference ? `${item.workReference.type === 'github-issue' ? '#' : `${item.workReference.type} `}${item.workReference.id}` : <GuidDisplay value={item.id} />}</Link><Link className="mt-1 block break-all text-xs text-tertiary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}><GuidDisplay value={item.id} /></Link></td><td className="px-5 py-5"><Link className="text-sm text-primary hover:text-brand-secondary" to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link></td><td className="px-5 py-5"><State item={item} />{item.currentStage && <p className="mt-2 text-xs text-tertiary">{t("executions.reportedStage")}{' '}{localizeText(item.currentStage)}</p>}<p className="mt-2 line-clamp-2 text-xs text-secondary">{item.pendingReason || item.completionSummary || item.recoveryReason || t("executions.noAdditionalResultReported")}</p></td><td className="px-5 py-5 text-sm text-secondary">{item.assignedWorkerId ? <Link className="hover:text-brand-secondary" to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{worker?.displayName ?? item.assignedWorkerId}</Link> : t("executions.workerUnassigned")}</td><td className="px-5 py-5"><Timing item={item} /></td></tr>; })}</tbody></table></div></TableCard.Root>}
    <div className="mt-4 flex flex-wrap gap-3"><Button color="secondary" isDisabled={offset(search) === 0 || read.loading} onPress={() => page(Math.max(0, offset(search) - 50))}>{t("executions.previousPage")}</Button><Button color="secondary" isDisabled={!read.data || read.data.length < 50 || offset(search) >= 10000} onPress={() => page(Math.min(10000, offset(search) + 50))}>{t("executions.nextPage")}</Button><span className="text-sm text-tertiary">{t("executions.offset")}{' '}{offset(search)}</span></div>
  </>;
}
function AttentionList({ entries, search, projects: projectItems, workers: workerItems }: {
  entries: { item: Execution; assessment: NonNullable<ReturnType<typeof attentionAssessment>> }[];
  search: string; projects: { id: string; name: string }[]; workers: { workerId: string; displayName?: string }[];
}) {
  useLanguage();
  return <TableCard.Root><ul className="divide-y divide-secondary">
    {entries.map(({ item, assessment }) => {
      const project = projectItems.find(value => value.id === item.projectId), worker = workerItems.find(value => value.workerId === item.assignedWorkerId);
      const activity = assessment.lastActivityAtUtc, age = timeSince(activity, Date.now());
      return <li key={item.id} className="grid gap-3 p-5 sm:grid-cols-2 xl:grid-cols-[minmax(12rem,1.2fr)_minmax(10rem,1fr)_minmax(12rem,1fr)_minmax(12rem,1fr)] xl:items-start">
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.projectIssue')}</p>
          <p className="mt-1 font-semibold text-primary">{item.workReference?.type === 'github-issue' ? `#${item.workReference.id}` : item.workReference?.id ?? <GuidDisplay value={item.id} />}</p>
          <Link className="mt-1 block truncate text-sm text-brand-secondary hover:underline" to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link>
          <p className="mt-1 text-sm text-secondary">{item.assignedWorkerId ? worker?.displayName ?? item.assignedWorkerId : t('executions.workerUnassigned')}</p>
        </div>
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.executionAndClassification')}</p>
          <div className="mt-2 flex flex-wrap gap-2"><StatusBadge tone={stateTone(item.state)} active={isActiveExecutionState(item.state)}>{statusLabel(item.state)}</StatusBadge><StatusBadge tone="warning">{t(`maintenance.classification.${assessment.classification}` as TranslationKey)}</StatusBadge></div>
          <Link className="mt-2 block break-all text-xs text-tertiary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}><GuidDisplay value={item.id} /></Link>
        </div>
        <div>
          <p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.lastReportedActivity')}</p>
          {activity ? <><time className="mt-1 block text-sm text-secondary" dateTime={activity}>{timestamp(activity)}</time><p className="mt-1 text-xs text-tertiary">{age ? t('maintenance.ago', { age }) : t('maintenance.justNow')}</p></> : <p className="mt-1 text-sm text-tertiary">{t('maintenance.activityUnknown')}</p>}
          <p className="mt-1 text-xs text-tertiary">{t('maintenance.activityLimit')}</p>
        </div>
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.reasonAndNextAction')}</p>
          <p className="mt-1 text-sm text-secondary">{t(assessment.reason as TranslationKey)}</p>
          <p className="mt-2 text-sm font-medium text-primary">{t(assessment.nextAction as TranslationKey)}</p>
          <Link className="mt-3 inline-flex min-h-10 items-center rounded-lg px-3 text-sm font-semibold text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={`/executions/${encodeURIComponent(item.id)}${search}`}>{t('maintenance.reviewExecution')}</Link>
        </div>
      </li>;
    })}
  </ul></TableCard.Root>;
}
function ExecutionDetail({ id, search }: { id: string; search: string }) {
  useLanguage();
  const base = `/api/v1/executions/${encodeURIComponent(id)}`, runtime = useRuntime(), session = useSession(), read = useApiRead(base, execution);
  const projectRead = useApiRead('/api/v1/projects', projects), workerRead = useApiRead('/api/v1/workers', workers);
  const nodeRead = useApiRead('/api/v1/nodes', nodes);
  const [selected, select] = useState<'cancel' | 'reconcile'>(), [pending, setPending] = useState(false), [message, setMessage] = useState('');
  const [disposition, setDisposition] = useState('NotIntegrated'), [evidence, setEvidence] = useState(''), [commit, setCommit] = useState('');
  const [retryId, setRetryId] = useState<string>();
  const item = read.data, locked = runtime.locked(base);
  const project = projectRead.data?.find(value => value.id === item?.projectId);
  const assignedWorker = workerRead.data?.find(value => value.workerId === item?.assignedWorkerId);
  const cancelDisabledReason = item && item.state !== 'Queued' ? t('executions.cancelOnlyThisQueuedRequestAssignedAndRunningWorkCannotBeCancelled') : undefined;
  async function refresh() {
    if (pending) return;
    const generation = session.generation; setPending(true);
    try {
      await runtime.reconcile(base, async signal => {
        const current = await runtime.read(base, signal, execution);
        if (current.id !== id) throw new Error('Identity mismatch.');
        // Fresh state establishes whether cancellation/reconciliation remains permitted.
        // The atomic Server reconciliation retains its disposition and evidence here.
        await runtime.queries.cancelQueries({ queryKey: queryKeys.read(generation, base), exact: true });
        runtime.queries.setQueryData(queryKeys.read(generation, base), current);
      });
      if (runtime.snapshot().generation === generation) setMessage(t("executions.authoritativeExecutionRefreshedReviewCurrentStateAndRetainedRecoveryEvidenceBeforeAnother"));
    } catch { if (runtime.snapshot().generation === generation) setMessage(t("executions.authoritativeExecutionUnavailableAnyUncertainOperationRemainsLockedNoActionWasResubmitted")); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  async function submit() {
    if (!selected || pending || locked) throw new Error('Action unavailable.');
    const generation = session.generation, action = selected; setPending(true);
    try {
      const check = async (signal: AbortSignal) => {
        const current = await runtime.read(base, signal, execution);
        if (current.id !== id || (action === 'cancel' ? current.state !== 'Queued' : !canReconcile(current))) throw new Error('State changed.');
      };
      if (action === 'cancel') await runtime.mutate(base, `${base}/cancel`, 'POST', undefined, cancellationResult(id), check);
      else {
        if (!validEvidence(disposition, evidence, commit)) throw new Error('Evidence required.');
        const result = await runtime.mutate(base, `${base}/reconcile`, 'POST', { disposition, evidence: evidence.trim(), ...(disposition === 'Integrated' ? { integrationCommit: commit } : {}) }, reconciliationResult(id, disposition), check);
        if (runtime.snapshot().generation === generation) setRetryId(result.retry?.id);
      }
      if (runtime.snapshot().generation === generation) setMessage(t("executions.actionAcceptedServerObservationsAreRefreshing"));
    } catch { if (runtime.snapshot().generation === generation) setMessage(t("executions.actionResultUnavailableExplicitlyRefreshAuthoritativeExecutionBeforeAnotherAction")); throw new Error('Action unavailable.'); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  const close = () => { select(undefined); setEvidence(''); setCommit(''); setDisposition('NotIntegrated'); };
  const terminal = item ? ['Completed', 'Failed', 'Cancelled'].includes(item.state) : false;
  return <>
    {item ? <header className="mb-5 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
      <div className="min-w-0 flex-1">
        <nav aria-label={t('shared.breadcrumb')} className="mb-2 text-sm text-tertiary"><ol className="flex flex-wrap gap-2"><li><Link to={`/executions${search}`}>{t('executions.executions')}</Link></li><li aria-hidden="true">/</li><li aria-current="page" className="break-all">{id}</li></ol></nav>
        <h1 tabIndex={-1} className="text-display-sm font-semibold text-primary">{t('executions.execution')} · {id}</h1>
        <p className="mt-2 flex flex-wrap items-center gap-x-2 text-sm text-tertiary"><Link to={`/projects/${encodeURIComponent(item.projectId)}`} className="hover:text-brand-secondary">{project?.name ?? item.projectId}</Link><span aria-hidden="true">·</span><IssueTitle projectId={item.projectId} repository={project?.repository} workReference={item.workReference} /></p>
      </div>
      <div className="flex flex-wrap items-center justify-end gap-2"><ExecutionStatus item={item} /><StatusBadge tone="purple">{item.currentStage ? localizeText(item.currentStage) : t('executions.notReported')}</StatusBadge><span className="inline-flex" title={cancelDisabledReason}><Button color="secondary" isDisabled={item.state !== 'Queued' || pending || locked} aria-describedby={cancelDisabledReason ? 'execution-cancel-disabled-reason' : undefined} onPress={() => select('cancel')}>{t('executions.cancelExecution')}</Button>{cancelDisabledReason && <span id="execution-cancel-disabled-reason" className="sr-only">{cancelDisabledReason}</span>}</span></div>
    </header> : <PageHeading title={t('executions.execution')} resourceId={id} breadcrumbs={[{ label: t('executions.executions'), href: `/executions${search}` }, { label: t('executions.execution') }]} />}
    <div className="space-y-4">
      {message && <Notice>{message}</Notice>}{retryId && <Link className="text-sm text-brand-secondary" to={`/executions/${encodeURIComponent(retryId)}${search}`}>{t("executions.viewQueuedRecoveryAttempt")}</Link>}{locked && <Notice>{t("executions.operationPendingOrUncertainActionsAreLockedUntilExplicitAuthoritativeRefreshSucceeds")}</Notice>}
      {!item ? <ViewState title={read.error ? t("executions.executionUnavailableOrDeletedRefreshThisExactResourceListContextIsRetained") : t("executions.loadingExecution")} error={!!read.error} /> : <>
        <TableCard.Root>
          <section aria-labelledby="execution-current-state" className="flex flex-wrap items-center justify-between gap-x-8 gap-y-4 bg-secondary px-5 py-5 sm:px-6">
            <div className="min-w-0">
              <h2 id="execution-current-state" className="flex items-center gap-3 text-lg font-semibold text-primary"><span aria-hidden="true" className={`size-2.5 shrink-0 rounded-full ${stateDot(item.state)}`} />{item.state === 'Running' ? t('executions.executionInProgress') : t('executions.executionStateReported', { state: statusLabel(item.state) })}</h2>
              <p className="mt-2 break-words text-sm text-secondary">{t('executions.assignedTo', { worker: assignedWorker?.displayName ?? item.assignedWorkerId ?? t('executions.workerUnassigned') })}{' · '}{t('executions.startedAt', { time: item.startedAtUtc ? timestamp(item.startedAtUtc) : t('executions.notReported') })}</p>
            </div>
            <div className="min-w-44 text-left sm:text-right"><p className="text-xs text-tertiary">{t('shared.currentStage')}</p><p className="mt-1 font-semibold text-primary">{item.currentStage ? localizeText(item.currentStage) : t('executions.notReported')}</p></div>
          </section>
        </TableCard.Root>

        <div className="grid gap-5 xl:grid-cols-[minmax(0,1.72fr)_minmax(22rem,1fr)]">
          <TableCard.Root><section aria-labelledby="execution-summary" className="p-5 sm:p-6"><h2 id="execution-summary" className="mb-2 text-lg font-semibold text-primary">{t('executions.executionSummary')}</h2><div className="divide-y divide-secondary">
            <ExecutionDetailRow label={t('executions.project')}><Link to={`/projects/${encodeURIComponent(item.projectId)}`} className="hover:text-brand-secondary">{project?.name ?? item.projectId}</Link></ExecutionDetailRow>
            <ExecutionDetailRow label={t('executions.workItem')}><IssueTitle projectId={item.projectId} repository={project?.repository} workReference={item.workReference} /></ExecutionDetailRow>
            <ExecutionDetailRow label={t('workers.worker')}>{item.assignedWorkerId ? <Link to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`} className="hover:text-brand-secondary">{assignedWorker?.displayName ?? item.assignedWorkerId}</Link> : t('executions.workerUnassigned')}</ExecutionDetailRow>
            <ExecutionDetailRow label={t('executions.created')}><time dateTime={item.createdAtUtc}>{timestamp(item.createdAtUtc)}</time></ExecutionDetailRow>
            <ExecutionDetailRow label={t('executions.assigned')}>{item.assignedAtUtc ? timestamp(item.assignedAtUtc) : t('executions.notReported')}</ExecutionDetailRow>
          </div></section></TableCard.Root>

          <TableCard.Root><section aria-labelledby="execution-outcome" className="flex h-full flex-col p-5 sm:p-6"><h2 id="execution-outcome" className="mb-4 text-lg font-semibold text-primary">{t('executions.outcomeAndRecovery')}</h2>
            <div><ExecutionStatus item={item} /><p className="mt-4 font-semibold text-primary">{item.state === 'Failed' ? item.completionSummary?.trim() ? item.completionSummary : t('executions.failureReasonUnavailable') : terminal ? item.completionSummary || t('executions.noCompletionSummaryReported') : t('executions.noCompletionOutcomeYet')}</p><p className="mt-2 text-sm text-secondary">{terminal ? item.completionSummary ? t('executions.completionSummaryReported') : t('executions.terminalStateHasNoCompletionSummary') : t('executions.executionHasNotReportedTerminalStateOrCompletionSummary')}</p></div>
            <div className="mt-5 border-t border-secondary pt-4"><p className="text-xs text-tertiary">{t('executions.recoveryStatus')}</p><p className="mt-1 break-words text-sm text-secondary">{item.recoveryState ? statusLabel(item.recoveryState) : t('executions.notReported')}</p></div>
            <div className="mt-auto flex flex-wrap items-center gap-3 pt-5"><Button color="link-gray" size="sm" isDisabled={pending} onPress={() => { void refresh(); }}>{t('executions.refreshAuthoritativeExecution')}</Button>{canReconcile(item) && <Button color="secondary" isDisabled={pending || locked} onPress={() => select('reconcile')}>{t('executions.reconcileUncertainIntegration')}</Button>}</div>
          </section></TableCard.Root>
        </div>

        <section aria-labelledby="execution-evidence" className="pt-2">
          <header className="mb-4"><h2 id="execution-evidence" className="text-lg font-semibold text-primary">{t('executions.executionEvidence')}</h2><p className="mt-1 text-sm text-tertiary">{t('executions.operationalDetailReportedByServer')}</p></header>
          <div className="overflow-hidden rounded-xl bg-primary shadow-xs ring-1 ring-secondary">
            <EvidenceDisclosure title={t('executions.assignmentAndLease')} description={t('executions.assignmentAndLeaseDescription')} value={{ assignedWorkerId: item.assignedWorkerId, assignmentId: item.assignmentId, executionId: item.executionId, workerExecutionId: item.workerExecutionId, assignedAtUtc: item.assignedAtUtc, startedAtUtc: item.startedAtUtc, lease: item.lease }} />
            <EvidenceDisclosure title={t('executions.validationAttempts')} description={t('executions.validationAttemptsDescription')} value={{ validationResult: item.validationResult, missingRequirements: item.missingRequirements }} />
            <EvidenceDisclosure title={t('executions.integrationAndRecovery')} description={t('executions.integrationAndRecoveryDescription')} value={{ integrationResult: item.integrationResult, recoveryState: item.recoveryState, recoveryReason: item.recoveryReason, recoverable: item.recoverable, workspaceRecovery: item.workspaceRecovery, retryOfExecutionId: item.retryOfExecutionId, attemptNumber: item.attemptNumber, failureClassification: item.failureClassification, managedEligibilityState: item.managedEligibilityState, managedEligibilityReasons: item.managedEligibilityReasons, managedEligibilityCheckedAtUtc: item.managedEligibilityCheckedAtUtc }}>
              {item.retryOfExecutionId && <Link to={`/executions/${encodeURIComponent(item.retryOfExecutionId)}${search}`}>{t('executions.previousAttempt')} {item.retryOfExecutionId}</Link>}
              <p>{t('executions.theAPIRetainsTheCurrentRecoveryRecordAndPreviousAttemptLinkNot')}</p>
              <Link to={`/executions?project=${encodeURIComponent(item.projectId)}&issue=${encodeURIComponent(item.workReference?.id ?? '')}`}>{t('executions.browseMatchingAttempts')}</Link>
            </EvidenceDisclosure>
          </div>
          <p className="mt-4 text-xs text-tertiary">{t('executions.detailedEvidenceShownOnlyWhenReturnedByApi')}</p>
        </section>
        <ManagedMaintenance item={item} worker={assignedWorker} workerLoading={workerRead.loading} workerUnavailable={!!workerRead.error}
          workerNode={nodeRead.data?.find(value => value.id === item.assignedWorkerId)} onReconcile={() => select('reconcile')} />
      </>}
    </div>
    {selected === 'cancel' && <ConfirmationDialog isOpen title={t("executions.cancelQueuedRequest")} description={t("executions.cancelOnlyThisQueuedRequestAssignedAndRunningWorkCannotBeCancelled")} actionLabel={t("executions.cancelRequest")} destructive onClose={close} onSubmit={submit} disabled={pending || locked} />}
    {selected === 'reconcile' && <ActionDialog isOpen title={t("executions.reconcileUncertainIntegration")} description={t("executions.checkAuthoritativeRepositoryEvidenceFirstNotIntegratedQueuesANewAttemptWith")} actionLabel={t("executions.recordVerifiedDisposition")} onClose={close} onSubmit={submit} disabled={pending || locked || !validEvidence(disposition, evidence, commit)}>
      <label className="text-sm text-secondary">{t("executions.verifiedDisposition")}<select className="mt-1 block w-full rounded-lg border border-secondary bg-primary p-2" value={disposition} onChange={e => setDisposition(e.target.value)}><option value="NotIntegrated">{t("executions.notIntegratedQueueNewAttempt")}</option><option value="Integrated">{t("executions.integratedRecordVerifiedCommit")}</option></select></label>
      <Input label={t("executions.authoritativeIntegrationEvidence")} hint={t("executions.describeTheRepositoryBaseBranchAndEvidenceChecked11000PrintableCharacters")} isRequired maxLength={1000} value={evidence} onChange={setEvidence} />
      {disposition === 'Integrated' && <Input label={t("executions.fullIntegratedCommitID")} isRequired value={commit} onChange={setCommit} />}
    </ActionDialog>}
  </>;
}
type ManagedAction = 'inventory' | 'inspect' | 'cleanup' | 'archive' | 'retry-report';
function ManagedMaintenance({ item, worker, workerNode, workerLoading, workerUnavailable, onReconcile }: {
  item: Execution; worker: WorkerObservation | undefined; workerLoading: boolean; workerUnavailable: boolean;
  workerNode?: import('../../shared/api/contracts').NodeSummary;
  onReconcile(): void;
}) {
  useLanguage();
  const runtime = useRuntime(), session = useSession(), base = `/api/v1/executions/${encodeURIComponent(item.id)}`;
  const historyPath = worker?.workerId ? `/api/v1/maintenance/executions?workerId=${encodeURIComponent(worker.workerId)}&limit=50&offset=0` : '/api/v1/maintenance/executions?limit=50&offset=0';
  const history = useQuery({ queryKey: queryKeys.read(session.generation, historyPath), enabled: session.authenticated && !!worker?.workerId,
    queryFn: ({ signal }) => runtime.read(historyPath, signal, maintenanceCommands) });
  const [operationId, setOperationId] = useState<string>(), [dialogAction, setDialogAction] = useState<ManagedAction>(), [dialogApply, setDialogApply] = useState(false);
  const [operationBefore, setOperationBefore] = useState<{ id: string; item: Execution }>();
  const [pending, setPending] = useState(false), [error, setError] = useState(''), [message, setMessage] = useState('');
  const operationPath = operationId ? `/api/v1/maintenance/executions/${encodeURIComponent(operationId)}` : '/api/v1/maintenance/executions/00000000000000000000000000000000';
  const operation = useQuery({ queryKey: queryKeys.read(session.generation, operationPath), enabled: session.authenticated && !!operationId,
    queryFn: ({ signal }) => runtime.read(operationPath, signal, maintenanceDetail) });
  const activeCommand = history.data?.find(command => ['pending', 'running', 'uncertain'].includes(command.status));
  useEffect(() => { if (!operationId && activeCommand) setOperationId(activeCommand.request.operationId); }, [activeCommand, operationId]);
  const currentCommand = operation.data?.operation;
  const otherCommandOutstanding = !!activeCommand && activeCommand.request.operationId !== currentCommand?.request.operationId;
  const locked = runtime.locked(base), frozen = pending || locked || otherCommandOutstanding || ['pending', 'running', 'uncertain'].includes(currentCommand?.status ?? '');
  const closeDialog = () => { if (!pending) { setDialogAction(undefined); setDialogApply(false); } };

  async function createOperation(action: ManagedAction, apply = false) {
    if (pending || locked || otherCommandOutstanding || !worker?.workerId) return;
    const workerId = worker.workerId, workerExecutionId = item.workerExecutionId, assignmentId = item.assignmentId, generation = item.lease?.generation;
    const operation = globalThis.crypto.randomUUID().replaceAll('-', '').toLowerCase();
    const request = action === 'inventory'
      ? { operationId: operation, workerId, action, apply: false, timeoutSeconds: 60, limit: 50, offset: 0 }
      : { operationId: operation, workerId, serverExecutionId: item.id, workerExecutionId: workerExecutionId ?? '', assignmentId: assignmentId ?? '', generation: generation ?? 0,
        action, apply, timeoutSeconds: 60, limit: 1, offset: 0 };
    setPending(true); setError(''); setMessage(''); setOperationId(operation); setOperationBefore({ id: operation, item });
    try {
      await runtime.mutate(base, '/api/v1/maintenance/executions', 'POST', request, value => {
        const created = maintenanceCommand(value);
        if (created.request.operationId !== operation || created.request.workerId !== workerId || created.request.action !== action || created.request.apply !== apply ||
            action !== 'inventory' && created.request.serverExecutionId !== item.id) throw new Error('Unexpected maintenance request.');
        return created;
      }, async signal => {
        const current = await runtime.read(base, signal, execution);
        if (current.id !== item.id || current.assignedWorkerId !== item.assignedWorkerId || action !== 'inventory' && !sameMaintenanceIdentity(item, current)) throw new Error('maintenance.scopeChanged');
        const freshWorker = await runtime.read(`/api/v1/workers/${encodeURIComponent(workerId)}`, signal, validateWorker);
        const freshNodes = await runtime.read('/api/v1/nodes', signal, nodes);
        const freshNode = freshNodes.find(value => value.id === workerId);
        const eligibility = action === 'inventory' ? maintenanceInventoryScope(current, freshWorker, freshNode) : maintenanceScope(current, action, apply, freshWorker, freshNode, Date.now());
        if (!eligibility.allowed) throw new Error(eligibility.reason ?? 'maintenance.actionUnavailable');
      });
      if (runtime.snapshot().generation === session.generation) {
        setMessage(apply ? t('maintenance.applySubmitted') : action === 'inspect' ? t('maintenance.inspectSubmitted') : t('maintenance.previewSubmitted'));
        void runtime.queries.invalidateQueries({ queryKey: queryKeys.read(session.generation, historyPath), exact: true });
      }
    } catch (failure) {
      const code = failure instanceof Error && failure.message.startsWith('maintenance.') ? failure.message : 'maintenance.operationUnavailable';
      if (runtime.snapshot().generation === session.generation) setError(t(code as TranslationKey));
    } finally { if (runtime.snapshot().generation === session.generation) setPending(false); }
  }

  async function refreshOperation() {
    if (!operationId || pending) return;
    const generation = session.generation; let operationMissing = false;
    setPending(true); setError('');
    try {
      await runtime.reconcile(base, async signal => {
        const current = await runtime.read(base, signal, execution);
        if (current.id !== item.id) throw new Error('maintenance.operationUnavailable');
        runtime.queries.setQueryData(queryKeys.read(generation, base), current);
        let detail: ExecutionMaintenanceDetail;
        try { detail = await runtime.read(operationPath, signal, maintenanceDetail); }
        catch (failure) {
          if (!(failure instanceof ApiError) || failure.status !== 404) throw failure;
          const commands = await runtime.read(historyPath, signal, maintenanceCommands);
          if (commands.some(command => command.request.operationId === operationId)) throw failure;
          runtime.queries.setQueryData(queryKeys.read(generation, historyPath), commands);
          operationMissing = true;
          setOperationId(undefined);
          return;
        }
        if (detail.operation.request.operationId !== operationId) throw new Error('maintenance.operationUnavailable');
        runtime.queries.setQueryData(queryKeys.read(generation, operationPath), detail);
      });
      setMessage(t(operationMissing ? 'maintenance.operationNotAccepted' : 'maintenance.authoritativeStatusRefreshed'));
      void runtime.queries.invalidateQueries({ queryKey: queryKeys.read(generation, historyPath), exact: true });
    } catch { if (runtime.snapshot().generation === generation) setError(t('maintenance.operationUnavailable')); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }

  async function cancelPendingOperation() {
    if (!operationId || !currentCommand || currentCommand.status !== 'pending' || pending || locked) return;
    const generation = session.generation, path = `${operationPath}/cancel`;
    setPending(true); setError('');
    try {
      await runtime.mutate(base, path, 'POST', undefined, value => {
        const cancelled = maintenanceCommand(value);
        if (cancelled.request.operationId !== operationId || cancelled.status !== 'cancelled') throw new Error('Unexpected cancellation result.');
        return cancelled;
      }, async signal => {
        const current = await runtime.read(operationPath, signal, maintenanceDetail);
        if (current.operation.request.operationId !== operationId || current.operation.status !== 'pending') throw new Error('maintenance.actionUnavailable');
      });
      if (runtime.snapshot().generation === generation) setMessage(t('maintenance.pendingCancelled'));
    } catch (failure) {
      const code = failure instanceof Error && failure.message.startsWith('maintenance.') ? failure.message : 'maintenance.operationUnavailable';
      if (runtime.snapshot().generation === generation) setError(t(code as TranslationKey));
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }

  function beginApply(action: ManagedAction) { setDialogAction(action); setDialogApply(true); }
  function confirmedApply() {
    if (!dialogAction || !dialogApply) throw new Error('Maintenance confirmation missing.');
    return createOperation(dialogAction, true);
  }
  const detail = operation.data, report = detail?.operation.report, previewAction = detail?.operation.request.apply === false ? detail.operation.request.action : undefined;
  const before = operationBefore && operationBefore.id === operationId ? operationBefore.item : undefined;
  const previewPassed = !!report && report.outcome === 'succeeded' &&
    (previewAction === 'cleanup' ? report.reason === 'integrated' :
      previewAction === 'archive' ? report.reason === 'already-clean' : previewAction === 'retry-report' && report.reason === 'completion-report-preview');
  const archiveProofReady = archiveApplyAllowed(detail);
  const applyAction = previewAction === 'cleanup' || previewAction === 'archive' || previewAction === 'retry-report' ? previewAction : undefined;
  const applyScope = applyAction ? maintenanceScope(item, applyAction, true, worker, workerNode, Date.now()) : undefined;
  const attention = attentionAssessment(item, worker, workerNode, Date.now(), workerLoading ? 'loading' : workerUnavailable ? 'unavailable' : 'current');
  const actions: { action: ManagedAction; label: string }[] = [
    { action: 'inventory', label: t('maintenance.inspectWorkerInventory') },
    { action: 'inspect', label: t('maintenance.inspectWorkerRecord') },
    { action: 'cleanup', label: t('maintenance.previewCleanup') },
    { action: 'archive', label: t('maintenance.previewArchive') },
    { action: 'retry-report', label: t('maintenance.previewReportRetry') }
  ];
  const available = actions.map(entry => ({ ...entry, eligibility: entry.action === 'inventory'
    ? maintenanceInventoryScope(item, worker, workerNode) : maintenanceScope(item, entry.action, false, worker, workerNode, Date.now()) }));
  const applyTitle = dialogAction === 'cleanup' ? t('maintenance.confirmCleanup') : dialogAction === 'archive' ? t('maintenance.confirmArchive') : t('maintenance.confirmReportRetry');
  const applyDescription = dialogAction === 'cleanup' ? t('maintenance.confirmCleanupDescription') : dialogAction === 'archive' ? t('maintenance.confirmArchiveDescription') : t('maintenance.confirmReportRetryDescription');
  const operationStatus = detail?.status ?? detail?.operation.status;
  return <section aria-labelledby="managed-maintenance-heading" className="space-y-4 pt-2">
    <header><h2 id="managed-maintenance-heading" className="text-lg font-semibold text-primary">{t('maintenance.managedExecutionMaintenance')}</h2><p className="mt-1 max-w-3xl text-sm text-secondary">{t('maintenance.serverAuthorityDescription')}</p></header>
    {error && <Notice error>{error}</Notice>}{message && <Notice>{message}</Notice>}{locked && <Notice>{t('maintenance.uncertainOperationLocked')}</Notice>}
    <TableCard.Root><div className="space-y-4 p-5 sm:p-6">
      <div className="grid gap-3 sm:grid-cols-3">
        <div><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.executionStatus')}</p><div className="mt-2"><ExecutionStatus item={item} /></div></div>
        <div><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.maintenanceClassification')}</p><p className="mt-2 text-sm text-secondary">{attention ? t(`maintenance.classification.${attention.classification}` as TranslationKey) : item.recoveryState ? statusLabel(item.recoveryState) : t('maintenance.noMaintenanceClassification')}</p></div>
        <div><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('workers.worker')} · {t('maintenance.dataFreshness')}</p><p className="mt-2 text-sm text-secondary">{worker ? statusLabel(worker.availability) : workerLoading ? t('maintenance.attention.workerStateLoading') : workerUnavailable ? t('maintenance.attention.workerStateUnavailable') : t('maintenance.workerRecordMissing')}{workerNode?.observationsStale ? ` · ${t('maintenance.outdated')}` : workerNode ? ` · ${t('maintenance.current')}` : ` · ${t('maintenance.freshnessUnknown')}`}</p></div>
      </div>
      {attention && <p className="rounded-lg bg-secondary p-3 text-sm text-secondary">{t(attention.reason as TranslationKey)}{attention.lastActivityAtUtc ? ` · ${timestamp(attention.lastActivityAtUtc)}` : ''}</p>}
      {available.some(entry => !entry.eligibility.allowed) && <p className="text-sm text-tertiary">{t(available.find(entry => !entry.eligibility.allowed)?.eligibility.reason as TranslationKey)}</p>}
      <div className="flex flex-wrap gap-2">{available.map(entry => entry.eligibility.allowed && <Button key={entry.action} color="secondary" isDisabled={frozen || history.isLoading} onPress={() => { void createOperation(entry.action); }}>{entry.label}</Button>)}
        {canReconcile(item) && <Button color="secondary" isDisabled={pending || locked} onPress={onReconcile}>{t('executions.reconcileUncertainIntegration')}</Button>}
      </div>
      <p className="text-sm text-tertiary">{t('maintenance.purgeUnavailable')}</p>
      {otherCommandOutstanding && activeCommand && <Notice>{t('maintenance.workerOperationOutstanding', { operation: activeCommand.request.operationId, status: statusLabel(activeCommand.status) })}</Notice>}
      {history.isLoading && <p role="status" className="text-sm text-tertiary">{t('maintenance.loadingOperationHistory')}</p>}
      {history.error && <div className="flex flex-wrap items-center gap-2"><Notice error>{t('maintenance.operationHistoryUnavailable')}</Notice><Button color="secondary" size="sm" isDisabled={pending} onPress={() => { void history.refetch(); }}>{t('maintenance.refreshOperationHistory')}</Button></div>}
      {!history.isLoading && history.data?.length === 0 && <p className="text-sm text-tertiary">{t('maintenance.noPriorOperations')}</p>}
      {!!history.data?.length && <details className="rounded-lg border border-secondary p-3"><summary className="cursor-pointer text-sm font-semibold text-primary">{t('maintenance.recentOperations', { count: history.data.length })}</summary><ul className="mt-3 divide-y divide-secondary">{history.data.slice(0, 8).map(command => <li key={command.request.operationId} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm"><span>{statusLabel(command.request.action)} · {statusLabel(command.status)}{command.report ? ` · ${maintenanceReason(command.report.reason)}` : ''}{command.report && command.report.observations.length > 1 ? ` · ${command.report.observations.length} ${t('maintenance.recordsObserved')}` : ''}</span><Button color="link-gray" size="sm" onPress={() => setOperationId(command.request.operationId)}>{t('maintenance.openOperation')}</Button></li>)}</ul></details>}
    </div></TableCard.Root>
    {operationId && <TableCard.Root><section aria-labelledby="maintenance-operation-result" className="space-y-3 p-5 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3"><div><h3 id="maintenance-operation-result" className="font-semibold text-primary">{t('maintenance.operationResult')}</h3><p className="break-all text-xs text-tertiary">{operationId}</p></div><Button color="secondary" isDisabled={pending} onPress={() => { void refreshOperation(); }}>{t('maintenance.refreshOperation')}</Button></div>
      {operation.isPending ? <p role="status" className="text-sm text-secondary">{t('maintenance.loadingOperation')}</p> : operation.error ? <Notice error>{t('maintenance.operationUnavailable')}</Notice> : detail && <>
        <div className="flex flex-wrap gap-2"><StatusBadge tone={statusTone(operationStatus ?? 'unknown')}>{statusLabel(operationStatus)}</StatusBadge>{report && <StatusBadge tone={statusTone(report.outcome)}>{statusLabel(report.outcome)}</StatusBadge>}</div>
        <p className="text-sm text-secondary">{report ? maintenanceReason(report.reason) : t('maintenance.operationWaitingForWorker')}</p>
        {currentCommand?.status === 'pending' && <Button color="secondary" isDisabled={pending || locked} onPress={() => { void cancelPendingOperation(); }}>{t('maintenance.cancelPendingOperation')}</Button>}
        {report && previewAction !== 'inventory' && <div className="grid gap-3 sm:grid-cols-2"><p className="rounded-lg bg-secondary p-3 text-sm text-secondary">{before ? t('maintenance.beforeOutcome', { state: statusLabel(before.state), recovery: statusLabel(before.recoveryState) }) : t('maintenance.beforeStateUnavailable')}</p><p className="rounded-lg bg-secondary p-3 text-sm text-secondary">{t('maintenance.afterOutcome', { state: statusLabel(detail.observations[0]?.observation.state ?? detail.execution?.state ?? item.state), recovery: statusLabel(detail.observations[0]?.observation.recoveryState ?? detail.execution?.recoveryState ?? item.recoveryState) })}</p></div>}
        {detail.workerStatus !== 'online' && detail.workerStatus !== 'draining' && <Notice>{t('maintenance.workerOfflineStatus')}</Notice>}
        {!!detail.observations.length && <ul className="divide-y divide-secondary rounded-lg border border-secondary">{detail.observations.map(row => <li key={row.observation.executionId} className="flex flex-wrap items-center justify-between gap-2 p-3 text-sm"><span className="break-all">{row.observation.project} · #{row.observation.issueNumber}</span><span className="flex flex-wrap gap-2"><StatusBadge tone={stateTone(row.observation.state)} active={isActiveExecutionState(row.observation.state)}>{statusLabel(row.observation.state)}</StatusBadge><StatusBadge tone={row.status === 'confirmed' ? 'success' : 'warning'}>{statusLabel(row.status)}</StatusBadge><StatusBadge tone={statusTone(row.observation.reportingStatus)}>{statusLabel(row.observation.reportingStatus)}</StatusBadge></span></li>)}</ul>}
        {previewPassed && applyScope && applyAction && <div className="flex flex-wrap items-center gap-3"><p className="text-sm text-secondary">{!applyScope.allowed ? t(applyScope.reason as TranslationKey) : applyAction === 'archive' && !archiveProofReady ? t('maintenance.archivePreviewRequiresCleanHistory') : t('maintenance.previewAllowsApply')}</p>{applyScope.allowed && (applyAction !== 'archive' || archiveProofReady) && <Button color={applyAction === 'cleanup' || applyAction === 'archive' ? 'primary-destructive' : 'primary'} isDisabled={frozen} onPress={() => beginApply(applyAction)}>{applyAction === 'cleanup' ? t('maintenance.applyCleanup') : applyAction === 'archive' ? t('maintenance.applyArchive') : t('maintenance.applyReportRetry')}</Button>}</div>}
      </>}
    </section></TableCard.Root>}
    {dialogAction && dialogApply && <ConfirmationDialog isOpen title={applyTitle} description={applyDescription} actionLabel={dialogAction === 'cleanup' ? t('maintenance.applyCleanup') : dialogAction === 'archive' ? t('maintenance.applyArchive') : t('maintenance.applyReportRetry')} destructive={dialogAction === 'cleanup' || dialogAction === 'archive'} onClose={closeDialog} onSubmit={confirmedApply} disabled={pending || frozen} />}
  </section>;
}
function sameMaintenanceIdentity(before: Execution, current: Execution) {
  return before.id === current.id && before.assignedWorkerId === current.assignedWorkerId && before.assignmentId === current.assignmentId &&
    before.workerExecutionId === current.workerExecutionId && before.lease?.generation === current.lease?.generation && before.lease?.workerId === current.lease?.workerId;
}
function statusTone(value: string): 'gray' | 'success' | 'warning' | 'error' {
  const tone = statusColor(value);
  return tone === 'success' || tone === 'warning' || tone === 'error' ? tone : 'gray';
}
function ExecutionDetailRow({ label, children }: { label: string; children: ReactNode }) {
  return <div className="grid gap-1 py-3 sm:grid-cols-[10.5rem_minmax(0,1fr)] sm:gap-3"><span className="text-sm text-tertiary">{label}</span><span className="min-w-0 break-words text-sm text-secondary">{children}</span></div>;
}

function EvidenceDisclosure({ title, description, value, children }: { title: string; description: string; value: unknown; children?: ReactNode }) {
  return <details className="group border-b border-secondary last:border-b-0">
    <summary className="flex cursor-pointer list-none items-center justify-between gap-4 px-5 py-4 outline-brand focus-visible:outline-2 focus-visible:outline-offset-[-2px] sm:px-6">
      <span className="min-w-0"><span className="block text-sm font-semibold text-primary">{title}</span><span className="mt-1 block text-xs text-tertiary">{description}</span></span>
      <span aria-hidden="true" className="shrink-0 text-lg text-tertiary transition-transform group-open:rotate-180">⌄</span>
    </summary>
    <div className="space-y-3 px-5 pb-5 sm:px-6"><Evidence value={value} />{children}</div>
  </details>;
}

function Evidence({ value }: { value: unknown }) {
  useLanguage();
  const serialized = JSON.stringify(value, null, 2);
  return serialized === '{}' ? <p className="text-sm text-tertiary">{t('executions.noEvidenceReturnedForThisSection')}</p>
    : <pre className="whitespace-pre-wrap break-all rounded-lg bg-secondary p-4 text-xs text-secondary">{serialized}</pre>;
}

function stateTone(state: string): 'gray' | 'success' | 'warning' | 'error' | 'info' {
  const tone = statusColor(state);
  return tone === 'success' || tone === 'warning' || tone === 'error' || tone === 'info' ? tone : 'gray';
}

function stateDot(state: string) {
  const tone = stateTone(state);
  return tone === 'success' ? 'bg-success-solid' : tone === 'warning' ? 'bg-warning-solid' : tone === 'error' ? 'bg-error-solid' : tone === 'info' ? 'bg-utility-blue-500' : 'bg-secondary-solid';
}
