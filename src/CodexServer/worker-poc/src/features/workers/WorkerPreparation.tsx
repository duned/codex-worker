import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useLocation } from 'react-router-dom';
import type { NodeSummary, ProjectSummary, WorkerObservation, WorkerReadiness } from '../../shared/api/contracts';
import { TableCard } from '../../untitled/components/application/table/table';
import { Button } from '../../untitled/components/base/buttons/button';
import { Notice } from '../../shared/Presentation';
const steps = ['registration', 'preparation', 'project', 'activation'];
const titles = ['Registration', 'Preparation', t("workers.projectContext"), 'Activation'];
export function WorkerPreparation({ worker, diagnostics, node, projects }: { worker: WorkerObservation; diagnostics?: WorkerReadiness; node?: NodeSummary; projects?: ProjectSummary[] }) {
  useLanguage();
  const location = useLocation(), params = new URLSearchParams(location.search);
  const requested = params.get('step'), projectId = params.get('project');
  const step = requested && steps.includes(requested) ? requested : projectId ? 'project' : 'registration';
  const href = (value: string) => { const next = new URLSearchParams(params); next.set('step', value); return `${location.pathname}?${next}`; };
  const current = worker.availability === 'online' && diagnostics?.configurationSynchronization === 'synchronized' && diagnostics.capabilityObservationsCurrent === true && node && !node.observationsStale;
  const evidence = (value?: boolean) => current && value ? t("workers.workerReportedEvidencePresent") : t("workers.unavailableOrUnverified");
  return <TableCard.Root><TableCard.Header title={t("workers.workerPreparation")} description={t("workers.resumeFromAuthoritativeObservationsPreparationNeverEnablesScheduling")} /><div className="space-y-4 p-5">
    <nav aria-label={t("workers.workerPreparationSteps")} className="flex flex-wrap gap-2">{steps.map((value, index) => <Button key={value} href={href(value)} color="secondary" size="sm" aria-current={step === value ? 'step' : undefined}>{index + 1} · {localizeText(titles[index])}</Button>)}</nav>
    {step === 'registration' && <p>{t("workers.registered2")}{' '}{localizeText(worker.availability)} · {localizeText(worker.lifecycleState ?? t("workers.lifecycleUnreported"))}{t("workers.connectivityDoesNotEstablishExecutionReadinessLeaveAndReturnAtAnyTime")}</p>}
    {step === 'preparation' && <><p>{t("workers.workerVersion")}{' '}{worker.workerVersion ?? diagnostics?.workerVersion ?? t("workers.unreported")}.</p><p>{t("workers.managedConfiguration")}{' '}{localizeText(diagnostics?.configurationSynchronization ?? t("workers.unavailable"))}{t("workers.repairInvalidRuntimeDefaultsOnTheNodeThenRefreshConfigurationAndRe")}</p>
      <p>{t("workers.codexServiceAccountAuthentication")}{' '}{current ? localizeText(node.capabilities.find(item => item.definition.id === 'codex-cli')?.state.authentication ?? t("workers.unknown")) : t("workers.unavailableOrUnverified")}{t("workers.executionPreflight")}{' '}{evidence(diagnostics?.aiAgentReady)}{t("workers.cLILoginAloneDoesNotEstablishSuccessfulExecutionPreflight")}</p>
      <p>{t("workers.workerGitHubAuthentication")}{' '}{current ? localizeText(node.capabilities.find(item => item.definition.id === 'github-cli')?.state.authentication ?? t("workers.unknown")) : t("workers.unavailableOrUnverified")}{t("workers.serverGitHubLoginWorkerLoginAndScopedRepositoryPermissionsAreIndependentInspect")}</p></>}
    {step === 'project' && <><p>{t("workers.authorizedManagedWorkersReceiveCentralProjectsAndRevisionsThroughSnapshotsNoSeparate")}</p>
      {!projects ? <Notice>{t("workers.centralProjectsUnavailable")}</Notice> : !projects.length ? <p>{t("workers.noCentralProjectExists")}{' '}<a href="/projects">{t("workers.createAProject")}</a>{' '}{t("workers.andReturn")}</p> : <>
        {projectId && !projects.some(project => project.id === projectId) && <Notice error>{t("workers.theSelectedProjectIsUnavailableOrDeleted")}</Notice>}
        {[...projects].sort((a, b) => Number(b.id === projectId) - Number(a.id === projectId)).map(project => {
          const report = diagnostics?.projects?.find(item => item.projectId === project.id);
          const scoped = (name: string) => worker.capabilities?.some(item => item.type === 'authentication' && item.name === name && item.scope?.toLowerCase() === project.repository.toLowerCase());
          return <article key={project.id} className="space-y-2 rounded-lg border border-secondary p-4"><h3 className="font-semibold text-primary"><a href={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</a></h3><p>{project.repository}{' '}{t("workers.centralRevision")}{' '}{project.revision ?? t("workers.unknown")} · {project.enabled == null ? t("workers.enablementUnknown") : project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</p>
            <p>{t("workers.workerRevision")}{' '}{report?.workerReportedRevision ?? t("workers.notReported")} · {localizeText(report?.observationStatus ?? 'not-reported')}{t("workers.checkout")}{' '}{localizeText(report?.materializationState ?? 'unverified')}{report?.diagnosticCode && ` · ${report.diagnosticCode}`}.</p>
            <p>{t("workers.scopedGitHubAPIAccess")}{' '}{evidence(scoped('github-api'))}{t("workers.gitCheckoutPushPermission")}{' '}{evidence(scoped('git-repository'))}.</p>
            <p>{t("workers.scopedProjectEligibility")}{' '}{report ? report.isEligible ? t("workers.requirementsMatchCurrentRevisionAndFreshnessStillGovernActivation") : t("workers.missingRequirements") : t("workers.notReported")}. {report?.missingRequirements.join('; ')}</p>
            <p>{t("workers.requirements")}{' '}{project.requirements?.map(item => `${item.type} · ${item.name}${item.version ? ` ${item.version}` : ''}`).join('; ') || t("workers.noneReported")}{t("workers.workerScopedGitHubReadWriteAndGitCheckoutPushAccessRemainSeparate")}</p>
            <Button href={`${location.pathname}?${new URLSearchParams({ step: 'preparation', project: project.id })}`} color="secondary" size="sm">{t("workers.prepareForThisProject")}</Button></article>;
        })}</>}
    </>}
    {step === 'activation' && <><p>{diagnostics?.canActivate ? t("workers.currentServerEvidencePermitsAnExplicitActivationRequestInWorkerControls") : t("workers.activationBlockedUntilCurrentServerEvidenceIsReported")}{' '}{t("workers.existingAssignmentsRetainLeasesWhenDrainingOrDisablingScheduling")}</p>{(diagnostics?.activationBlockingReasons ?? [t("workers.readinessEvidenceUnavailable")]).map(reason => <p key={reason}>{reason}</p>)}</>}
  </div></TableCard.Root>;
}
