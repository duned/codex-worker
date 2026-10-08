import { t, useLanguage, statusLabel, localizeText } from '../../shared/i18n';
import { ExternalLink } from '../../shared/Actions';
import { useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { projects, workers } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { PageHeading, ViewState, Notice, AdvancedDisclosure, StatusBadge } from '../../shared/Presentation';
import { ConfirmationDialog, ActionDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../shared/Input';
import { TableCard } from '../../untitled/components/application/table/table';
import { issueLink, timestamp, duration } from '../../model';
import { execution, executionList, cancellationResult, reconciliationResult, query, offset, states, presentation, canReconcile, validEvidence, type Execution } from './model';

export function ExecutionsPage() {
  useLanguage();
  const { resourceId } = useParams(), location = useLocation();
  // Resource keys discard dialog drafts when navigation changes identity.
  return resourceId ? <ExecutionDetail key={resourceId} id={resourceId} search={location.search} /> : <ExecutionList />;
}
function Identity({ item }: { item: Execution }) {
  useLanguage();
  const p = useApiRead('/api/v1/projects', projects), w = useApiRead('/api/v1/workers', workers);
  const project = p.data?.find(x => x.id === item.projectId), worker = w.data?.find(x => x.workerId === item.assignedWorkerId);
  const href = issueLink(item.workReference, project?.repository);
  return <div className="space-y-1 break-words text-sm text-secondary">
    <Link to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link>
    <p>{href ? <ExternalLink href={href}>{t("executions.gitHubIssue")}{item.workReference?.id}</ExternalLink> : item.workReference ? `${item.workReference.type} ${item.workReference.id}` : t("executions.workReferenceUnavailable")}</p>
    <p>{item.assignedWorkerId ? <Link to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{worker?.displayName ?? item.assignedWorkerId}</Link> : t("executions.workerUnassigned")}</p>
  </div>;
}
function Timing({ item }: { item: Execution }) {
  useLanguage();
  return <div className="text-sm text-tertiary"><p>{t("executions.created")}{' '}{timestamp(item.createdAtUtc)}</p>{item.assignedAtUtc && <p>{t("executions.assigned")}{' '}{timestamp(item.assignedAtUtc)}</p>}{item.startedAtUtc && <p>{t("executions.started")}{' '}{timestamp(item.startedAtUtc)}</p>}{item.completedAtUtc && <p>{t("executions.completed")}{' '}{timestamp(item.completedAtUtc)}</p>}<p>{duration(item, Date.now())}</p></div>;
}
function State({ item }: { item: Execution }) {
  useLanguage(); const state = presentation(item); return <StatusBadge tone={state.tone}>{state.text}</StatusBadge>; }
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
  const [selected, select] = useState<'cancel' | 'reconcile'>(), [pending, setPending] = useState(false), [message, setMessage] = useState('');
  const [disposition, setDisposition] = useState('NotIntegrated'), [evidence, setEvidence] = useState(''), [commit, setCommit] = useState('');
  const [retryId, setRetryId] = useState<string>();
  const item = read.data, locked = runtime.locked(base);
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
  return <><PageHeading title={t("executions.execution")} resourceId={id} breadcrumbs={[{ label: t("executions.executions"), href: `/executions${search}` }, { label: t("executions.execution") }]} actions={<Button color="secondary" isDisabled={pending} onPress={() => { void refresh(); }}>{t("executions.refreshAuthoritativeExecution")}</Button>} />
    <Link className="text-sm text-brand-secondary" to={`/executions${search}`}>{t("executions.backToExecutions")}</Link>
    <div className="mt-4 space-y-4">{message && <Notice>{message}</Notice>}{retryId && <Link className="text-sm text-brand-secondary" to={`/executions/${encodeURIComponent(retryId)}${search}`}>{t("executions.viewQueuedRecoveryAttempt")}</Link>}{locked && <Notice>{t("executions.operationPendingOrUncertainActionsAreLockedUntilExplicitAuthoritativeRefreshSucceeds")}</Notice>}
      {!item ? <ViewState title={read.error ? t("executions.executionUnavailableOrDeletedRefreshThisExactResourceListContextIsRetained") : t("executions.loadingExecution")} error={!!read.error} /> : <>
        <div className="rounded-xl border border-secondary bg-primary p-5"><div className="flex flex-wrap items-center justify-between gap-3"><div className="flex flex-wrap items-center gap-3"><State item={item} />{item.currentStage && <StatusBadge tone="info" compact>{localizeText(item.currentStage)}</StatusBadge>}</div>{item.state === 'Queued' && <Button color="primary-destructive" isDisabled={pending || locked} onPress={() => select('cancel')}>{t("executions.cancelQueuedRequest")}</Button>}</div><p className="mt-3 break-words text-sm text-secondary">{item.pendingReason || item.completionSummary || t("executions.noAdditionalResultReported")}</p>{item.recoveryReason && <p className="mt-2 break-words text-sm text-secondary">{t("executions.recovery")}{' '}{item.recoveryReason}</p>}</div>
        <div className="grid gap-5 xl:grid-cols-[minmax(0,1.65fr)_minmax(20rem,1fr)]"><TableCard.Root><section aria-labelledby="execution-summary" className="p-5"><h2 id="execution-summary" className="mb-3 text-lg font-semibold text-primary">{t("executions.executionSummary")}</h2><div className="divide-y divide-secondary">{[[t("executions.project"), <Identity item={item} />], [t("executions.created"), timestamp(item.createdAtUtc)], [t("executions.assigned"), item.assignedAtUtc ? timestamp(item.assignedAtUtc) : t("executions.notReported")], [t("executions.started"), item.startedAtUtc ? timestamp(item.startedAtUtc) : t("executions.notReported")], [t("shared.duration"), duration(item, Date.now())]].map(([label, value]) => <div key={String(label)} className="grid gap-2 py-3 sm:grid-cols-[9rem_minmax(0,1fr)]"><span className="text-sm text-tertiary">{label}</span><span className="min-w-0 break-words text-sm text-secondary">{value}</span></div>)}</div></section></TableCard.Root>
          <TableCard.Root><section aria-labelledby="execution-outcome" className="space-y-4 p-5"><h2 id="execution-outcome" className="text-lg font-semibold text-primary">{t("shared.outcome")}</h2><State item={item} /><p className="text-sm text-secondary">{item.completionSummary || item.pendingReason || t("executions.noAdditionalResultReported")}</p><div className="border-t border-secondary pt-4"><p className="text-sm font-medium text-primary">{t("executions.nextPermittedAction")}</p><p className="mt-1 text-sm text-secondary">{item.state === 'Queued' ? t("executions.nextActionWaitForAssignmentOrCancelThisQueuedRequest") : canReconcile(item) ? t("executions.nextActionVerifyAuthoritativeRepositoryIntegrationEvidenceThenRecordTheDispositionBelow") : t("executions.noOperatorExecutionActionIsCurrentlyPermittedReviewReportedStateAndRecovery")}</p></div>{canReconcile(item) && <Button color="secondary" isDisabled={pending || locked} onPress={() => select('reconcile')}>{t("executions.reconcileUncertainIntegration")}</Button>}{item.recoverable != null && <p className="text-sm text-tertiary">{t("executions.recoverable")}: {item.recoverable ? t("shared.yes") : t("shared.no")}</p>}</section></TableCard.Root></div>
        <p className="text-sm text-tertiary">{t("executions.executionFailureDoesNotEstablishWorkerHealthStageHistoryIsNotSupplied")}</p>
        <AdvancedDisclosure title={t("executions.advancedAssignmentAndLeaseEvidence")}><Evidence value={{ assignedWorkerId: item.assignedWorkerId, assignmentId: item.assignmentId, executionId: item.executionId, workerExecutionId: item.workerExecutionId, lease: item.lease }} /></AdvancedDisclosure>
        <AdvancedDisclosure title={t("executions.advancedAttemptLineageAndRecovery")}><p>{t("executions.attempt")}{' '}{item.attemptNumber ?? t("executions.notReported")}</p>{item.retryOfExecutionId && <Link to={`/executions/${encodeURIComponent(item.retryOfExecutionId)}${search}`}>{t("executions.previousAttempt")}{' '}{item.retryOfExecutionId}</Link>}<Evidence value={{ recoveryState: item.recoveryState, recoveryReason: item.recoveryReason, recoverable: item.recoverable, workspaceRecovery: item.workspaceRecovery, integrationResult: item.integrationResult, validationResult: item.validationResult, failureClassification: item.failureClassification, missingRequirements: item.missingRequirements, managedEligibilityState: item.managedEligibilityState, managedEligibilityReasons: item.managedEligibilityReasons, managedEligibilityCheckedAtUtc: item.managedEligibilityCheckedAtUtc }} /><p>{t("executions.theAPIRetainsTheCurrentRecoveryRecordAndPreviousAttemptLinkNot")}</p><Link to={`/executions?project=${encodeURIComponent(item.projectId)}&issue=${encodeURIComponent(item.workReference?.id ?? '')}`}>{t("executions.browseMatchingAttempts")}</Link></AdvancedDisclosure>
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
function Evidence({ value }: { value: unknown }) {
  useLanguage(); return <pre className="whitespace-pre-wrap break-all rounded-lg bg-secondary p-4 text-xs text-secondary">{JSON.stringify(value, null, 2)}</pre>; }
