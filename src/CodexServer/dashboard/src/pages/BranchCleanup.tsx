import { useEffect, useRef, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { ApiError } from '../shared/api/client';
import type { ArchivedBranch, BranchRequest, BranchTarget, ProjectSummary } from '../shared/api/contracts';
import { useApiRead, useRuntime } from '../shared/api/session';
import { branchOperation, branchTargets } from '../shared/api/validation';
import { useWorkers } from '../shared/data';
import { t, tryT, useLanguage } from '../shared/i18n';
import { branchOperationPending, defaultBranchTarget, deletableBranch } from '../shared/model';
import { Card, Loading, Notice, Select } from '../shared/ui';

const ages = [7, 30, 90, 180];
const reasonText = (code: string) => tryT(`branches.reason.${code}`, code);
const wait = (ms: number, signal: AbortSignal) => new Promise<void>((resolve, reject) => {
  const abort = () => { clearTimeout(timer); reject(signal.reason); };
  const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, ms);
  signal.addEventListener('abort', abort, { once: true });
});

function BranchList({ rows, reasons }: { rows: ArchivedBranch[]; reasons?: boolean }) {
  return <ul className="mt-2 max-h-56 overflow-auto rounded-lg bg-secondary px-3 py-2 text-xs">
    {rows.map(row => <li key={row.branch} className="py-1 break-all"><span className="font-mono text-primary">{row.branch}</span>
      {reasons && <span className="block text-tertiary">{row.reason}</span>}</li>)}
  </ul>;
}

/** Deletes a project's old archived branches through a Worker: look first, then delete what the Worker proved integrated. */
export function BranchCleanup({ project }: { project: ProjectSummary }) {
  useLanguage();
  const runtime = useRuntime(), action = useAction(), workers = useWorkers();
  const targets = useApiRead(`/api/v1/projects/${encodeURIComponent(project.id)}/branch-maintenance-targets`, branchTargets);
  const [chosen, setChosen] = useState(''), [days, setDays] = useState(30);
  const [found, setFound] = useState<ArchivedBranch[]>(), [result, setResult] = useState<ArchivedBranch[]>();
  const [phase, setPhase] = useState<'scanning' | 'deleting'>(), [confirm, setConfirm] = useState(false), [error, setError] = useState('');
  const [pausedHere, setPausedHere] = useState<string>();
  const alive = useRef(new AbortController());
  useEffect(() => { const current = new AbortController(); alive.current = current; return () => current.abort(); }, []);

  // Who may delete changes as a Worker pauses and finishes its work; the Worker list refreshes live, the targets do not.
  const refetchTargets = targets.refetch, seenWorkers = useRef(workers.updatedAt);
  useEffect(() => {
    if (seenWorkers.current === workers.updatedAt) return;
    seenWorkers.current = workers.updatedAt;
    void refetchTargets();
  }, [workers.updatedAt, refetchTargets]);

  const inventory = targets.data?.projectId === project.id ? targets.data : undefined;
  const usable = inventory?.targets.filter(target => target.eligible) ?? [];
  const worker: BranchTarget | undefined = usable.find(target => target.workerId === chosen) ?? defaultBranchTarget(usable);
  const policy = workers.data?.find(item => item.workerId === worker?.workerId)?.schedulingPolicy;
  const deletable = (found ?? []).filter(deletableBranch), kept = (found ?? []).filter(row => !deletableBranch(row));
  const reset = () => { setFound(undefined); setResult(undefined); setConfirm(false); setError(''); };

  async function run(apply: boolean) {
    if (!worker || !inventory || phase) return;
    const request: BranchRequest = { operationId: crypto.randomUUID().replaceAll('-', ''), workerId: worker.workerId,
      project: inventory.project, repository: inventory.repository, projectId: inventory.projectId, projectRevision: inventory.revision,
      action: 'archived-branches', apply, olderThanDays: days, limit: 100, offset: 0, timeoutSeconds: 300,
      ...(apply ? { branches: deletable.map(row => ({ branch: row.branch, expectedTip: row.tip ?? '' })) } : {}) };
    const signal = alive.current.signal, resource = `branches-${project.id}`, validate = branchOperation(request);
    const path = `/api/v1/maintenance/executions/${request.operationId}`;
    setPhase(apply ? 'deleting' : 'scanning'); setConfirm(false); setError('');
    if (!apply) setResult(undefined);
    try {
      try { await runtime.mutate(resource, '/api/v1/maintenance/executions', 'POST', request, validate); }
      catch (failure) {
        // The request may have reached the Server anyway; its record decides whether to follow it or report the refusal.
        const exists = await runtime.read(path, signal, validate).then(() => true, (missing: unknown) => missing instanceof ApiError && missing.status === 404 ? false : undefined);
        if (exists === undefined) throw failure;
        await runtime.reconcile(resource, async () => {});
        if (!exists) throw failure;
      }
      for (let attempt = 0; attempt < 180; attempt++) {
        const operation = await runtime.read(path, signal, validate);
        if (!branchOperationPending(operation.status)) {
          if (operation.status === 'uncertain') throw new Error(t('branches.uncertain'));
          const rows = operation.report?.branches ?? [];
          const succeeded = operation.status === 'succeeded' && operation.report?.outcome === 'succeeded';
          if (apply) { setResult(rows); setFound(undefined); }
          else if (succeeded) setFound(rows);
          if (!succeeded) setError(reasonText(operation.report?.reason ?? operation.status));
          return;
        }
        await wait(2000, signal);
      }
      throw new Error(t('branches.stillRunning'));
    } catch (failure) {
      if (signal.aborted) return;
      setError(failure instanceof Error ? reasonText(failure.message) : t('common.actionFailed'));
    } finally {
      if (!signal.aborted) { setPhase(undefined); void refetchTargets(); }
    }
  }
  async function setPolicy(policy: 'Draining' | 'Enabled') {
    if (!worker) return;
    const done = await action.run(`worker-${worker.workerId}`, `/api/v1/workers/${encodeURIComponent(worker.workerId)}/scheduling-policy`, 'PUT', { policy },
      policy === 'Enabled' ? 'workers.resumed' : 'workers.pausedDone');
    if (done) { setPausedHere(policy === 'Draining' ? worker.workerId : undefined); void refetchTargets(); }
  }

  const name = worker?.displayName || worker?.workerId.slice(0, 8) || '';
  const busy = !!phase || !!action.busy;
  return <Card title={t('branches.title')}>
    <p className="text-sm text-tertiary">{t('branches.description', { branch: project.defaultBranch ?? 'main' })}</p>
    {targets.loading ? <Loading /> : targets.hasError ? <div className="mt-4"><Notice tone="bad">{targets.error ?? t('common.loadFailed')}{' '}
      <button className="font-semibold underline" onClick={() => void targets.refetch()}>{t('common.retry')}</button></Notice></div>
      : !worker ? <div className="mt-4"><Notice>{t('branches.noWorker')}{inventory?.targets[0]?.reason && <span className="block text-tertiary">{reasonText(inventory.targets[0].reason)}</span>}</Notice></div>
      : <div className="mt-4 flex flex-col gap-4">
        {usable.length > 1 && <Select label={t('branches.worker')} value={worker.workerId} onChange={value => { setChosen(value); reset(); }}
          options={usable.map(target => ({ value: target.workerId, label: target.displayName || target.workerId.slice(0, 8) }))} />}
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-0 flex-1"><Select label={t('branches.olderThan')} value={String(days)} onChange={value => { setDays(Number(value)); reset(); }}
            options={ages.map(age => ({ value: String(age), label: t('branches.days', { n: age }) }))} /></div>
          <Button size="sm" color="secondary" isDisabled={busy} onClick={() => void run(false)}>{t(found ? 'branches.searchAgain' : 'branches.search')}</Button>
        </div>

        {phase && <Notice tone="run">{t(phase === 'scanning' ? 'branches.scanning' : 'branches.deleting', { worker: name })}</Notice>}
        {error && <Notice tone="bad">{error}</Notice>}

        {found && !phase && <div className="flex flex-col gap-3">
          {deletable.length ? <div><p className="text-sm font-medium text-primary">{t('branches.found', { n: deletable.length })}</p><BranchList rows={deletable} /></div>
            : <p className="text-sm text-secondary">{t('branches.noneFound')}</p>}
          {kept.length > 0 && <details className="text-sm text-tertiary"><summary className="cursor-pointer">{t('branches.kept', { n: kept.length })}</summary><BranchList rows={kept} reasons /></details>}
          {found.length === 100 && <p className="text-xs text-tertiary">{t('branches.firstHundred')}</p>}

          {deletable.length > 0 && (worker.canApply ? !confirm
            ? <div><Button size="sm" color="primary-destructive" isDisabled={busy} onClick={() => setConfirm(true)}>{t('branches.delete', { n: deletable.length })}</Button></div>
            : <div className="rounded-lg bg-secondary p-4">
              <p className="text-sm text-secondary">{t('branches.confirm', { n: deletable.length, repository: project.repository })}</p>
              <div className="mt-3 flex flex-wrap gap-2">
                <Button size="sm" color="primary-destructive" isDisabled={busy} onClick={() => void run(true)}>{t('branches.confirmButton')}</Button>
                <Button size="sm" color="secondary" onClick={() => setConfirm(false)}>{t('common.cancel')}</Button>
              </div>
            </div>
            : <Notice tone="warn">{t(policy === 'Draining' ? 'branches.waitingForWork' : 'branches.needsPause', { worker: name })}
              {worker.mode === 'managed' && policy !== 'Draining' && <div className="mt-2">
                <Button size="sm" color="secondary" isDisabled={busy} onClick={() => void setPolicy('Draining')}>{t('branches.pauseWorker', { worker: name })}</Button></div>}
            </Notice>)}
        </div>}

        {result && !phase && <div className="flex flex-col gap-3">
          <Notice tone="ok">{t('branches.deleted', { n: result.filter(row => row.decision === 'deleted').length })}</Notice>
          {result.some(row => row.decision !== 'deleted') && <details className="text-sm text-tertiary"><summary className="cursor-pointer">
            {t('branches.notDeleted', { n: result.filter(row => row.decision !== 'deleted').length })}</summary><BranchList rows={result.filter(row => row.decision !== 'deleted')} reasons /></details>}
          {pausedHere === worker.workerId && policy === 'Draining' && <div>
            <Button size="sm" color="secondary" isDisabled={busy} onClick={() => void setPolicy('Enabled')}>{t('branches.resumeWorker', { worker: name })}</Button></div>}
        </div>}
      </div>}
  </Card>;
}
