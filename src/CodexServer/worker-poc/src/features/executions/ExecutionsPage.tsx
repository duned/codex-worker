import { t, useLanguage, statusLabel, localizeText } from '../../shared/i18n';
import { useState, type ReactNode } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { projects, workers } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { PageHeading, ViewState, Notice, StatusBadge } from '../../shared/Presentation';
import { ConfirmationDialog, ActionDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../shared/Input';
import { TableCard } from '../../untitled/components/application/table/table';
import { timestamp, duration, statusColor } from '../../model';
import { execution, executionList, cancellationResult, reconciliationResult, query, offset, states, presentation, canReconcile, validEvidence, type Execution } from './model';
import { IssueTitle } from '../projects/IssueTitle';

export function ExecutionsPage() {
  useLanguage();
  const { resourceId } = useParams(), location = useLocation();
  // Resource keys discard dialog drafts when navigation changes identity.
  return resourceId ? <ExecutionDetail key={resourceId} id={resourceId} search={location.search} /> : <ExecutionList />;
}
function Timing({ item }: { item: Execution }) {
  useLanguage();
  return <div className="text-sm text-tertiary"><p>{t("executions.created")}{' '}{timestamp(item.createdAtUtc)}</p>{item.assignedAtUtc && <p>{t("executions.assigned")}{' '}{timestamp(item.assignedAtUtc)}</p>}{item.startedAtUtc && <p>{t("executions.started")}{' '}{timestamp(item.startedAtUtc)}</p>}{item.completedAtUtc && <p>{t("executions.completed")}{' '}{timestamp(item.completedAtUtc)}</p>}<p>{duration(item, Date.now())}</p></div>;
}
function State({ item }: { item: Execution }) {
  useLanguage(); const state = presentation(item); return <StatusBadge tone={state.tone}>{state.text}</StatusBadge>; }
function ExecutionStatus({ item }: { item: Execution }) {
  useLanguage(); return <StatusBadge tone={stateTone(item.state)}>{statusLabel(item.state)}</StatusBadge>;
}
function ExecutionList() {
  useLanguage();
  const [params, setParams] = useSearchParams(), search = `?${params}`, read = useApiRead(query(search), executionList);
  const p = useApiRead('/api/v1/projects', projects);
  const workerRead = useApiRead('/api/v1/workers', workers);
  const change = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); next.delete('offset'); setParams(next); };
  const page = (value: number) => { const next = new URLSearchParams(params); next.set('offset', String(value)); setParams(next); };
  return <><PageHeading title={t("executions.executions")} actions={<Button color="secondary" onPress={() => { void read.refetch(); }}>{t("executions.refreshExecutions")}</Button>} />
    <TableCard.Root><div className="mb-6 flex flex-wrap items-end gap-4 p-4">
      <label className="text-sm text-secondary">{t("executions.project")}<select className="block max-w-full rounded-lg border border-secondary bg-primary p-2" value={params.get('project') ?? ''} onChange={e => change('project', e.target.value)}><option value="">{t("executions.allProjects")}</option>{params.get('project') && !p.data?.some(x => x.id === params.get('project')) && <option value={params.get('project') ?? ''}>{params.get('project')}</option>}{p.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      <label className="text-sm text-secondary">{t("executions.state")}<select className="block rounded-lg border border-secondary bg-primary p-2" value={params.get('state') ?? ''} onChange={e => change('state', e.target.value)}><option value="">{t("executions.allStates")}</option>{states.map(x => <option key={x} value={x}>{statusLabel(x)}</option>)}</select></label>
      <Input label={t("executions.gitHubIssueNumber")} value={params.get('issue') ?? ''} onChange={value => change('issue', value)} />
    </div></TableCard.Root>
    <p className="mb-4 text-sm text-tertiary">{t("executions.upTo50RequestsPerPageTotalHistoryCountIsUnavailableExecution")}</p>
    {!read.data ? <ViewState title={read.error ? t("executions.executionsUnavailableRefreshToRetrieveCurrentData") : t("executions.loadingExecutions")} error={!!read.error} /> : !read.data.length ? <ViewState title={t("executions.noMatchingExecutionRequests")} /> : <TableCard.Root><div className="overflow-x-auto"><table className="w-full min-w-[1080px] table-fixed text-left"><colgroup><col className="w-[28%]"/><col className="w-[19%]"/><col className="w-[20%]"/><col className="w-[15%]"/><col className="w-[18%]"/></colgroup><thead className="bg-secondary text-xs uppercase tracking-wide text-tertiary"><tr><th className="px-5 py-4 font-semibold">{t("executions.execution")}</th><th className="px-5 py-4 font-semibold">{t("executions.project")}</th><th className="px-5 py-4 font-semibold">{t("executions.state")}</th><th className="px-5 py-4 font-semibold">{t("workers.worker")}</th><th className="px-5 py-4 font-semibold">{t("executions.created")}</th></tr></thead><tbody className="divide-y divide-secondary">{read.data.map(item => { const project = p.data?.find(value => value.id === item.projectId), worker = workerRead.data?.find(value => value.workerId === item.assignedWorkerId); return <tr key={item.id} className="align-top"><td className="px-5 py-5"><Link className="font-medium text-primary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}>{item.workReference ? `${item.workReference.type === 'github-issue' ? '#' : `${item.workReference.type} `}${item.workReference.id}` : item.id}</Link><Link className="mt-1 block break-all text-xs text-tertiary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}>{item.id}</Link></td><td className="px-5 py-5"><Link className="text-sm text-primary hover:text-brand-secondary" to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link></td><td className="px-5 py-5"><State item={item} />{item.currentStage && <p className="mt-2 text-xs text-tertiary">{t("executions.reportedStage")}{' '}{localizeText(item.currentStage)}</p>}<p className="mt-2 line-clamp-2 text-xs text-secondary">{item.pendingReason || item.completionSummary || item.recoveryReason || t("executions.noAdditionalResultReported")}</p></td><td className="px-5 py-5 text-sm text-secondary">{item.assignedWorkerId ? <Link className="hover:text-brand-secondary" to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{worker?.displayName ?? item.assignedWorkerId}</Link> : t("executions.workerUnassigned")}</td><td className="px-5 py-5"><Timing item={item} /></td></tr>; })}</tbody></table></div></TableCard.Root>}
    <div className="mt-4 flex flex-wrap gap-3"><Button color="secondary" isDisabled={offset(search) === 0 || read.loading} onPress={() => page(Math.max(0, offset(search) - 50))}>{t("executions.previousPage")}</Button><Button color="secondary" isDisabled={!read.data || read.data.length < 50 || offset(search) >= 10000} onPress={() => page(Math.min(10000, offset(search) + 50))}>{t("executions.nextPage")}</Button><span className="text-sm text-tertiary">{t("executions.offset")}{' '}{offset(search)}</span></div>
  </>;
}
function ExecutionDetail({ id, search }: { id: string; search: string }) {
  useLanguage();
  const base = `/api/v1/executions/${encodeURIComponent(id)}`, runtime = useRuntime(), session = useSession(), read = useApiRead(base, execution);
  const projectRead = useApiRead('/api/v1/projects', projects), workerRead = useApiRead('/api/v1/workers', workers);
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
            <div><ExecutionStatus item={item} /><p className="mt-4 font-semibold text-primary">{terminal ? item.completionSummary || t('executions.noCompletionSummaryReported') : t('executions.noCompletionOutcomeYet')}</p><p className="mt-2 text-sm text-secondary">{terminal ? item.completionSummary ? t('executions.completionSummaryReported') : t('executions.terminalStateHasNoCompletionSummary') : t('executions.executionHasNotReportedTerminalStateOrCompletionSummary')}</p></div>
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

function stateTone(state: string) {
  if (state.toLowerCase() === 'running') return 'info';
  const tone = statusColor(state);
  return tone === 'success' || tone === 'warning' || tone === 'error' ? tone : 'gray';
}

function stateDot(state: string) {
  const tone = stateTone(state);
  return tone === 'success' ? 'bg-success-solid' : tone === 'warning' ? 'bg-warning-solid' : tone === 'error' ? 'bg-error-solid' : tone === 'info' ? 'bg-utility-blue-500' : 'bg-secondary-solid';
}
