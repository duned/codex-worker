import { t, useLanguage, localizeText, statusLabel } from './shared/i18n';
import { ExternalLink } from './shared/Actions';
import { StatusBadge, PageHeading, AdvancedDisclosure } from './shared/Presentation';
import { Button } from './untitled/components/base/buttons/button';
import { Table, TableCard } from './untitled/components/application/table/table';
import { FeaturedIcon } from './untitled/components/foundations/featured-icon/featured-icon';
import { Activity, Terminal } from '@untitledui/icons';

import { duration, issueLink, statusColor, terminalStates, timestamp, workerExecutions } from './model.js';

function Status({ value }) {
  useLanguage(); return <StatusBadge tone={statusColor(value)}>{value ?? t("shared.unknown")}</StatusBadge>; }
function Fact({ label, children }) {
  useLanguage(); return <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{label}</dt><dd className="text-sm text-secondary">{children}</dd></div>; }
function Execution({ item, projects, now, active }) {
  useLanguage();
  const project = projects?.find(project => project.id === item.projectId);
  const work = item.workReference;
  const url = issueLink(work, project?.repository);
  const identity = work?.type === 'github-issue' ? `${t('home.issue')}${work.id}` : `${work?.type ?? t("shared.work")} ${work?.id ?? t("shared.unknown")}`;
  return <TableCard.Root className="poc-current"><div className="poc-execution p-5">
    <div><p className="poc-project mb-1 text-sm font-medium text-tertiary"><a href={'/projects/' + encodeURIComponent(item.projectId)}>{project?.name ?? item.projectId}</a></p>
      <h4 className="text-lg font-semibold text-primary">{url ? <ExternalLink href={url}>{identity}</ExternalLink> : identity}</h4>
      <p className="poc-muted text-sm text-tertiary">{work?.type === 'github-issue' && t("shared.issueTitleUnavailable")}{!url && work?.type === 'github-issue' && t("shared.issueLinkUnavailable")}<a href={'/executions/' + encodeURIComponent(item.id)}>{t("shared.executionDetails")}</a></p>
    </div>
    <div className="space-y-2"><Status value={item.state} />{active && <p>{t("shared.currentStage")}{' '}<strong className="text-brand-secondary">{localizeText(item.currentStage || t("shared.notReported"))}</strong></p>}
      <p className="poc-muted text-sm text-tertiary">{active ? t('shared.elapsed') : t('shared.durationLabel')}: {duration(item, now)}</p>
      <p className="poc-muted text-sm text-tertiary">{active ? item.startedAtUtc ? t('executions.started') : t('executions.assigned') : item.completedAtUtc ? t('executions.completed') : t('shared.requested')}: {timestamp(active ? item.startedAtUtc ?? item.assignedAtUtc : item.completedAtUtc ?? item.createdAtUtc)}</p>
      {item.recoveryState && <p>{t("shared.recovery")}{' '}{localizeText(item.recoveryState)}</p>}
    </div>
  <AdvancedDisclosure title={t("shared.executionReportingDetails")}><p>{t("shared.onlyTheReportedCurrentStageIsAvailableStageHistoryIsNotProvided")}</p></AdvancedDisclosure></div></TableCard.Root>;
}
export function CapabilityCard({ capability, commands, children }) {
  useLanguage();
  const { definition, state, availableActions } = capability;
  const matching = commands?.filter(command => command.request.capabilityId === definition.id)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const pending = matching?.filter(command => ['Pending', 'Running'].includes(command.status)) ?? [];
  const latest = matching?.[0];
  return <TableCard.Root className="poc-capability"><TableCard.Header title={definition.displayName} contentTrailing={<FeaturedIcon icon={Terminal} color="gray" theme="modern" size="sm" />} /><div className="p-4">
    <dl className="poc-facts">
      <Fact label={t("shared.installation")}><Status value={state.installation} /> · {state.detectedVersion || t("shared.versionUnknown")}</Fact>
      <Fact label={t("shared.health")}><Status value={state.health} /></Fact>
      <Fact label={t("shared.authentication")}><Status value={state.authentication ?? (definition.requiresAuthentication ? 'Unknown' : t("shared.notApplicable"))} /></Fact>
      <Fact label={t("shared.configuration")}><Status value={state.configuration ?? (definition.requiresConfiguration ? 'Unknown' : t("shared.notApplicable"))} /></Fact>
      <Fact label={t("shared.update")}><Status value={state.update} /></Fact>
      <Fact label={t("shared.detected")}>{timestamp(state.detectedAtUtc)}</Fact>
      <Fact label={t("shared.operation")}><Status value={state.operation?.state} /> {localizeText(state.operation?.action)}</Fact>
    </dl>
    {pending.map(command => <p key={command.id}><Status value={command.status} /> {localizeText(command.request.action)}</p>)}
    {!pending.length && latest && <p>{t("shared.latestCommand")}{' '}<Status value={latest.status} /> {localizeText(latest.request.action)}</p>}
    {commands === null && <p className="poc-muted text-sm text-tertiary">{t("shared.commandHistoryUnavailable")}</p>}
    {children}
    <AdvancedDisclosure title={t("shared.diagnosticContext")}><p>{t("shared.availableTypedActions")}{' '}{availableActions?.length ? availableActions.map(statusLabel).join(', ') : t("shared.noneReported")}</p><p>{localizeText(state.diagnosticCode || t("shared.noDiagnosticCodeReported"))}{state.operation?.diagnosticCode && ' · ' + state.operation.diagnosticCode}</p>{latest && <p>{t("shared.command")}{' '}{latest.id} · {localizeText(latest.diagnostic)}</p>}</AdvancedDisclosure>
  </div></TableCard.Root>;
}
export { CapabilityCard as Capability };

function ControlRail({ administration }) {
  useLanguage();
  const worker = administration?.worker;
  const actions = administration?.actions ?? [
    { key: 'Enabled', label: t("shared.activateScheduling") }, { key: 'Draining', label: t("shared.drainWorker") },
    { key: 'Disabled', label: t("shared.deactivate") }, { key: 'revoke-api', label: t("shared.revokeWorkerAPIToken") }
  ].map(action => ({ ...action, reason: t("shared.currentAdministrationUnavailable") }));
  return <aside className="poc-control-rail" aria-label={t("shared.workerControls")}><TableCard.Root>
    <TableCard.Header title={t("shared.workerControls")} /><div className="p-4">
    <dl className="poc-facts">
      <Fact label={t("shared.schedulingPolicy")}><Status value={worker?.schedulingPolicy ?? t("shared.unavailable")} />{worker?.schedulingPolicy === 'Draining' && <p>{worker.activeAssignments === 0 ? t("shared.drainedSchedulingIsPaused") : t('shared.drainingAssignments', { count: worker.activeAssignments })}</p>}</Fact>
      <Fact label={t("shared.credentialDeliveryAuthorization")}><Status value={administration?.delivery?.status ?? t("shared.unavailable")} />{administration?.delivery?.revokedAtUtc && <p>{t("shared.revoked")}{' '}{timestamp(administration.delivery.revokedAtUtc)}</p>}</Fact>
      <Fact label={t("shared.workerAPIToken")}><Status value={worker?.authenticationCredentialStatus ?? t("shared.unavailable")} />{worker?.authenticationCredentialRevokedAtUtc && <p>{t("shared.revoked")}{' '}{timestamp(worker.authenticationCredentialRevokedAtUtc)}</p>}</Fact>
    </dl>
    <details className="my-4 text-xs text-tertiary"><summary className="font-medium">{t("shared.controlEffects")}</summary><p id="poc-policy-effect" className="mt-2">{t("shared.schedulingControlsAffectNewAssignmentsExistingAssignmentsAndLeasesAreNotCancelled")}</p>
    <p id="poc-delivery-effect">{t("shared.revokingDeliveryAuthorizationStopsFutureServerCredentialDeliveryWorkerAPIAuthenticationAnd")}</p>
    <p id="poc-token-effect">{t("shared.revokingWorkerAPIAuthenticationDeniesCallsUsingItsTokenActiveLeasesMay")}</p></details>
    <div className="poc-controls">{actions.map(action => <div key={action.key}>
      <Button color="secondary" size="sm" className="w-full whitespace-normal text-center" isDisabled={!!action.reason || !administration?.onAction} aria-describedby={`${action.key === 'revoke-delivery' ? 'poc-delivery-effect' : action.key === 'revoke-api' ? 'poc-token-effect' : 'poc-policy-effect'} poc-action-${action.key}`} onPress={() => administration?.onAction(action.key)}>{localizeText(action.label)}</Button>
      <AdvancedDisclosure title={t("shared.actionDetails")}><p id={'poc-action-' + action.key}>{localizeText(action.reason || t("shared.confirmationRequired"))}</p></AdvancedDisclosure>
    </div>)}</div>
    <p role="status" aria-live="polite">{administration?.message && localizeText(administration.message)}</p>
    <Button color="secondary" size="sm" className="w-full whitespace-normal text-center" isDisabled={administration?.pending || !administration?.onRefresh} onPress={() => administration?.onRefresh()}>{t("shared.refreshAuthoritativeState")}</Button></div></TableCard.Root>
  </aside>;
}
export function WorkerDetail({ id, observations, loading, diagnostics = null, nodes = null, nodeCommands = null, executions = null, projects = null, administration = null, readOnly = false, administrationHref = null, workersHref = '/workers', now = Date.now(), preparation = null, provisioning = null }) {
  useLanguage();
  const worker = observations?.find(item => item.workerId === id);
  const node = nodes?.find(item => item.id === id && item.kind === 'worker');
  const items = workerExecutions(executions, id);
  const active = items.filter(item => ['Assigned', 'Running'].includes(item.state));
  const recent = items.filter(item => terminalStates.includes(item.state)).slice(0, 10);
  const commands = nodeCommands?.filter(command => command.request.nodeId === id) ?? null;
  const recentRows = recent.map(item => {
    const project = projects?.find(project => project.id === item.projectId);
    const url = issueLink(item.workReference, project?.repository);
    const identity = item.workReference?.type === 'github-issue' ? `${t('home.issue')}${item.workReference.id}` : item.workReference?.id ?? t("shared.work");
    return {
      id: item.id,
      reference: <><p className="font-medium text-primary"><a href={'/projects/' + encodeURIComponent(item.projectId)}>{project?.name ?? item.projectId}</a></p>{url ? <ExternalLink className="text-brand-secondary font-semibold" href={url}>{identity}</ExternalLink> : identity}<p><a href={'/executions/' + encodeURIComponent(item.id)}>{t("shared.executionDetails")}</a></p></>,
      outcome: <><Status value={item.state} />{item.recoveryState && <p>{t("shared.recovery")}{' '}{localizeText(item.recoveryState)}</p>}</>,
      timing: <><p>{t("shared.duration")}{' '}{duration(item, now)}</p><p>{item.completedAtUtc ? t('executions.completed') : t('shared.requested')}: {timestamp(item.completedAtUtc ?? item.createdAtUtc)}</p></>
    };
  });
  return <section className="poc-card text-sm text-secondary" aria-label={t("shared.workerDetail")}>
    <PageHeading title={worker?.displayName || t("shared.workerDetails")} resourceId={id}
      breadcrumbs={[{ label: t("shared.workers"), href: workersHref }, { label: t("shared.workerDetail") }]}
      actions={readOnly && <Button href={administrationHref ?? '/workers/' + encodeURIComponent(id)} color="secondary" size="sm">{t("shared.openWorkerDetailAndAdministration")}</Button>} />
    {!worker ? <p role="status">{loading ? t("shared.loadingCurrentWorkerObservations") : observations ? t("shared.workerUnavailableOrDeletedReturnToWorkersToRefreshTheInventory") : t("shared.currentWorkerObservationsUnavailableSignInOrRefreshToRecoverCurrentState")}</p> : <div className={readOnly ? "poc-layout poc-read-only" : "poc-layout"}><div className="poc-main">
      <TableCard.Root><div className="p-5"><dl className="poc-status-grid">
        <Fact label={t("shared.connection")}><Status value={worker.availability} />{node && <p>{t("shared.node")}{' '}{localizeText(node.connectivity)}</p>}</Fact>
        <Fact label={t("shared.executionReadiness")}><Status value={worker.lifecycleState} /><p>{t("shared.executionPrerequisites")}{' '}<Status value={node?.executionReadiness ?? t("shared.unavailable")} /></p><p>{t("shared.scheduling")}{' '}{localizeText(worker.schedulingPolicy ?? t("shared.unknown"))}</p></Fact>
        <Fact label={t("shared.observationFreshness")}><Status value={node ? node.observationsStale ? 'Stale' : 'Current' : 'Unknown'} /><p>{t("shared.lastHeartbeat")}{' '}{timestamp(worker.lastHeartbeatAtUtc)}</p></Fact>
        <Fact label={t("shared.workerSlots")}><strong>{worker.activeExecutions ?? t("shared.unknown")} / {worker.maximumCapacity ?? worker.capacity ?? t("shared.unknown")}{' '}{t("shared.active")}</strong><p>{worker.availableCapacity ?? t("shared.unknown")}{' '}{t("shared.available")}{' '}{worker.activeAssignments ?? t("shared.unknown")}{' '}{t("shared.activeServerAssignments")}</p></Fact>
      </dl>
      <AdvancedDisclosure title={t("shared.observationDetails")}><p>{t("shared.connectivityExecutionPrerequisitesAndSchedulingPolicyAreSeparateObservationsStaleEvidenceDoes")}</p></AdvancedDisclosure></div></TableCard.Root>
      <section className="poc-section"><div className="mb-3 flex items-center gap-3"><FeaturedIcon icon={Activity} color="brand" theme="light" size="sm" /><h2 className="text-lg font-semibold text-primary">{t(active.length > 1 ? 'shared.currentExecutions' : 'shared.currentExecution')}</h2></div>
        {executions === null ? <p>{t("shared.currentExecutionDataUnavailableRefreshToRecoverReportedStages")}</p> : active.length ? active.map(item => <Execution key={item.id} item={item} projects={projects} now={now} active />) : <p>{t("shared.noCurrentExecutionInTheLatest50ServerRequests")}{' '}{worker.activeAssignments > 0 || worker.activeExecutions > 0 ? t("shared.theWorkerReportsActiveWorkItsExecutionDetailIsUnavailableInThis") : t("shared.noActiveWorkReportedByThisWorker")}</p>}
      </section>
      <TableCard.Root size="sm"><TableCard.Header title={t("shared.recentExecutions")} description={t("shared.upTo10OutcomesForThisWorkerFromTheLatest50Server")} />
        {executions === null ? <p className="p-5">{t("shared.executionHistoryUnavailableRefreshToTryAgain")}</p> : recent.length ? <>
          <div className="hidden sm:block"><Table aria-label={t("shared.recentExecutions")} size="sm"><Table.Header>
            <Table.Head id="work" isRowHeader label={t("shared.projectIssue")} /><Table.Head id="outcome" label={t("shared.outcome")} /><Table.Head id="time" label={t("shared.time")} />
          </Table.Header><Table.Body>{recentRows.map(row => <Table.Row key={row.id} id={row.id}>
            <Table.Cell>{row.reference}</Table.Cell><Table.Cell>{row.outcome}</Table.Cell><Table.Cell>{row.timing}</Table.Cell>
          </Table.Row>)}</Table.Body></Table></div>
          <div className="divide-y divide-secondary sm:hidden">{recentRows.map(row => <article key={row.id} className="p-4">
            <div className="flex items-start justify-between gap-3"><div>{row.reference}</div><div>{row.outcome}</div></div>
            <div className="mt-3 text-sm text-tertiary">{row.timing}</div>
          </article>)}</div>
        </> : <p className="p-5">{t("shared.noRecentTerminalExecutionsForThisWorkerInThisView")}</p>}
      </TableCard.Root>
      {preparation}
      <section className="poc-section"><h2 className="mb-3 text-lg font-semibold text-primary">{t("shared.capabilitiesAndProvisioning")}</h2><p>{t("shared.provisioningReadiness")}{' '}<Status value={node?.provisioningReadiness ?? t("shared.unavailable")} />{' '}{t("shared.latestOperation")}{' '}<Status value={diagnostics?.provisioningState ?? t("shared.unavailable")} /></p>
        {diagnostics ? <AdvancedDisclosure title={t("shared.readinessEvidence")}><p>{t("shared.reportedReadinessEvidenceCodexPreflight")}{' '}{diagnostics.aiAgentReady ? localizeText('present') : localizeText('absent')}{' '}{t("shared.gitHubAccess")}{' '}{diagnostics.gitHubReady ? localizeText('present') : localizeText('absent')}{' '}{t("shared.gitAccess")}{' '}{diagnostics.gitReady ? localizeText('present') : localizeText('absent')}</p><p>{t("shared.configurationSynchronization")}{' '}<Status value={diagnostics.configurationSynchronization} /></p>{diagnostics.latestProvisioningOperation && <p>{localizeText(diagnostics.latestProvisioningOperation.action)} · {localizeText(diagnostics.latestProvisioningOperation.status)}</p>}</AdvancedDisclosure> : <p>{t("shared.workerReadinessDiagnosticsUnavailable")}</p>}
        {provisioning ?? (node ? node.capabilities?.length ? <div className="poc-capabilities mt-4">{node.capabilities.map(capability => <CapabilityCard key={capability.definition.id} capability={capability} commands={commands} />)}</div> : <p>{t("shared.noCapabilitiesReported")}</p> : <p>{t("shared.capabilityObservationsUnavailableRefreshProvisioningState")}</p>)}
      </section>
    </div>{!readOnly && <ControlRail administration={administration} />}</div>}
  </section>;
}
