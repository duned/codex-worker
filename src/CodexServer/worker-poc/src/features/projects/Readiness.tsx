import { t, useLanguage, localizeText } from '../../shared/i18n';
import { Link } from 'react-router-dom';
import { useApiRead } from '../../shared/api/session';
import { workers, diagnostics } from '../../shared/api/validation';
import { AdvancedDisclosure, Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import type { Project } from './contracts';
const observationLabels: Record<string, string> = { 'worker-reported-current-revision': t("projects.currentRevision"), 'stale-heartbeat': t("projects.staleHeartbeat"), 'cached-worker-observation': t("projects.cachedObservation"), 'stale-revision': t("projects.staleRevision"), 'not-reported': t("projects.unreportedPreparation") };
export function readiness(value: unknown) {
  const shared = diagnostics(value);
  return { ...shared, projects: shared.projects ?? [] };
}
export function ProjectWorkers({ project }: { project: Project }) {
  useLanguage();
  const read = useApiRead('/api/v1/workers', workers);
  return <section className="space-y-4"><h2 className="text-lg font-semibold text-primary">{t("projects.workerReportedReadinessAndPreparation")}</h2>
    <p className="text-sm text-tertiary">{t("projects.managedRevisionsAreDeliveredToAuthorizedWorkersAssociationToolReadinessRepositoryPermission")}</p>
    {!read.data ? <ViewState title={read.error ? t("projects.workerInventoryUnavailableRefreshWorkersToInspectPreparation") : t("projects.loadingWorkers")} error={!!read.error} /> : !read.data.length ? <ViewState title={t("projects.noWorkerExistsYet")}><a href="/workers?prepare=1" className="text-sm text-brand-secondary">{t("projects.addAndPrepareAWorker")}</a></ViewState> : read.data.map(worker => <WorkerProject key={worker.workerId} project={project} id={worker.workerId} name={worker.displayName ?? worker.workerId} availability={worker.availability} />)}
  </section>;
}
function WorkerProject({ project, id, name, availability }: { project: Project; id: string; name: string; availability: string }) {
  useLanguage();
  const read = useApiRead(`/api/v1/workers/${encodeURIComponent(id)}/diagnostics`, readiness), p = read.data?.projects.find(p => p.projectId === project.id);
  // Server observationStatus is authoritative; retain all distinctions rather than
  // treating capability eligibility as permission to schedule or a ready checkout.
  return <div className="space-y-2 rounded-lg border border-secondary p-4">
    <Link to={`/workers/${encodeURIComponent(id)}?step=preparation&project=${encodeURIComponent(project.id)}`} className="font-medium text-primary">{name}</Link>
    <a href={`/workers/${encodeURIComponent(id)}?step=preparation&project=${encodeURIComponent(project.id)}`} className="ml-3 text-sm text-brand-secondary">{t("projects.prepareAssociateWorker")}</a>
    <StatusBadge tone={availability === 'online' ? 'success' : 'warning'}>{availability}</StatusBadge>
    {!read.data ? <Notice error={!!read.error}>{read.error ? t("projects.readinessUnavailableInspectWorkerDiagnostics") : t("projects.loadingReadiness")}</Notice> : <>
      <p className="text-sm text-secondary">{t("projects.configurationSynchronization")}{' '}{localizeText(read.data.configurationSynchronization)}</p>
      <p className="text-sm text-secondary">{t("projects.reportedRevision")}{' '}{p?.workerReportedRevision ?? t("projects.notReported")}{' '}{t("projects.centralRevision")}{' '}{project.revision}</p>
      <StatusBadge tone={p?.observationStatus === 'worker-reported-current-revision' && availability === 'online' ? 'success' : 'warning'}>{observationLabels[p?.observationStatus ?? 'not-reported'] ?? p?.observationStatus ?? t("projects.workerObservationUnavailable")}</StatusBadge>
      <p className="text-sm text-secondary">{t("projects.checkoutMaterialization")}{' '}<StatusBadge tone={p?.materializationState === 'failed' ? 'error' : 'gray'}>{localizeText(p?.materializationState ?? 'unverified')}</StatusBadge></p>
      <p className="text-sm text-secondary">{t("projects.capabilityRequirements")}{' '}{p ? p.isEligible ? t("projects.matchReportedCapabilities") : t('projects.unmet') : t("projects.notReported")}{t("projects.executionReadinessAndSchedulingRemainSeparate")}</p>
      {p?.missingRequirements.map(reason => <Notice key={reason} error>{reason}</Notice>)}
      <AdvancedDisclosure title={t("projects.advancedWorkerReadinessEvidence")}><p>{t("projects.gitHubAPIReadiness")}{' '}{localizeText(String(read.data.gitHubReady))}{' '}{t("projects.gitReadiness")}{' '}{localizeText(String(read.data.gitReady))}{' '}{t("projects.aIAgentReadiness")}{' '}{localizeText(String(read.data.aiAgentReady))}</p><p>{t("projects.observationStatus")}{' '}{localizeText(p?.observationStatus ?? 'not-reported')}</p><p>{t("projects.provisioning")}{' '}{localizeText(read.data.provisioningState)}</p>{p?.diagnosticCode && <p>{t("projects.diagnostic")}{' '}{p.diagnosticCode}</p>}</AdvancedDisclosure>
    </>}
  </div>;
}
/** Compact list evidence, reusing the same deduplicated Server diagnostics reads. */
export function ProjectBlockers({ project }: { project: Project }) {
  useLanguage();
  const read = useApiRead('/api/v1/workers', workers);
  if (!read.data) return <p className="text-sm text-tertiary">{t("projects.workerPreparation")}{' '}{read.error ? t("projects.unavailableInspectWorkers") : t("projects.loading")}</p>;
  if (!read.data.length) return <p className="text-sm text-warning-primary">{t("projects.noWorkerExistsAddAndPrepareAWorker")}</p>;
  return <div className="space-y-1 text-sm text-secondary"><p className="text-xs text-tertiary">{t("projects.readinessObservationsUpTo3Workers")}</p>{read.data.slice(0, 3).map(worker => <WorkerBlocker key={worker.workerId} project={project} id={worker.workerId} name={worker.displayName ?? worker.workerId} />)}</div>;
}
function WorkerBlocker({ project, id, name }: { project: Project; id: string; name: string }) {
  useLanguage();
  const read = useApiRead(`/api/v1/workers/${encodeURIComponent(id)}/diagnostics`, readiness), p = read.data?.projects.find(p => p.projectId === project.id);
  const observation = observationLabels[p?.observationStatus ?? 'not-reported'] ?? p?.observationStatus ?? t("projects.observationUnavailableInspectWorker");
  return <p>{name}: {!read.data ? read.error ? t("projects.readinessUnavailableInspectWorker") : t("projects.loadingReadiness2") : <>{localizeText(observation)}{p?.missingRequirements.length ? ` · ${p.missingRequirements.join('; ')}` : ''}{p?.materializationState === 'failed' ? t("projects.checkoutFailedInspectPreparation") : ''}</>}</p>;
}
