import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useApiRead } from '../../shared/api/session';
import { provisioningPlans } from '../../shared/api/validation';
import type { WorkerObservation, WorkerReadiness } from '../../shared/api/contracts';
import { timestamp } from '../../model.js';
import { useState } from 'react';
function WorkerAdvancedDetails({ worker, diagnostics }: { worker: WorkerObservation; diagnostics?: WorkerReadiness }) {
  useLanguage();
  const plans = useApiRead('/api/v1/provisioning', provisioningPlans);
  const matching = plans.data?.filter(plan => plan.workerId === worker.workerId);
  return <div className="poc-worker-advanced-content">
    <p>{t("workers.workerVersion")}{' '}{worker.workerVersion ?? diagnostics?.workerVersion ?? t("workers.unreported")}{' '}{t("workers.configurationSynchronization")}{' '}{localizeText(diagnostics?.configurationSynchronization ?? t("workers.unavailable"))}</p>
    <p>{t("workers.platform")}{' '}{worker.platform ?? t("workers.unreported")}{' '}{t("workers.registered")}{' '}{timestamp(worker.firstRegisteredAtUtc)}</p>
    <p>{t("workers.activeWorkerProjects")}{' '}{worker.activeProjects?.join('; ') || t("workers.noneReported")}{t("workers.executionTimeNamesDoNotEstablishCentralProjectAssociation")}</p>
    <p>{t("workers.capabilityObservations")}{' '}{diagnostics?.capabilityObservationsCurrent == null ? localizeText('Unreported') : diagnostics.capabilityObservationsCurrent ? localizeText('Current') : t("workers.staleOrUnavailable")}</p>
    {diagnostics?.reasons?.map(reason => <p key={reason}>{reason}</p>)}
    {diagnostics?.recentOperationalError && <p>{diagnostics.recentOperationalError}</p>}
    <a href="/executions">{t("workers.inspectUncertainExecutionRecovery")}</a>
    <p>{t("workers.legacyPlanHistory")}</p>
    {!matching ? <p>{plans.error ? t("workers.legacyProvisioningHistoryUnavailable") : t("workers.loadingHistory")}</p> : !matching.length ? <p>{t("workers.noLegacyPlansInTheAvailableHistory")}</p> : matching.map(plan => <div key={plan.id}><p>{localizeText(plan.state)} · {timestamp(plan.createdAtUtc)}{' '}{t("workers.iD")}{' '}{plan.id}</p><p>{plan.actions.map(action => `${action.type} · ${action.name}${action.version ? ` ${action.version}` : ''}`).join('; ') || t("workers.noActionsRequired")}{plan.currentActionId && t('workers.currentAction', { id: plan.currentActionId })}</p></div>)}
  </div>;
}
export function WorkerAdvanced({ worker, diagnostics }: { worker: WorkerObservation; diagnostics?: WorkerReadiness }) {
  useLanguage();
  const [open, setOpen] = useState(false);
  return <details className="poc-worker-information-section poc-worker-advanced" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>{t("workers.advancedWorkerDiagnosticsAndLegacyProvisioningHistory")}</summary>
    {open && <WorkerAdvancedDetails worker={worker} diagnostics={diagnostics} />}
  </details>;
}
