import { t, useLanguage, localizeText } from '../../shared/i18n';
import { Link } from 'react-aria-components';
import { useSearchParams } from 'react-router-dom';
import { PageHeading, ResourceIdentity, StatusBadge, ViewState, Notice } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead } from '../../shared/api/session';
import { workers, nodes } from '../../shared/api/validation';
import { statusColor, workerListSignals } from '../../model.js';
import { EnrollmentDialog } from './EnrollmentDialog';
export function WorkersPage() {
  useLanguage();
  const inventory = useApiRead('/api/v1/workers', workers), capabilities = useApiRead('/api/v1/nodes', nodes);
  const [params, setParams] = useSearchParams();
  function enrollment(open: boolean) { const next = new URLSearchParams(params); if (open) next.set('enroll', '1'); else { next.delete('enroll'); next.delete('prepare'); } setParams(next); }
  return <section><PageHeading title={t("workers.workers")} actions={<Button onPress={() => enrollment(true)}>{t("workers.addWorker")}</Button>} />
    {inventory.error ? <ViewState error title={t("workers.workerInventoryUnavailable")}>{inventory.error}</ViewState> : !inventory.data ? <ViewState title={t("workers.loadingWorkers")} /> : !inventory.data.length ? <ViewState title={t("workers.noWorkersRegistered")}>{t("workers.addAWorkerToEnrollANewMachineOrSafelyAssociateAn")}</ViewState> : <TableCard.Root><div className="divide-y divide-secondary">{inventory.data.map(worker => {
      const node = capabilities.data?.find(item => item.id === worker.workerId && item.kind === 'worker');
      const signals = workerListSignals(worker, node);
      const signal = (label: string, value: string) => <div className="min-w-0 space-y-1"><span className="block text-xs font-medium text-tertiary">{label}</span><StatusBadge tone={statusColor(value)}>{localizeText(value)}</StatusBadge></div>;
      return <article key={worker.workerId} className="grid gap-4 p-5 sm:grid-cols-2 xl:grid-cols-[minmax(12rem,1.4fr)_repeat(3,minmax(8rem,1fr))_minmax(10rem,1.2fr)_auto] xl:items-center">
        <div className="min-w-0"><Link href={`/workers/${encodeURIComponent(worker.workerId)}`}><ResourceIdentity name={worker.displayName || t("workers.worker")} id={worker.workerId} /></Link></div>
        {signal(t("workers.connection"), signals.connection)}
        {signal(t("workers.freshness"), signals.freshness)}
        {signal(t("workers.executionReadiness"), signals.readiness)}
        <div className="min-w-0 space-y-1"><span className="block text-xs font-medium text-tertiary">{t("workers.slotUsage")}</span><span className="font-medium text-primary">{signals.occupiedSlots ?? t("workers.unknown")} / {signals.totalSlots ?? t("workers.unknown")}</span><div><StatusBadge tone={statusColor(signals.scheduling)}>{localizeText(signals.scheduling)}</StatusBadge></div></div>
        <div className="min-w-0 space-y-1"><span className="block text-xs font-medium text-tertiary">{t("workers.projectContext")}</span><span className="break-words text-sm text-secondary">{signals.projects.length ? signals.projects.join(', ') : t("workers.noneReported")}</span></div>
        <Button href={`/workers/${encodeURIComponent(worker.workerId)}?step=preparation`} color="secondary" size="sm">{t("workers.prepareWorker")}</Button>
      </article>;
    })}</div></TableCard.Root>}
    {capabilities.error && <Notice error>{t("workers.readinessObservationsUnavailable")}</Notice>}
    {(params.get('enroll') === '1' || params.get('prepare') === '1') && <EnrollmentDialog onClose={() => enrollment(false)} />}
  </section>;
}
