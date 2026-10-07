import { useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { projects, workers } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { PageHeading, ViewState, Notice, AdvancedDisclosure, StatusBadge } from '../../shared/Presentation';
import { ConfirmationDialog, ActionDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../untitled/components/base/input/input';
import { TableCard } from '../../untitled/components/application/table/table';
import { issueLink, timestamp, duration } from '../../model';
import { execution, executionList, cancellationResult, reconciliationResult, query, offset, states, presentation, canReconcile, validEvidence, type Execution } from './model';

export function ExecutionsPage() {
  const { resourceId } = useParams(), location = useLocation();
  // Resource keys discard dialog drafts when navigation changes identity.
  return resourceId ? <ExecutionDetail key={resourceId} id={resourceId} search={location.search} /> : <ExecutionList />;
}
function Identity({ item }: { item: Execution }) {
  const p = useApiRead('/api/v1/projects', projects), w = useApiRead('/api/v1/workers', workers);
  const project = p.data?.find(x => x.id === item.projectId), worker = w.data?.find(x => x.workerId === item.assignedWorkerId);
  const href = issueLink(item.workReference, project?.repository);
  return <div className="space-y-1 break-words text-sm text-secondary">
    <Link to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link>
    <p>{href ? <a href={href}>GitHub Issue #{item.workReference?.id}</a> : item.workReference ? `${item.workReference.type} ${item.workReference.id}` : 'Work reference unavailable'}</p>
    <p>{item.assignedWorkerId ? <Link to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{worker?.displayName ?? item.assignedWorkerId}</Link> : 'Worker unassigned'}</p>
  </div>;
}
function Timing({ item }: { item: Execution }) {
  return <div className="text-sm text-tertiary"><p>Created {timestamp(item.createdAtUtc)}</p>{item.assignedAtUtc && <p>Assigned {timestamp(item.assignedAtUtc)}</p>}{item.startedAtUtc && <p>Started {timestamp(item.startedAtUtc)}</p>}{item.completedAtUtc && <p>Completed {timestamp(item.completedAtUtc)}</p>}<p>{duration(item, Date.now())}</p></div>;
}
function State({ item }: { item: Execution }) { const state = presentation(item); return <StatusBadge tone={state.tone}>{state.text}</StatusBadge>; }
function ExecutionList() {
  const [params, setParams] = useSearchParams(), search = `?${params}`, read = useApiRead(query(search), executionList);
  const p = useApiRead('/api/v1/projects', projects);
  const change = (key: string, value: string) => { const next = new URLSearchParams(params); if (value) next.set(key, value); else next.delete(key); next.delete('offset'); setParams(next); };
  const page = (value: number) => { const next = new URLSearchParams(params); next.set('offset', String(value)); setParams(next); };
  return <><PageHeading title="Executions" actions={<Button color="secondary" onPress={() => { void read.refetch(); }}>Refresh executions</Button>} />
    <div className="mb-6 flex flex-wrap items-end gap-4">
      <label className="text-sm text-secondary">Project<select className="block max-w-full rounded-lg border border-secondary bg-primary p-2" value={params.get('project') ?? ''} onChange={e => change('project', e.target.value)}><option value="">All projects</option>{params.get('project') && !p.data?.some(x => x.id === params.get('project')) && <option value={params.get('project') ?? ''}>{params.get('project')}</option>}{p.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
      <label className="text-sm text-secondary">State<select className="block rounded-lg border border-secondary bg-primary p-2" value={params.get('state') ?? ''} onChange={e => change('state', e.target.value)}><option value="">All states</option>{states.map(x => <option key={x}>{x}</option>)}</select></label>
      <Input label="GitHub Issue number" value={params.get('issue') ?? ''} onChange={value => change('issue', value)} />
    </div>
    <p className="mb-4 text-sm text-tertiary">Up to 50 requests per page. Total history count is unavailable. Execution outcomes are separate from Worker health.</p>
    {!read.data ? <ViewState title={read.error ? 'Executions unavailable. Refresh to retrieve current data.' : 'Loading executions…'} error={!!read.error} /> : !read.data.length ? <ViewState title="No matching execution requests." /> : <TableCard.Root><ul className="divide-y divide-secondary">{read.data.map(item => <li key={item.id} className="flex flex-wrap items-start justify-between gap-4 p-5"><div className="min-w-0"><Identity item={item} /><Link className="text-sm text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}>Execution details<span className="block break-all text-xs text-tertiary">ID {item.id}</span></Link></div><div className="space-y-2"><State item={item} /><Timing item={item} />{(item.pendingReason || item.recoveryReason || item.completionSummary) && <p className="max-w-lg break-words text-sm text-secondary">{item.pendingReason || item.recoveryReason || item.completionSummary}</p>}</div></li>)}</ul></TableCard.Root>}
    <div className="mt-4 flex flex-wrap gap-3"><Button color="secondary" isDisabled={offset(search) === 0 || read.loading} onPress={() => page(Math.max(0, offset(search) - 50))}>Previous page</Button><Button color="secondary" isDisabled={!read.data || read.data.length < 50 || offset(search) >= 10000} onPress={() => page(Math.min(10000, offset(search) + 50))}>Next page</Button><span className="text-sm text-tertiary">Offset {offset(search)}</span></div>
  </>;
}
function ExecutionDetail({ id, search }: { id: string; search: string }) {
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
      if (runtime.snapshot().generation === generation) setMessage('Authoritative execution refreshed. Review current state and retained recovery evidence before another action.');
    } catch { if (runtime.snapshot().generation === generation) setMessage('Authoritative execution unavailable. Any uncertain operation remains locked; no action was resubmitted.'); }
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
      if (runtime.snapshot().generation === generation) setMessage('Action accepted. Server observations are refreshing.');
    } catch { if (runtime.snapshot().generation === generation) setMessage('Action result unavailable. Explicitly refresh authoritative execution before another action.'); throw new Error('Action unavailable.'); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  const close = () => { select(undefined); setEvidence(''); setCommit(''); setDisposition('NotIntegrated'); };
  return <><PageHeading title="Execution" resourceId={id} breadcrumbs={[{ label: 'Executions', href: `/dashboard-preview/executions${search}` }, { label: 'Execution' }]} actions={<Button color="secondary" isDisabled={pending} onPress={() => { void refresh(); }}>Refresh authoritative execution</Button>} />
    <Link className="text-sm text-brand-secondary" to={`/executions${search}`}>Back to executions</Link>
    <div className="mt-4 space-y-4">{message && <Notice>{message}</Notice>}{retryId && <Link className="text-sm text-brand-secondary" to={`/executions/${encodeURIComponent(retryId)}${search}`}>View queued recovery attempt</Link>}{locked && <Notice>Operation pending or uncertain. Actions are locked until explicit authoritative refresh succeeds.</Notice>}
      {!item ? <ViewState title={read.error ? 'Execution unavailable or deleted. Refresh this exact resource; list context is retained.' : 'Loading execution…'} error={!!read.error} /> : <>
        <TableCard.Root><div className="space-y-4 p-5"><State item={item} /><Identity item={item} />{item.currentStage && <p className="text-secondary">Reported stage: {item.currentStage}</p>}<p className="break-words text-secondary">{item.pendingReason || item.completionSummary || 'No additional result reported.'}</p>{item.recoveryReason && <p className="break-words text-secondary">Recovery: {item.recoveryReason}</p>}<Timing item={item} />
          <p className="text-sm text-secondary">{item.state === 'Queued' ? 'Next action: wait for assignment or cancel this queued request.' : canReconcile(item) ? 'Next action: verify authoritative repository integration evidence, then record the disposition below.' : 'No operator execution action is currently permitted. Review reported state and recovery evidence.'}</p>
          <div className="flex flex-wrap gap-3">{item.state === 'Queued' && <Button color="primary-destructive" isDisabled={pending || locked} onPress={() => select('cancel')}>Cancel queued request</Button>}{canReconcile(item) && <Button color="secondary" isDisabled={pending || locked} onPress={() => select('reconcile')}>Reconcile uncertain integration</Button>}</div>
          <p className="text-sm text-tertiary">Execution failure does not establish Worker health. Stage history is not supplied by this API; no progress sequence is inferred.</p>
        </div></TableCard.Root>
        <AdvancedDisclosure title="Advanced · assignment and lease evidence"><Evidence value={{ assignedWorkerId: item.assignedWorkerId, assignmentId: item.assignmentId, executionId: item.executionId, workerExecutionId: item.workerExecutionId, lease: item.lease }} /></AdvancedDisclosure>
        <AdvancedDisclosure title="Advanced · attempt lineage and recovery"><p>Attempt {item.attemptNumber ?? 'not reported'}</p>{item.retryOfExecutionId && <Link to={`/executions/${encodeURIComponent(item.retryOfExecutionId)}${search}`}>Previous attempt · {item.retryOfExecutionId}</Link>}<Evidence value={{ recoveryState: item.recoveryState, recoveryReason: item.recoveryReason, recoverable: item.recoverable, workspaceRecovery: item.workspaceRecovery, integrationResult: item.integrationResult, validationResult: item.validationResult, failureClassification: item.failureClassification, missingRequirements: item.missingRequirements, managedEligibilityState: item.managedEligibilityState, managedEligibilityReasons: item.managedEligibilityReasons, managedEligibilityCheckedAtUtc: item.managedEligibilityCheckedAtUtc }} /><p>The API retains the current recovery record and previous-attempt link, not a complete event history. Browse matching requests to find subsequent attempts.</p><Link to={`/executions?project=${encodeURIComponent(item.projectId)}&issue=${encodeURIComponent(item.workReference?.id ?? '')}`}>Browse matching attempts</Link></AdvancedDisclosure>
      </>}
    </div>
    {selected === 'cancel' && <ConfirmationDialog isOpen title="Cancel queued request" description="Cancel only this queued request. Assigned and running work cannot be cancelled here. The Server checks current state again before applying cancellation." actionLabel="Cancel request" destructive onClose={close} onSubmit={submit} disabled={pending || locked} />}
    {selected === 'reconcile' && <ActionDialog isOpen title="Reconcile uncertain integration" description="Check authoritative repository evidence first. Not integrated queues a new attempt with a fresh workspace. Integrated records the verified commit without rerunning work. This does not resolve conflicts or repeat implementation." actionLabel="Record verified disposition" onClose={close} onSubmit={submit} disabled={pending || locked || !validEvidence(disposition, evidence, commit)}>
      <label className="text-sm text-secondary">Verified disposition<select className="mt-1 block w-full rounded-lg border border-secondary bg-primary p-2" value={disposition} onChange={e => setDisposition(e.target.value)}><option value="NotIntegrated">Not integrated · queue new attempt</option><option value="Integrated">Integrated · record verified commit</option></select></label>
      <Input label="Authoritative integration evidence" hint="Describe the repository, base branch and evidence checked. 1–1000 printable characters; exclude credentials." isRequired maxLength={1000} value={evidence} onChange={setEvidence} />
      {disposition === 'Integrated' && <Input label="Full integrated commit ID" isRequired value={commit} onChange={setCommit} />}
    </ActionDialog>}
  </>;
}
function Evidence({ value }: { value: unknown }) { return <pre className="whitespace-pre-wrap break-all rounded-lg bg-secondary p-4 text-xs text-secondary">{JSON.stringify(value, null, 2)}</pre>; }
