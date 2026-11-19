import { useEffect, useRef, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useRuntime } from '../shared/api/session';
import type { ArchivedBranch, ArchivedBranchDeletion, ProjectSummary } from '../shared/api/contracts';
import { archivedBranchDeletion, archivedBranchPage } from '../shared/api/validation';
import { t, tryT, useLanguage } from '../shared/i18n';
import { branchBatches, deletableBranch } from '../shared/model';
import { Ago, Card, Notice, Select } from '../shared/ui';

const ages = [7, 30, 90, 180];
/** The Server inventory is bounded; this keeps one scan from paging forever. */
const maximumPages = 100;
const reasonText = (code: string) => tryT(`branches.reason.${code}`, code);

function BranchList({ rows, why }: { rows: { branch: string; reason: string; committedAtUtc?: string | null }[]; why?: boolean }) {
  return <ul className="mt-2 max-h-56 overflow-auto rounded-lg bg-secondary px-3 py-2 text-xs">
    {rows.map(row => <li key={row.branch} className="py-1 break-all"><span className="font-mono text-primary">{row.branch}</span>
      {(why || row.committedAtUtc) && <span className="block text-tertiary">
        {why && reasonText(row.reason)}{why && row.committedAtUtc && ' · '}{row.committedAtUtc && <Ago value={row.committedAtUtc} />}
      </span>}</li>)}
  </ul>;
}

/** Deletes a project's old archived branches straight from GitHub: the Server looks first, then deletes what it proved is already in the base branch. */
export function BranchCleanup({ project }: { project: ProjectSummary }) {
  useLanguage();
  const runtime = useRuntime();
  const [days, setDays] = useState(30);
  const [found, setFound] = useState<ArchivedBranch[]>(), [result, setResult] = useState<ArchivedBranchDeletion[]>();
  const [phase, setPhase] = useState<'scanning' | 'deleting'>(), [progress, setProgress] = useState({ done: 0, total: 0 });
  const [confirm, setConfirm] = useState(false), [error, setError] = useState('');
  const alive = useRef(new AbortController());
  useEffect(() => { const current = new AbortController(); alive.current = current; return () => current.abort(); }, []);

  const deletable = (found ?? []).filter(deletableBranch), kept = (found ?? []).filter(row => !deletableBranch(row));
  const reset = () => { setFound(undefined); setResult(undefined); setConfirm(false); setError(''); };
  const base = `/api/v1/projects/${encodeURIComponent(project.id)}/archived-branches`;

  async function scan(signal: AbortSignal) {
    const rows: ArchivedBranch[] = [];
    for (let page = 0, offset = 0; page < maximumPages; page++) {
      const current = await runtime.read(`${base}?olderThanDays=${days}&offset=${offset}`, signal, archivedBranchPage(project.id, days, offset));
      rows.push(...current.branches);
      offset += current.branches.length;
      setProgress({ done: offset, total: current.total });
      if (!current.branches.length || offset >= current.total) break;
    }
    return rows;
  }

  async function search() {
    if (phase) return;
    const signal = alive.current.signal;
    setPhase('scanning'); setProgress({ done: 0, total: 0 }); reset();
    try { const rows = await scan(signal); if (!signal.aborted) setFound(rows); }
    catch (failure) { if (!signal.aborted) setError(failure instanceof Error ? failure.message : t('common.loadFailed')); }
    finally { if (!signal.aborted) setPhase(undefined); }
  }

  async function remove() {
    if (phase || !deletable.length) return;
    const signal = alive.current.signal, resource = `branches-${project.id}`, done: ArchivedBranchDeletion[] = [];
    setPhase('deleting'); setConfirm(false); setError(''); setProgress({ done: 0, total: deletable.length });
    try {
      for (const batch of branchBatches(deletable.map(row => ({ branch: row.branch, expectedTip: row.tip })))) {
        const answer = await runtime.mutate(resource, `${base}/delete`, 'POST', { olderThanDays: days, branches: batch }, archivedBranchDeletion(project.id, batch));
        done.push(...answer.branches);
        setProgress({ done: done.length, total: deletable.length });
        if (answer.branches.some(row => row.outcome === 'failed')) break;
      }
    } catch (failure) {
      if (signal.aborted) return;
      // The request may have reached GitHub anyway: a fresh look is the only reliable answer.
      setError(`${failure instanceof Error ? failure.message : t('common.actionFailed')} ${t('branches.uncertain')}`);
      await runtime.reconcile(resource, async () => {}).catch(() => undefined);
    } finally {
      if (!signal.aborted) { setResult(done); setFound(undefined); setPhase(undefined); }
    }
  }

  const removed = (result ?? []).filter(row => row.outcome === 'deleted'), notRemoved = (result ?? []).filter(row => row.outcome !== 'deleted');
  return <Card title={t('branches.title')}>
    <p className="text-sm text-tertiary">{t('branches.description', { branch: project.defaultBranch ?? 'main', repository: project.repository })}</p>
    <div className="mt-4 flex flex-col gap-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="min-w-0 flex-1"><Select label={t('branches.olderThan')} value={String(days)} onChange={value => { setDays(Number(value)); reset(); }}
          options={ages.map(age => ({ value: String(age), label: t('branches.days', { n: age }) }))} /></div>
        <Button size="sm" color="secondary" isDisabled={!!phase} onClick={() => void search()}>{t(found ? 'branches.searchAgain' : 'branches.search')}</Button>
      </div>

      {phase && <Notice tone="run">{t(phase === 'scanning' ? 'branches.scanning' : 'branches.deleting', progress)}</Notice>}
      {error && <Notice tone="bad">{error}</Notice>}

      {found && !phase && <div className="flex flex-col gap-3">
        {deletable.length ? <div><p className="text-sm font-medium text-primary">{t('branches.found', { n: deletable.length })}</p><BranchList rows={deletable} /></div>
          : <p className="text-sm text-secondary">{t('branches.noneFound')}</p>}
        {kept.length > 0 && <details className="text-sm text-tertiary"><summary className="cursor-pointer">{t('branches.kept', { n: kept.length })}</summary><BranchList rows={kept} why /></details>}

        {deletable.length > 0 && (!confirm
          ? <div><Button size="sm" color="primary-destructive" onClick={() => setConfirm(true)}>{t('branches.delete', { n: deletable.length })}</Button></div>
          : <div className="rounded-lg bg-secondary p-4">
            <p className="text-sm text-secondary">{t('branches.confirm', { n: deletable.length, repository: project.repository })}</p>
            <div className="mt-3 flex flex-wrap gap-2">
              <Button size="sm" color="primary-destructive" onClick={() => void remove()}>{t('branches.confirmButton')}</Button>
              <Button size="sm" color="secondary" onClick={() => setConfirm(false)}>{t('common.cancel')}</Button>
            </div>
          </div>)}
      </div>}

      {result && !phase && <div className="flex flex-col gap-3">
        <Notice tone={notRemoved.some(row => row.outcome === 'failed') ? 'warn' : 'ok'}>{t('branches.deleted', { n: removed.length })}</Notice>
        {notRemoved.length > 0 && <details className="text-sm text-tertiary"><summary className="cursor-pointer">{t('branches.notDeleted', { n: notRemoved.length })}</summary>
          <BranchList rows={notRemoved} why /></details>}
      </div>}
    </div>
  </Card>;
}
