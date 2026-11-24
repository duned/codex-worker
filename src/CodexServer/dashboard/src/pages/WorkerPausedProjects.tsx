import { useEffect } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead } from '../shared/api/session';
import type { WorkerDiagnostics } from '../shared/api/contracts';
import { projectResume } from '../shared/api/validation';
import { t, tryT, useLanguage } from '../shared/i18n';
import { pausedOnWorker } from '../shared/model';
import { Card, Notice, Pill, Row, Rows } from '../shared/ui';

/** Pauses a managed Worker holds in memory, with the reason it reported and a one-click resume. */
export function WorkerPausedProjects({ workerId, diagnostics, online, onResumed }: { workerId: string; diagnostics?: WorkerDiagnostics; online: boolean; onResumed(): void }) {
  useLanguage();
  const base = `/api/v1/workers/${encodeURIComponent(workerId)}/projects`;
  const paused = (diagnostics?.projects ?? []).filter(pausedOnWorker);
  const last = useApiRead(`${base}/resume`, projectResume, paused.length > 0);
  const action = useAction();
  const operation = last.data ?? undefined, pending = operation?.status === 'pending' || operation?.status === 'running';
  const refetchLast = last.refetch;
  // Detail reads are not refreshed by the Server freshness markers; follow a request until the Worker answers.
  useEffect(() => {
    if (!pending) return;
    const id = setInterval(() => { void refetchLast(); onResumed(); }, 5000);
    return () => clearInterval(id);
  }, [pending, refetchLast, onResumed]);
  if (!paused.length) return null;
  const outcome = operation && (pending ? 'pending' : operation.diagnostic ?? operation.status);
  async function resume(projectId: string) {
    if (await action.run(`project-resume-${workerId}`, `${base}/${encodeURIComponent(projectId)}/resume`, 'POST', {}, 'workers.resumeRequested')) {
      void last.refetch();
      onResumed();
    }
  }
  return <Card title={t('workers.pausedTitle')} flush>
    <p className="px-5 pt-4 text-sm text-tertiary">{t('workers.pausedHint')}</p>
    {outcome && <div className="px-5 pt-3"><Notice tone={operation?.status === 'succeeded' ? 'ok' : pending ? 'run' : 'warn'}>
      {tryT(`workers.resume.${outcome}`, outcome)}</Notice></div>}
    <Rows>{paused.map(project => <Row key={project.projectId} className="grid-cols-[minmax(0,1fr)_auto] items-start gap-3">
      <span className="min-w-0">
        <span className="flex flex-wrap items-center gap-2"><span className="font-medium text-primary">{project.projectName}</span><Pill tone="warn">{t('workers.pausedPill')}</Pill></span>
        <span className="mt-1 block text-sm break-words text-tertiary">{project.detail ?? t('workers.pausedNoReason')}</span>
      </span>
      <Button size="sm" color="secondary" isDisabled={!online || pending || !!action.busy} onClick={() => void resume(project.projectId)}>{t('workers.resumeProject')}</Button>
    </Row>)}</Rows>
  </Card>;
}
