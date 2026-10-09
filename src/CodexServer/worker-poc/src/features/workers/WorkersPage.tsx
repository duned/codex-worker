import { t, useLanguage, localizeText } from '../../shared/i18n';
import { GuidDisplay } from '../../shared/GuidDisplay';
import { Link } from 'react-aria-components';
import { useSearchParams } from 'react-router-dom';
import { PageHeading, ViewState, Notice } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead } from '../../shared/api/session';
import { workers, nodes } from '../../shared/api/validation';
import { statusColor, workerListSignals } from '../../model.js';
import { EnrollmentDialog } from './EnrollmentDialog';

const dotTone = (value: string) => {
  const tone = statusColor(value);
  return tone === 'success' ? 'bg-success-solid' : tone === 'warning' ? 'bg-warning-solid' : tone === 'error' ? 'bg-error-solid' : tone === 'info' ? 'bg-utility-blue-500' : 'bg-secondary-solid';
};

function heartbeatDetail(value?: string) {
  if (!value) return t('workers.heartbeatUnavailable');
  const date = Date.parse(value);
  if (!Number.isFinite(date)) return t('workers.heartbeatUnavailable');
  const seconds = Math.max(0, Math.floor((Date.now() - date) / 1000));
  const age = seconds < 60 ? t('workers.secondsAgo', { count: seconds }) : seconds < 3600
    ? t('workers.minutesAgo', { count: Math.floor(seconds / 60) }) : t('workers.hoursAgo', { count: Math.floor(seconds / 3600) });
  return `${t('workers.heartbeat')} ${age}`;
}

export function WorkersPage() {
  useLanguage();
  const inventory = useApiRead('/api/v1/workers', workers), capabilities = useApiRead('/api/v1/nodes', nodes);
  const [params, setParams] = useSearchParams();
  const filter = params.get('filter') ?? 'all';
  function enrollment(open: boolean) { const next = new URLSearchParams(params); if (open) next.set('enroll', '1'); else { next.delete('enroll'); next.delete('prepare'); } setParams(next); }
  const allWorkers = inventory.data ?? [];
  const shownWorkers = allWorkers.filter(worker => filter === 'all' || (filter === 'connected' ? worker.availability === 'online' : worker.availability !== 'online'));
  return <section className="mx-auto max-w-[1132px]">
    <PageHeading title={t('workers.workers')} actions={<Button color="secondary" onPress={() => enrollment(true)}>{t('workers.addWorker')}</Button>} />
    {inventory.error ? <ViewState error title={t('workers.workerInventoryUnavailable')}>{inventory.error}</ViewState> : !inventory.data ? <ViewState title={t('workers.loadingWorkers')} /> : <>
      <TableCard.Root className="mb-4 overflow-hidden">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-secondary px-5 py-4">
          <div><h2 className="text-sm font-semibold text-primary">{t('workers.workerInventory')}</h2><p className="mt-1 text-xs text-tertiary">{t('workers.inventorySummary', { count: allWorkers.length })}</p></div>
          <label className="sr-only" htmlFor="worker-filter">{t('workers.filterWorkers')}</label>
          <select id="worker-filter" value={filter} onChange={event => { const next = new URLSearchParams(params); if (event.target.value === 'all') next.delete('filter'); else next.set('filter', event.target.value); setParams(next); }} className="h-9 rounded-lg border border-secondary bg-primary px-3 text-sm text-secondary">
            <option value="all">{t('workers.allWorkers')}</option><option value="connected">{t('workers.connectedWorkers')}</option><option value="disconnected">{t('workers.disconnectedWorkers')}</option>
          </select>
        </div>
        {allWorkers.length === 0 ? <div className="p-5"><Notice>{t('workers.noWorkersRegistered')}. {t('workers.addAWorkerToEnrollANewMachineOrSafelyAssociateAn')}</Notice></div> : shownWorkers.length === 0 ? <div className="p-5"><Notice>{t('workers.noWorkersMatchFilter')}</Notice></div> : <div className="overflow-x-auto">
          <table className="w-full min-w-[940px] table-fixed border-collapse text-left">
            <colgroup><col className="w-[22%]"/><col className="w-[24%]"/><col className="w-[23%]"/><col className="w-[10%]"/><col className="w-[21%]"/></colgroup>
            <thead className="bg-secondary"><tr className="h-[52px] text-[11px] font-semibold uppercase tracking-wide text-tertiary">
              {[t('workers.worker'), t('workers.connectionFreshness'), t('workers.readinessScheduling'), t('workers.slots'), t('workers.projects')].map(label => <th key={label} scope="col" className="px-5">{label}</th>)}
            </tr></thead>
            <tbody className="divide-y divide-secondary">{shownWorkers.map(worker => {
              const node = capabilities.data?.find(item => item.id === worker.workerId && item.kind === 'worker');
              const signals = workerListSignals(worker, node);
              const readinessDetail = !node ? t('workers.readinessUnavailable') : node.observationsStale ? t('workers.observationIsStale') : signals.scheduling === 'DrainRequested' || signals.scheduling === 'Draining' ? t('workers.drainingNoNewWork') : t('workers.schedulingPolicyDetail', { policy: localizeText(signals.scheduling) });
              const projects = signals.projects;
              const workerTone = worker.availability === 'online' ? 'text-success-primary' : worker.availability === 'offline' ? 'text-error-primary' : 'text-fg-quaternary';
              return <tr key={worker.workerId} className="h-[87px]">
                <td className="px-5"><Link href={`/workers/${encodeURIComponent(worker.workerId)}`} className="flex min-w-0 items-center gap-3 rounded focus-visible:outline-2 focus-visible:outline-focus-ring">
                  <span aria-hidden="true" className={`flex size-[38px] shrink-0 items-center justify-center rounded-full ${workerTone === 'text-success-primary' ? 'bg-success-secondary' : workerTone === 'text-error-primary' ? 'bg-error-secondary' : 'bg-tertiary'} ${workerTone}`}><svg viewBox="0 0 24 24" className="size-[19px]" fill="none" stroke="currentColor" strokeWidth="1.7"><rect x="3" y="3" width="18" height="13" rx="2"/><path d="M12 16v4m-5 0h10"/></svg></span>
                  <span className="min-w-0"><span className="block truncate text-sm font-semibold text-primary">{worker.displayName || t('workers.worker')}{worker.workerVersion && <span className="ml-2 text-xs font-normal text-tertiary">{worker.workerVersion}</span>}</span><span className="block truncate text-xs text-tertiary"><GuidDisplay value={worker.workerId} /></span></span>
                </Link></td>
                <td className="px-5"><div className="flex items-center gap-2 text-sm text-secondary"><span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${dotTone(signals.connection)}`}/>{localizeText(signals.connection)}</div><div className="mt-1.5 flex flex-wrap items-center gap-1.5 text-xs text-tertiary">
                  <span className={`rounded-full px-2 py-0.5 font-semibold ${signals.freshness === 'Current' ? 'bg-success-secondary text-success-primary' : signals.freshness === 'Stale' ? 'bg-warning-secondary text-warning-primary' : 'bg-tertiary text-tertiary'}`}>{localizeText(signals.freshness)}</span><span>· {heartbeatDetail(worker.lastHeartbeatAtUtc)}</span>
                </div></td>
                <td className="px-5"><div className="flex items-center gap-2 text-sm text-secondary"><span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${dotTone(signals.readiness)}`}/>{localizeText(signals.readiness)}</div><span className="mt-1.5 block truncate text-xs text-tertiary">{readinessDetail}</span></td>
                <td className="px-5"><span className="block text-sm font-semibold text-primary">{signals.occupiedSlots ?? t('workers.unknown')} / {signals.totalSlots ?? t('workers.unknown')}</span><span className="mt-1 block text-xs text-tertiary">{Number.isFinite(signals.occupiedSlots) && Number.isFinite(signals.totalSlots) ? signals.occupiedSlots === signals.totalSlots ? t('workers.atCapacity') : t('workers.occupied') : t('workers.reported')}</span></td>
                <td className="px-5"><Link href={`/workers/${encodeURIComponent(worker.workerId)}`} className="flex items-center justify-between gap-2 rounded focus-visible:outline-2 focus-visible:outline-focus-ring"><span className="min-w-0"><span className="block truncate text-sm text-brand-secondary">{projects[0] ?? t('workers.noActiveProjects')}</span><span className="mt-1 block truncate text-xs text-tertiary">{projects.length > 1 ? t('workers.additionalProjects', { count: projects.length - 1 }) : projects.length === 1 ? t('workers.oneActiveProject') : ''}</span></span><span aria-hidden="true" className="text-xl text-brand-secondary">›</span></Link></td>
              </tr>;
            })}</tbody>
          </table>
        </div>}
        <p className="border-t border-secondary px-5 py-3 text-xs text-tertiary">{t('workers.showingWorkers', { shown: shownWorkers.length, total: allWorkers.length })}</p>
      </TableCard.Root>
      <section aria-labelledby="worker-status-guide" className="mx-auto max-w-[1088px] rounded-lg border border-secondary bg-secondary p-5 sm:p-6">
        <h2 id="worker-status-guide" className="mb-4 text-sm font-semibold text-primary">{t('workers.readingWorkerStatus')}</h2>
        <div className="grid gap-x-8 gap-y-3 sm:grid-cols-2 text-xs text-secondary">
          {[
            ['success', t('workers.legendConnection')], ['gray', t('workers.legendScheduling')],
            ['success', t('workers.legendReadiness')], ['gray', t('workers.legendSlots')],
            ['warning', t('workers.legendFreshness')], ['gray', t('workers.legendProjects')]
          ].map(([tone, text]) => <p key={text} className="flex items-center gap-3"><span aria-hidden="true" className={`size-2 shrink-0 rounded-full ${dotTone(tone)}`}/>{text}</p>)}
        </div>
      </section>
    </>}
    {capabilities.error && <Notice error>{t('workers.readinessObservationsUnavailable')}</Notice>}
    {(params.get('enroll') === '1' || params.get('prepare') === '1') && <EnrollmentDialog onClose={() => enrollment(false)} />}
  </section>;
}
