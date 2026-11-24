import { useCallback, useState } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead, useRuntime } from '../shared/api/session';
import type { Validator } from '../shared/api/client';
import { diagnostics as validateDiagnostics, record, worker as validateWorker } from '../shared/api/validation';
import { useExecutions, useNodes } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { isActive, isOnline, pausedOnWorker, scheduling, schedulingBlockerText, schedulingTone, slots } from '../shared/model';
import { Ago, Card, Empty, KeyValues, PageHeader, Pill, ReadState, Row, Rows, Tile, CopyValue } from '../shared/ui';
import { ActiveExecution, ExecutionHead, ExecutionRow } from './ExecutionRow';
import { WorkerMaintenance } from './WorkerMaintenance';
import { WorkerPanel } from './WorkerPanel';
import { WorkerPausedProjects } from './WorkerPausedProjects';
import { WorkerTools } from './WorkerTools';

const journal: Validator<{ state: string; lines: string[] }> = value => {
  const item = record(value);
  if (typeof item.state !== 'string' || !Array.isArray(item.lines) || item.lines.some(line => typeof line !== 'string')) throw new Error('Invalid journal.');
  return item as { state: string; lines: string[] };
};
const gigabytes = (bytes?: number) => bytes === undefined ? '—' : `${(bytes / 1024 ** 3).toFixed(1)} GB`;

export function WorkerPage() {
  useLanguage();
  const { workerId = '' } = useParams(), base = `/api/v1/workers/${encodeURIComponent(workerId)}`;
  const read = useApiRead(base, validateWorker), diagnostics = useApiRead(`${base}/diagnostics`, validateDiagnostics);
  const nodes = useNodes(), executions = useExecutions(), action = useAction(), runtime = useRuntime();
  const [tab, setTab] = useState<'work' | 'diagnostics'>('work'), [editing, setEditing] = useState(false);
  const [log, setLog] = useState<{ state: string; lines: string[] }>(), [loadingLog, setLoadingLog] = useState(false);
  const refetchWorker = read.refetch;
  const refreshWorker = useCallback(() => { void refetchWorker(); }, [refetchWorker]);
  const refetchDiagnostics = diagnostics.refetch;
  // A finished tool action can change readiness: re-read the Worker and its diagnostics once.
  const refreshReadiness = useCallback(() => { void refetchWorker(); void refetchDiagnostics(); }, [refetchWorker, refetchDiagnostics]);
  const worker = read.data;
  if (!worker) return <><PageHeader title={t('workers.title')} crumbs={[{ label: t('nav.workers'), to: '/workers' }]} /><Card flush><ReadState read={read}>{null}</ReadState></Card></>;
  const state = scheduling(worker), use = slots(worker), online = isOnline(worker);
  const mine = (executions.data ?? []).filter(item => item.assignedWorkerId === worker.workerId);
  const running = mine.filter(isActive), recent = mine.filter(item => !isActive(item) && item.state !== 'Queued').slice(0, 10);
  const node = nodes.data?.find(item => item.id === worker.workerId), resources = worker.hostResources;
  const paused = worker.schedulingPolicy === 'Disabled' || worker.schedulingPolicy === 'Draining';
  const setPolicy = (policy: 'Enabled' | 'Draining') => action.run(`worker-${worker.workerId}`, `${base}/scheduling-policy`, 'PUT', { policy }, policy === 'Enabled' ? 'workers.resumed' : 'workers.pausedDone');
  async function loadJournal() {
    setLoadingLog(true);
    try { setLog(await runtime.read(`${base}/journal?lines=100&refresh=true`, new AbortController().signal, journal)); }
    catch (error) { setLog({ state: error instanceof Error ? error.message : t('common.loadFailed'), lines: [] }); }
    finally { setLoadingLog(false); }
  }
  const blockers = [...(diagnostics.data?.reasons ?? []), ...(worker.schedulingBlocker ? [schedulingBlockerText(worker.schedulingBlocker)] : [])];

  return <>
    <PageHeader crumbs={[{ label: t('nav.workers'), to: '/workers' }, { label: worker.displayName || worker.workerId.slice(0, 8) }]}
      title={worker.displayName || worker.workerId.slice(0, 8)} description={`${t('workers.managed')}${worker.workerVersion ? ` · v${worker.workerVersion}` : ''}${worker.platform ? ` · ${worker.platform}` : ''}`}
      actions={<>
        <Button color="secondary" isDisabled={!!action.busy} onClick={() => void setPolicy(paused ? 'Enabled' : 'Draining')}>{t(paused ? 'workers.resume' : 'workers.pause')}</Button>
        <Button color="primary" onClick={() => setEditing(true)}>{t('workers.edit')}</Button>
      </>} />

    <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
      <Tile label={t('workers.connection')} tone={online ? 'ok' : 'idle'} value={t(online ? 'workers.online' : 'workers.offline')} detail={<>{t('workers.lastSignal')} <Ago value={worker.lastHeartbeatAtUtc} /></>} />
      <Tile label={t('workers.scheduling')} tone={schedulingTone[state]} value={t(`scheduling.${state}`)} detail={t(`scheduling.${state}.detail`)} />
      <Tile label={t('workers.slots')} tone={use.used ? 'run' : 'idle'} value={t('workers.slotsUsed', { used: use.used, total: use.total })} detail={t('workers.slotsFree', { n: Math.max(0, use.total - use.used) })} />
    </div>
    {state === 'blocked' && blockers.length > 0 && <Card><p className="text-sm font-semibold text-primary">{t('workers.whyBlocked')}</p>
      <ul className="mt-2 list-disc pl-5 text-sm text-secondary">{blockers.map(reason => <li key={reason}>{reason}</li>)}</ul></Card>}

    <WorkerPausedProjects workerId={worker.workerId} diagnostics={diagnostics.data} online={online} onResumed={refreshReadiness} />
    <WorkerMaintenance worker={worker} onProgress={refreshWorker} />

    <div role="tablist" aria-label={t('workers.sections')} className="flex gap-6 border-b border-secondary">
      {(['work', 'diagnostics'] as const).map(value => <button key={value} role="tab" aria-selected={tab === value} onClick={() => setTab(value)}
        className={`-mb-px border-b-2 pb-2.5 text-sm font-semibold ${tab === value ? 'border-brand text-primary' : 'border-transparent text-tertiary hover:text-secondary'}`}>{t(value === 'work' ? 'workers.tabWork' : 'workers.tabDiagnostics')}</button>)}
    </div>

    {tab === 'work' ? <>
      <Card title={t('workers.now')} actions={<span className="text-sm text-tertiary">{t('workers.slotsUsed', { used: use.used, total: use.total })}</span>} flush>
        <ReadState read={executions}><Rows>{running.length ? running.map(item => <ActiveExecution key={item.id} execution={item} showWorker={false} />)
          : <Empty>{t(state === 'accepting' ? 'workers.nothingNowAccepting' : 'workers.nothingNow')}</Empty>}</Rows></ReadState>
      </Card>
      <Card title={t('workers.recent')} flush><ReadState read={executions}><Rows>
        <ExecutionHead />{recent.length ? recent.map(item => <ExecutionRow key={item.id} execution={item} />) : <Empty>{t('workers.noRecent')}</Empty>}
      </Rows></ReadState></Card>
    </> : <div className="grid grid-cols-1 gap-6 lg:grid-cols-2 lg:items-start">
      <WorkerTools node={node} nodes={nodes} online={online} onSettled={refreshReadiness} />
      <div className="flex flex-col gap-6">
        <Card title={t('workers.machine')}><KeyValues items={[
          [t('workers.system'), worker.platform ?? '—'], [t('workers.cpu'), resources?.cpuUsagePercent !== undefined ? `${Math.round(resources.cpuUsagePercent)} %` : '—'],
          [t('workers.memory'), resources?.totalMemoryBytes ? `${gigabytes(resources.usedMemoryBytes)} / ${gigabytes(resources.totalMemoryBytes)}` : '—'],
          [t('workers.disk'), gigabytes(resources?.diskAvailableBytes)], [t('workers.cores'), resources?.logicalCpuCount ?? '—'],
          [t('workers.identifier'), <CopyValue value={worker.workerId} />]
        ]} /></Card>
        <Card title={t('workers.projects')} flush><ReadState read={diagnostics}><Rows>
          {diagnostics.data?.projects?.length ? diagnostics.data.projects.map(project => <Row key={project.projectId} className="grid-cols-[minmax(0,1fr)_auto]">
            <span><span className="block font-medium text-primary">{project.projectName}</span>
              {!project.isEligible && project.missingRequirements.length > 0 && <span className="text-sm text-tertiary">{t('workers.missing', { list: project.missingRequirements.join(', ') })}</span>}</span>
            {pausedOnWorker(project) ? <Pill tone="warn">{t('workers.pausedPill')}</Pill>
              : <Pill tone={project.isEligible ? 'ok' : 'warn'}>{t(project.isEligible ? 'workers.canRun' : 'workers.cannotRun')}</Pill>}
          </Row>) : <Empty>{t('workers.noProjects')}</Empty>}
        </Rows></ReadState></Card>
        <Card title={t('workers.journal')} actions={<Button size="sm" color="secondary" isDisabled={loadingLog || !online} onClick={() => void loadJournal()}>{t(log ? 'common.refresh' : 'workers.loadJournal')}</Button>}>
          {log ? log.lines.length ? <pre className="max-h-80 overflow-auto rounded-lg bg-secondary p-3 font-mono text-xs whitespace-pre-wrap text-secondary">{log.lines.join('\n')}</pre>
            : <p className="text-sm text-tertiary">{log.state === 'pending' ? t('workers.journalPending') : log.state}</p>
            : <p className="text-sm text-tertiary">{t(online ? 'workers.journalHint' : 'workers.journalOffline')}</p>}
        </Card>
      </div>
    </div>}
    <WorkerPanel open={editing} onClose={() => setEditing(false)} worker={worker} />
  </>;
}
