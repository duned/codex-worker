import { t, useLanguage, localizeText } from '../../shared/i18n';
import { Link } from 'react-aria-components';
import { useSearchParams } from 'react-router-dom';
import { PageHeading, ResourceIdentity, StatusBadge, ViewState, Notice } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead } from '../../shared/api/session';
import { workers, nodes } from '../../shared/api/validation';
import { statusColor } from '../../model.js';
import { EnrollmentDialog } from './EnrollmentDialog';
export function WorkersPage() {
  useLanguage();
  const inventory = useApiRead('/api/v1/workers', workers), capabilities = useApiRead('/api/v1/nodes', nodes);
  const [params, setParams] = useSearchParams();
  function enrollment(open: boolean) { const next = new URLSearchParams(params); if (open) next.set('enroll', '1'); else { next.delete('enroll'); next.delete('prepare'); } setParams(next); }
  return <section><PageHeading title={t("workers.workers")} actions={<Button onPress={() => enrollment(true)}>{t("workers.addWorker")}</Button>} />
    {inventory.error ? <ViewState error title={t("workers.workerInventoryUnavailable")}>{inventory.error}</ViewState> : !inventory.data ? <ViewState title={t("workers.loadingWorkers")} /> : !inventory.data.length ? <ViewState title={t("workers.noWorkersRegistered")}>{t("workers.addAWorkerToEnrollANewMachineOrSafelyAssociateAn")}</ViewState> : <TableCard.Root><div className="divide-y divide-secondary">{inventory.data.map(worker => {
      const node = capabilities.data?.find(item => item.id === worker.workerId && item.kind === 'worker');
      return <article key={worker.workerId} className="flex flex-wrap items-start justify-between gap-4 p-5"><div className="min-w-0 space-y-2"><Link href={`/workers/${encodeURIComponent(worker.workerId)}`}><ResourceIdentity name={worker.displayName || t("workers.worker")} id={worker.workerId} /></Link>
        <p>{t("workers.connection")}{' '}<StatusBadge tone={statusColor(worker.availability)}>{localizeText(worker.availability)}</StatusBadge>{' '}{t("workers.working")}{' '}{localizeText(worker.lifecycleState ?? t("workers.unknown"))}{' '}{t("workers.prerequisites")}{' '}{localizeText(node?.executionReadiness ?? t("workers.unavailable"))} · {node ? node.observationsStale ? localizeText('Stale') : localizeText('Current') : t("workers.freshnessUnavailable")}</p>
        <p>{worker.activeExecutions ?? t("workers.unknown")} / {worker.maximumCapacity ?? worker.capacity ?? t("workers.unknown")}{' '}{t("workers.activeSlotsScheduling")}{' '}{localizeText(worker.schedulingPolicy ?? t("workers.unknown"))}</p></div>
        <Button href={`/workers/${encodeURIComponent(worker.workerId)}?step=preparation`} color="secondary" size="sm">{t("workers.prepareWorker")}</Button></article>;
    })}</div></TableCard.Root>}
    {capabilities.error && <Notice error>{t("workers.readinessObservationsUnavailable")}</Notice>}
    {(params.get('enroll') === '1' || params.get('prepare') === '1') && <EnrollmentDialog onClose={() => enrollment(false)} />}
  </section>;
}
