import { useEffect, useRef, useState } from 'react';
import { Trash01 } from '@untitledui/icons';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { workers } from '../../shared/api/validation';
import { t, useLanguage } from '../../shared/i18n';
import { ActionDialog } from '../../shared/Dialogs';
import { Notice, StatusBadge } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { timestamp } from '../../model';
import type { Project } from './contracts';
import { branchOperation, selectable, selections } from './archived-branches.mjs';

type Branch = { branch: string; tip?: string; completedAtUtc?: string; decision: string; reason: string; localDeleted?: boolean; remoteDeleted?: boolean };
type Request = { operationId: string; workerId: string; project: string; repository: string; action: string; apply: boolean; olderThanDays: number; limit: number; offset: number; timeoutSeconds: number; branches?: { branch: string; expectedTip: string }[] };

export function ArchivedBranches({ project }: { project: Project }) {
  const session = useSession();
  return <BranchInventory key={`${session.generation}:${project.id}:${project.revision}`} project={project} />;
}
function BranchInventory({ project }: { project: Project }) {
  useLanguage();
  const runtime = useRuntime(), inventory = useApiRead('/api/v1/workers', workers);
  const [workerId, setWorker] = useState(''), [days, setDays] = useState(30), [offset, setOffset] = useState(0);
  const [rows, setRows] = useState<Branch[]>(), [results, setResults] = useState<Branch[]>();
  const [selected, setSelected] = useState<string[]>([]), [confirm, setConfirm] = useState<string[]>();
  const [ageConfirm, setAgeConfirm] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  const [retained, setRetained] = useState<Request>();
  const controller = useRef(new AbortController());
  useEffect(() => { const current = new AbortController(); controller.current = current; return () => current.abort(); }, []);
  const worker = inventory.data?.find(item => item.workerId === workerId);
  const available = !!worker && ['online', 'draining'].includes(worker.availability) && project.enabled;
  const drained = available && worker?.schedulingPolicy === 'Draining' && worker.activeAssignments === 0;
  const validDays = Number.isInteger(days) && days >= 1 && days <= 36500;
  function reset() { setRows(undefined); setSelected([]); setResults(undefined); setError(''); setConfirm(undefined); }
  async function execute(request: Request, resume = false): Promise<Branch[]> {
    const signal = controller.current.signal;
    const resource = `archived-branches:${project.id}:${request.workerId}`;
    setRetained(request);
    if (!resume) await runtime.mutate(resource, '/api/v1/maintenance/executions', 'POST', request, value => branchOperation(value, request));
    for (let attempt = 0; attempt < 155; attempt++) {
      const operation = await runtime.read(`/api/v1/maintenance/executions/${request.operationId}`, signal, value => branchOperation(value, request));
      if (signal.aborted) throw new Error('Aborted');
      if (!['pending', 'running'].includes(operation.status)) {
        if (operation.status === 'uncertain') throw Error(t('branches.pending'));
        await runtime.reconcile(resource, async () => {});
        setRetained(undefined);
        if (request.apply) setResults(operation.report?.branches ?? []);
        if (operation.status !== 'succeeded' || operation.report?.outcome !== 'succeeded') {
          if (!request.apply || !operation.report) throw Error(operation.report?.reason ?? operation.status);
          setError(operation.report.reason ?? operation.status);
        }
        return operation.report?.branches ?? [];
      }
      await new Promise<void>((resolve, reject) => {
        const abort = () => { clearTimeout(timer); reject(new Error('Aborted')); };
        const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, 2000);
        signal.addEventListener('abort', abort, { once: true });
      });
    }
    throw Error(t('branches.pending'));
  }
  function request(apply: boolean, names: string[] = []): Request {
    return { operationId: crypto.randomUUID().replaceAll('-', ''), workerId, project: project.name, repository: project.repository,
      action: 'archived-branches', apply, olderThanDays: days, limit: 100, offset: apply ? 0 : offset, timeoutSeconds: 300,
      ...(apply ? { branches: selections(rows ?? [], names) } : {}) };
  }
  async function load(apply = false, names: string[] = [], resume = false, preview = false) {
    if (busy || !available || !validDays || apply && (!drained || !selections(rows ?? [], names).length)) return;
    setBusy(true); setError(''); setSelected([]);
    try {
      const current = resume && retained ? retained : request(apply, names);
      const branches = await execute(current, resume);
      if (current.apply) {
        setResults(branches); setRows(undefined);
        setRows(await execute(request(false)));
      } else {
        setRows(branches);
        if (preview) {
          setAgeConfirm(true);
          setConfirm(branches.filter(selectable).map(branch => branch.branch));
        }
      }
    } catch (failure) {
      if (!controller.current.signal.aborted) { setRows(undefined); setError(failure instanceof Error ? failure.message : t('branches.unavailable')); }
    } finally { if (!controller.current.signal.aborted) setBusy(false); }
  }
  return <section className="mt-8 space-y-4" aria-labelledby="archived-branches">
    <h2 id="archived-branches" className="text-lg font-semibold text-primary">{t('branches.title')}</h2>
    <p className="text-sm text-tertiary">{t('branches.context', { project: project.name })}</p>
    <TableCard.Root><div className="space-y-4 p-5">
      <div className="flex flex-wrap items-end gap-4">
        <label className="text-sm text-secondary">{t('branches.worker')}<select className="block rounded border border-secondary bg-primary p-2" value={workerId} disabled={busy || !!retained} onChange={event => { reset(); setWorker(event.target.value); setOffset(0); }}><option value="">{t('branches.chooseWorker')}</option>{inventory.data?.filter(item => item.capabilities?.some(cap => cap.type === 'protocol' && cap.name === 'archived-feature-maintenance-v1')).map(item => <option key={item.workerId} value={item.workerId}>{item.displayName ?? item.workerId}</option>)}</select></label>
        <label className="text-sm text-secondary">{t('branches.days')}<input className="block w-28 rounded border border-secondary bg-primary p-2" type="number" min={1} max={36500} value={days} aria-invalid={!validDays} aria-describedby={!validDays ? "branch-age-error" : undefined} disabled={busy || !!retained} onChange={event => { reset(); setDays(Number(event.target.value)); setOffset(0); }} /></label>
        <Button color="secondary" isDisabled={busy || !available || !validDays} onPress={() => { void load(false, [], !!retained); }}>{t(retained ? 'branches.resume' : 'branches.load')}</Button>
        <Button color="secondary" isDisabled={busy || !!retained || !available || !validDays} onPress={() => { void load(false, [], false, true); }}>{t('branches.previewAge')}</Button>
        <Button color="primary-destructive" iconLeading={Trash01} isDisabled={busy || !!retained || !drained || !selected.length} onPress={() => { setAgeConfirm(false); setConfirm(selected); }}>{t('branches.deleteSelected', { count: selected.length })}</Button>
      </div>
      {!validDays && <p id="branch-age-error" role="alert">{t('branches.invalidDays')}</p>}
      {!drained && <p className="text-sm text-tertiary">{t('branches.drain')}</p>}
      {inventory.loading && <p role="status">{t('branches.pending')}</p>}
      {!inventory.loading && (inventory.error || !available) && <Notice error>{t('branches.unavailable')}</Notice>}
      {busy && <p role="status">{t('branches.pending')}</p>}
      {error && <Notice error>{t('branches.error', { reason: error })}</Notice>}
      {rows && !rows.length && <p>{t('branches.empty')}</p>}
      {rows && rows.length > 0 && <div className="overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr>{(['branches.select', 'branches.name', 'branches.completed', 'branches.eligibility'] as const).map(key => <th className="p-3" key={key} scope="col">{t(key)}</th>)}<th scope="col">{t('branches.actions')}</th></tr></thead><tbody>{rows.map(row => <tr key={row.branch} className="border-t border-secondary"><td className="p-3"><input type="checkbox" aria-label={t('branches.selectBranch', { branch: row.branch })} disabled={busy || !!retained || !selectable(row)} checked={selected.includes(row.branch)} onChange={event => setSelected(old => event.target.checked ? [...old, row.branch] : old.filter(name => name !== row.branch))} /></td><td className="break-all p-3">{row.branch}</td><td className="p-3">{row.completedAtUtc && Number.isFinite(Date.parse(row.completedAtUtc)) ? <>{timestamp(row.completedAtUtc)}<p className="text-xs text-tertiary">{t('branches.age', { count: Math.max(0, Math.floor((Date.now() - Date.parse(row.completedAtUtc)) / 86400000)) })}</p></> : t('branches.unknown')}</td><td className="p-3"><StatusBadge tone={selectable(row) ? 'success' : 'warning'}>{t(selectable(row) ? 'branches.eligible' : 'branches.retained')}</StatusBadge><p>{row.reason}</p></td><td className="p-3"><Button color="secondary-destructive" iconLeading={Trash01} isDisabled={busy || !!retained || !drained || !selectable(row)} onPress={() => { setAgeConfirm(false); setConfirm([row.branch]); }}>{t('branches.delete')}</Button></td></tr>)}</tbody></table></div>}
      {rows && <div className="flex flex-wrap items-center gap-3"><span>{t('branches.window', { offset, count: rows.length })}</span><Button color="secondary" isDisabled={busy || !!retained || offset === 0} onPress={() => { reset(); setOffset(Math.max(0, offset - 100)); }}>{t('branches.previous')}</Button><Button color="secondary" isDisabled={busy || !!retained || rows.length < 100 || offset >= 10000} onPress={() => { reset(); setOffset(offset + 100); }}>{t('branches.next')}</Button></div>}
      {results && <div role="status"><h3 className="font-semibold">{t('branches.results')}</h3><ul>{results.map(row => <li className="break-all py-2" key={row.branch}>{row.branch} · {t(row.decision === 'deleted' ? 'branches.deleted' : 'branches.retained')} · {row.reason}</li>)}</ul></div>}
    </div></TableCard.Root>
    {confirm && <ActionDialog isOpen title={t('branches.confirm')} description={t('branches.confirmAge', { project: project.name, days, offset })} actionLabel={t('branches.delete')} destructive disabled={busy || !drained || !confirm.length || !validDays} onClose={() => setConfirm(undefined)} onSubmit={async () => { setConfirm(undefined); await load(true, confirm); }}><p className="text-sm font-semibold">{t('branches.eligible')}</p>{!confirm.length && <p role="status">{t('branches.noEligible')}</p>}<ul className="max-h-60 overflow-auto text-sm">{confirm.map(name => <li className="break-all py-1" key={name}>{name}</li>)}</ul>{ageConfirm && <><h3 className="text-sm font-semibold">{t('branches.retained')}</h3><ul className="max-h-60 overflow-auto text-sm">{rows?.filter(row => !selectable(row)).map(row => <li className="break-all py-1" key={row.branch}>{row.branch} · {row.reason}</li>)}</ul></>}</ActionDialog>}
  </section>;
}
