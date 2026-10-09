import { WorkerResources } from './features/workers/WorkerResources';
import { useState } from 'react';
import { t, useLanguage, localizeText, statusLabel } from './shared/i18n';
import { ExternalLink } from './shared/Actions';
import { GuidDisplay } from './shared/GuidDisplay';
import { StatusBadge, AdvancedDisclosure } from './shared/Presentation';
import { Button } from './untitled/components/base/buttons/button';
import { TableCard } from './untitled/components/application/table/table';
import { Activity, Database01, PauseCircle, Play, Shield01, Stop, Trash01 } from '@untitledui/icons';
import { duration, issueLink, statusColor, terminalStates, timestamp, workerExecutions } from './model.js';

const executionStages = [
  { id: 'claimed', label: 'Claimed' },
  { id: 'preparing', label: 'Preparing' },
  { id: 'codex', label: 'Codex' },
  { id: 'validation', label: 'Validating' },
  { id: 'integration', label: 'Integrating' }
];

function statusTone(value) {
  if ([t('workers.fresh'), t('workers.ready'), t('workers.available'), t('workers.connected')].includes(value)) return 'success';
  if (value === t('workers.notReady')) return 'warning';
  return statusColor(value);
}

function Status({ value, tone }) {
  useLanguage();
  return <StatusBadge compact tone={tone ?? statusTone(value)} withDot>{value ?? t('shared.unknown')}</StatusBadge>;
}

function stageKey(value) {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (normalized === 'claim' || normalized === 'claimed') return 'claimed';
  if (normalized === 'preparing' || normalized === 'preparation') return 'preparing';
  if (normalized === 'codex') return 'codex';
  if (normalized === 'validation' || normalized === 'validating') return 'validation';
  if (normalized === 'integration' || normalized === 'integrating') return 'integration';
  return null;
}

function stageLabel(stage) {
  const key = stageKey(stage);
  return key ? localizeText(executionStages.find(item => item.id === key).label) : localizeText(stage);
}

function relativeAge(value, now) {
  const date = Date.parse(value ?? '');
  if (!Number.isFinite(date)) return t('workers.heartbeatUnavailable');
  const seconds = Math.max(0, Math.floor((now - date) / 1000));
  if (seconds < 60) return t('workers.secondsAgo', { count: seconds });
  if (seconds < 3600) return t('workers.minutesAgo', { count: Math.floor(seconds / 60) });
  return t('workers.hoursAgo', { count: Math.floor(seconds / 3600) });
}

function workerConnection(worker, node) {
  const connectivity = node?.connectivity?.toLowerCase();
  if (connectivity === 'connected') return t('workers.connected');
  if (connectivity === 'disconnected') return statusLabel('disconnected');
  const availability = worker.availability.toLowerCase();
  if (availability === 'online' || availability === 'draining') return t('workers.connected');
  return statusLabel(availability);
}

function heartbeatFreshness(worker) {
  if (!Number.isFinite(Date.parse(worker.lastHeartbeatAtUtc ?? ''))) return { value: t('shared.unknown'), tone: 'gray' };
  const availability = worker.availability.toLowerCase();
  if (availability === 'stale') return { value: statusLabel('stale'), tone: 'warning' };
  if (['online', 'draining', 'offline'].includes(availability)) return { value: t('workers.fresh'), tone: 'success' };
  return { value: t('shared.unknown'), tone: 'gray' };
}

function CopyWorkerId({ id }) {
  useLanguage();
  const [message, setMessage] = useState('');
  async function copy() {
    try {
      await navigator.clipboard.writeText(id);
      setMessage(t('workers.copied'));
    } catch {
      setMessage(t('workers.copyIdUnavailable'));
    }
  }
  return <>
    <button type="button" className="poc-copy-id" aria-label={t('workers.copyWorkerId')} title={t('workers.copyWorkerId')} onClick={() => { void copy(); }}>
      <svg aria-hidden="true" viewBox="0 0 20 20" fill="none"><rect x="7" y="6" width="9" height="11" rx="1.5"/><path d="M12 6V4.5A1.5 1.5 0 0 0 10.5 3h-6A1.5 1.5 0 0 0 3 4.5v8A1.5 1.5 0 0 0 4.5 14H7"/></svg>
    </button>
    <span className="sr-only" role="status" aria-live="polite">{message}</span>
  </>;
}

function WorkerHeading({ worker, node, now, readOnly, administrationHref, workersHref }) {
  useLanguage();
  const connection = workerConnection(worker, node);
  const readinessValue = !node ? t('shared.unknown') : node.observationsStale ? 'Stale' : node.executionReadiness;
  const readiness = localizeText(readinessValue);
  const freshness = heartbeatFreshness(worker);
  const slots = worker.activeExecutions == null || (worker.maximumCapacity ?? worker.capacity) == null
    ? t('shared.unknown')
    : `${worker.activeExecutions} / ${worker.maximumCapacity ?? worker.capacity}`;

  return <header className="poc-worker-heading">
    <div className="poc-worker-identity">
      <nav aria-label={t('shared.breadcrumb')} className="poc-worker-breadcrumb">
        <a href={workersHref}>{t('shared.workers')}</a><span aria-hidden="true">/</span><span aria-current="page">{worker.displayName || t('workers.worker')}</span>
      </nav>
      <h1 tabIndex={-1}>{worker.displayName || t('shared.workerDetails')}</h1>
      <div className="poc-worker-meta">
        <span className="poc-worker-id"><GuidDisplay value={worker.workerId} /></span><CopyWorkerId id={worker.workerId} />{worker.workerVersion && <span>{t('workers.workerVersion')} {worker.workerVersion}</span>}
        <div className="poc-worker-statuses" aria-label={t('workers.workerStatus')}>
          <Status value={connection} tone={connection === t('workers.connected') ? 'success' : statusTone(node?.connectivity ?? worker.availability)} />
          <Status value={readiness} tone={statusTone(readinessValue)} />
          <Status value={freshness.value} tone={freshness.tone} />
        </div>
        {readOnly && <a className="poc-readonly-link" href={administrationHref ?? `/workers/${encodeURIComponent(worker.workerId)}`}>{t('shared.openWorkerDetailAndAdministration')}</a>}
      </div>
    </div>
    <div className="poc-worker-metrics" aria-label={t('workers.workerSummary')}>
      <div className="poc-summary-metric">
        <Database01 aria-hidden="true" />
        <div><span>{t('workers.workSlots')}</span><strong>{slots}</strong></div>
      </div>
      <div className="poc-summary-metric">
        <Activity aria-hidden="true" />
        <div><span>{t('workers.lastHeartbeat')}</span><strong>{relativeAge(worker.lastHeartbeatAtUtc, now)}</strong></div>
      </div>
    </div>
  </header>;
}

function PanelHeading({ id, title, trailing }) {
  useLanguage();
  return <div className="poc-panel-heading">
    <h2 id={id}>{title}</h2>
    {trailing}
  </div>;
}

function WorkReference({ item, project }) {
  useLanguage();
  const work = item.workReference;
  if (!work) return <span>{t('executions.workReferenceUnavailable')}</span>;
  const url = issueLink(work, project?.repository);
  const identity = work.type === 'github-issue' ? `${t('workers.issue')} #${work.id}` : `${localizeText(work.type)} ${work.id}`;
  return url ? <ExternalLink href={url}>{identity}</ExternalLink> : <span>{identity}{work.type === 'github-issue' && <span className="block text-xs text-tertiary">{t('shared.issueLinkUnavailable')}</span>}</span>;
}

function ExecutionTimeline({ item }) {
  useLanguage();
  const current = stageKey(item.currentStage);
  return <div className="poc-execution-timeline">
    <ol className="poc-stage-list" aria-label={t('workers.executionStages')}>
      {executionStages.map(stage => {
        const active = current === stage.id;
        return <li key={stage.id} className={active ? 'is-current' : ''} aria-current={active ? 'step' : undefined}>
          <span className="poc-stage-marker" aria-hidden="true">{active ? <span /> : null}</span>
          <span className="poc-stage-name">{localizeText(stage.label)}</span>
          <span className="poc-stage-detail">{active ? t('workers.currentStageReported') : t('workers.notReported')}</span>
        </li>;
      })}
    </ol>
    <p className="poc-stage-history-note">{t('shared.onlyTheReportedCurrentStageIsAvailableStageHistoryIsNotProvided')}</p>
  </div>;
}

function CurrentExecution({ item, projects, now, showStatus }) {
  useLanguage();
  const project = projects?.find(value => value.id === item.projectId);
  const stage = item.currentStage ? stageLabel(item.currentStage) : localizeText(item.state);
  return <article className="poc-current-execution">
    {showStatus && <div className="poc-current-execution-heading"><span>{t('workers.execution')}</span><Status value={stage} tone={item.currentStage ? 'warning' : statusTone(item.state)} /></div>}
    <dl className="poc-execution-fields">
      <div>
        <dt>{t('executions.project')}</dt>
        <dd><a href={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</a></dd>
      </div>
      <div>
        <dt>{t('workers.gitHubIssue')}</dt>
        <dd><WorkReference item={item} project={project} /></dd>
      </div>
    </dl>
    <ExecutionTimeline item={item} />
    <p className="poc-current-execution-timing">{t('home.elapsed')} {duration(item, now)} · {item.startedAtUtc ? `${t('executions.started')} ${relativeAge(item.startedAtUtc, now)}` : t('workers.executionStartNotReported')} · <a href={`/executions/${encodeURIComponent(item.id)}`}>{t('shared.executionDetails')}</a></p>
  </article>;
}

function CurrentExecutions({ items, projects, now, worker, unavailable }) {
  useLanguage();
  const title = items.length > 1 ? t('shared.currentExecutions') : t('shared.currentExecution');
  const currentStage = items.length === 1 ? items[0].currentStage ? stageLabel(items[0].currentStage) : localizeText(items[0].state) : null;
  const trailing = currentStage && <Status value={currentStage} tone={items[0].currentStage ? 'warning' : statusTone(items[0].state)} />;
  return <TableCard.Root className="poc-dashboard-panel poc-current-panel">
    <PanelHeading id="poc-current-heading" title={title} trailing={trailing} />
    {unavailable ? <p className="poc-empty-panel">{t('shared.currentExecutionDataUnavailableRefreshToRecoverReportedStages')}</p>
      : items.length ? <div className="poc-current-executions-list">{items.map(item => <CurrentExecution key={item.id} item={item} projects={projects} now={now} showStatus={items.length > 1} />)}</div>
      : <p className="poc-empty-panel">{worker.activeAssignments > 0 || worker.activeExecutions > 0 ? t('shared.theWorkerReportsActiveWorkItsExecutionDetailIsUnavailableInThis') : t('shared.noCurrentExecutionInTheLatest50ServerRequests')}</p>}
  </TableCard.Root>;
}

function RecentExecutions({ items, projects, now, loading }) {
  useLanguage();
  const rows = items.slice(0, 3);
  const viewAll = <a className="poc-view-all" href="/executions">{t('workers.viewAllExecutions')}<span aria-hidden="true">→</span></a>;
  return <TableCard.Root className="poc-dashboard-panel poc-recent-panel">
    <PanelHeading id="poc-recent-heading" title={t('shared.recentExecutions')} trailing={viewAll} />
    {loading ? <p className="poc-empty-panel">{t('shared.executionHistoryUnavailableRefreshToTryAgain')}</p>
      : rows.length ? <div className="poc-recent-table-wrap">
        <table className="poc-recent-table" aria-labelledby="poc-recent-heading">
          <colgroup><col className="poc-col-project"/><col className="poc-col-issue"/><col className="poc-col-status"/><col className="poc-col-started"/><col className="poc-col-duration"/></colgroup>
          <thead><tr>
            <th scope="col">{t('executions.project')}</th><th scope="col">{t('workers.gitHubIssue')}</th><th scope="col">{t('workers.status')}</th><th scope="col">{t('workers.startedAt')}</th><th scope="col">{t('shared.durationLabel')}</th>
          </tr></thead>
          <tbody>{rows.map(item => {
            const project = projects?.find(value => value.id === item.projectId);
            return <tr key={item.id}>
              <td><a href={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</a></td>
              <td><WorkReference item={item} project={project} /><a className="block break-all text-xs text-tertiary" href={`/executions/${encodeURIComponent(item.id)}`}><GuidDisplay value={item.id} /></a></td>
              <td><span className="poc-table-status"><Status value={item.state} /></span></td>
              <td>{item.startedAtUtc ? timestamp(item.startedAtUtc) : t('workers.notReported')}</td>
              <td>{duration(item, now)}</td>
            </tr>;
          })}</tbody>
        </table>
      </div> : <p className="poc-empty-panel">{t('shared.noRecentTerminalExecutionsForThisWorkerInThisView')}</p>}
  </TableCard.Root>;
}

function ServiceMark({ type }) {
  if (type === 'git') return <svg aria-hidden="true" viewBox="0 0 48 48" className="poc-service-mark"><path d="M18.2 3.9 3.9 18.2a5.3 5.3 0 0 0 0 7.5l18.4 18.4a5.3 5.3 0 0 0 7.5 0l14.3-14.3a5.3 5.3 0 0 0 0-7.5L25.7 3.9a5.3 5.3 0 0 0-7.5 0Z" fill="#f04438"/><path d="m15.4 17.4 15.2 15.2M23 13.5v7.2m7.7 4.5v6.1" fill="none" stroke="#fff" strokeWidth="3" strokeLinecap="round"/><circle cx="23" cy="13" r="3.2" fill="#fff"/><circle cx="30.8" cy="24.8" r="3.2" fill="#fff"/><circle cx="30.8" cy="32.5" r="3.2" fill="#fff"/></svg>;
  if (type === 'github-cli') return <svg aria-hidden="true" viewBox="0 0 48 48" className="poc-service-mark poc-service-mark-github"><circle cx="24" cy="24" r="22" fill="#0b1020"/><path fill="#f5f5f6" d="M24 10.1c-7.7 0-14 6.3-14 14 0 6.2 4 11.4 9.6 13.2.7.1 1-.3 1-.7v-2.6c-3.9.9-4.7-1.6-4.7-1.6-.6-1.6-1.6-2-1.6-2-1.3-.9.1-.9.1-.9 1.4.1 2.1 1.5 2.1 1.5 1.2 2.1 3.2 1.5 4 .1.1-.9.5-1.5.9-1.9-3.1-.4-6.4-1.6-6.4-6.9 0-1.5.5-2.7 1.5-3.7-.2-.4-.7-1.8.1-3.7 0 0 1.2-.4 3.9 1.5a13.4 13.4 0 0 1 7.1 0c2.7-1.9 3.9-1.5 3.9-1.5.8 1.9.3 3.3.1 3.7 1 1 1.5 2.2 1.5 3.7 0 5.3-3.3 6.5-6.4 6.9.5.4 1 1.3 1 2.7v4c0 .4.3.8 1 .7 5.6-1.8 9.6-7 9.6-13.2 0-7.7-6.3-14-14-14Z"/></svg>;
  if (type === 'codex-cli') return <svg aria-hidden="true" viewBox="0 0 48 48" className="poc-service-mark poc-service-mark-terminal"><rect x="2" y="2" width="44" height="44" rx="10" fill="#0b1020" stroke="#303748"/><path d="m14 16 8 8-8 8m12 0h9" fill="none" stroke="#d0d5dd" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"/></svg>;
  return <svg aria-hidden="true" viewBox="0 0 48 48" className="poc-service-mark poc-service-mark-dotnet"><rect x="2" y="2" width="44" height="44" rx="10" fill="#53389e"/><text x="24" y="29" textAnchor="middle" fill="#fff" fontSize="11" fontWeight="700">.NET</text></svg>;
}

const serviceTiles = [
  { id: 'git', type: 'git', name: 'Git', detail: 'version' },
  { id: 'github-cli', type: 'github-cli', name: 'GitHub CLI', detail: 'authentication' },
  { id: 'codex-cli', type: 'codex-cli', name: 'Codex CLI', detail: 'version-authentication' },
  { id: 'dotnet-sdk', type: 'dotnet-sdk', name: '.NET SDK', detail: 'sdk' }
];

function capabilityStatus(capability, stale, id) {
  if (!capability) return t('shared.unknown');
  if (stale) return 'Stale';
  const { definition, state } = capability;
  const installed = state.installation?.toLowerCase() === 'installed';
  const healthy = state.health?.toLowerCase() === 'healthy';
  const authenticated = !definition.requiresAuthentication || state.authentication?.toLowerCase() === 'satisfied';
  const configured = !definition.requiresConfiguration || state.configuration?.toLowerCase() === 'satisfied';
  if (installed && healthy && authenticated && configured) return id === 'dotnet-sdk' ? t('workers.available') : t('workers.ready');
  return state.health || state.installation || t('shared.unknown');
}

function capabilityDetail(tile, capability) {
  if (!capability) return t('workers.notReported');
  const { state } = capability;
  if (tile.detail === 'authentication') {
    if (state.authentication?.toLowerCase() === 'satisfied') return t('workers.authenticated');
    return localizeText(state.authentication ?? t('workers.notReported'));
  }
  if (tile.detail === 'version-authentication') return state.detectedVersion ? `v${state.detectedVersion}` : t('shared.versionUnknown');
  if (tile.detail === 'sdk') return state.detectedVersion ?? t('shared.versionUnknown');
  return state.detectedVersion ? `v${state.detectedVersion}` : t('shared.versionUnknown');
}

function capabilitySupportingText(tile, capability) {
  if (!capability) return t('workers.capabilityObservationsUnavailable');
  if (tile.detail === 'version') return capability.state.detectedVersion ? t('workers.versionDetected') : t('workers.versionNotReported');
  if (tile.detail === 'sdk') return t('workers.runtimeVersion');
  if (['authentication', 'version-authentication'].includes(tile.detail)) {
    if (capability.state.authentication?.toLowerCase() === 'satisfied') {
      return t(tile.detail === 'authentication' ? 'workers.tokenAvailable' : 'workers.authenticated');
    }
    return t('workers.authenticationStatus');
  }
  return t('workers.runtimeVersion');
}

function CapabilityTile({ tile, capability, stale }) {
  useLanguage();
  const name = localizeText(tile.name);
  const status = capabilityStatus(capability, stale, tile.id);
  const tone = status === t('workers.ready') || status === t('workers.available') ? 'success' : statusTone(status);
  return <article className="poc-service-tile" aria-label={`${name}: ${localizeText(status)}`}>
    <div className="poc-service-top">
      <ServiceMark type={tile.type} />
      <div className="poc-service-heading"><h3>{name}</h3><Status value={status} tone={tone} /></div>
    </div>
    <p className="poc-service-detail">{capabilityDetail(tile, capability)}</p>
    <p className="poc-service-support">{capabilitySupportingText(tile, capability)}</p>
  </article>;
}

function CapabilitiesPanel({ node }) {
  useLanguage();
  const capabilityById = new Map((node?.capabilities ?? []).map(capability => [capability.definition.id, capability]));
  return <TableCard.Root className="poc-dashboard-panel poc-capabilities-panel">
    <PanelHeading id="poc-capabilities-heading" title={t('shared.capabilitiesAndProvisioning')} />
    {node ? <div className="poc-service-grid">{serviceTiles.map(tile => <CapabilityTile key={tile.id} tile={tile} capability={capabilityById.get(tile.id)} stale={node.observationsStale} />)}</div>
      : <p className="poc-empty-panel">{t('shared.capabilityObservationsUnavailableRefreshProvisioningState')}</p>}
  </TableCard.Root>;
}

function ReadinessRow({ icon, title, detail, value, tone }) {
  return <div className="poc-readiness-row">
    <span className="poc-readiness-icon" aria-hidden="true">{icon}</span>
    <div className="poc-readiness-copy"><h3>{title}</h3><p>{detail}</p></div>
    <Status value={value} tone={tone} />
  </div>;
}

function ReadinessPanel({ worker, node, diagnostics, now }) {
  useLanguage();
  const readinessStale = !!node?.observationsStale || diagnostics?.capabilityObservationsCurrent === false;
  const preflight = !diagnostics ? t('shared.unknown') : readinessStale ? 'Stale' : diagnostics.aiAgentReady ? t('workers.ready') : t('workers.notReady');
  const eligibleCount = diagnostics?.projects?.filter(project => project.isEligible).length;
  const eligibilityStatus = eligibleCount == null ? t('shared.unknown') : readinessStale ? 'Stale' : eligibleCount > 0 ? t('workers.ready') : t('workers.notReady');
  const eligible = eligibleCount == null ? t('shared.unknown') : t('workers.eligibleProjects', { count: eligibleCount });
  const heartbeat = worker.lastHeartbeatAtUtc ? relativeAge(worker.lastHeartbeatAtUtc, now) : t('workers.heartbeatUnavailable');
  const freshness = heartbeatFreshness(worker);
  const preflightTone = preflight === t('workers.ready') ? 'success' : statusTone(preflight);
  const projectTone = readinessStale ? 'warning' : eligibleCount > 0 ? 'success' : eligibleCount === 0 ? 'warning' : 'gray';
  return <TableCard.Root className="poc-dashboard-panel poc-readiness-panel">
    <PanelHeading id="poc-readiness-heading" title={t('workers.readinessChecks')} />
    <div className="poc-readiness-list">
      <ReadinessRow icon={<Shield01 />} title={t('workers.executionPreflightTitle')} detail={!diagnostics ? t('workers.notReported') : readinessStale ? t('workers.preflightStale') : diagnostics.aiAgentReady ? t('workers.environmentAndToolsReady') : t('workers.preflightNotReady')} value={preflight} tone={preflightTone} />
      <ReadinessRow icon={<span className="poc-project-readiness-icon"><i/><i/></span>} title={t('workers.projectEligibility')} detail={eligible} value={eligibilityStatus} tone={projectTone} />
      <ReadinessRow icon={<Activity />} title={t('workers.heartbeatFreshness')} detail={`${t('workers.lastHeartbeat')}: ${heartbeat}`} value={freshness.value} tone={freshness.tone} />
    </div>
  </TableCard.Root>;
}

function ControlRail({ administration }) {
  useLanguage();
  const actions = administration?.actions ?? [
    { key: 'Enabled', label: t('shared.activateScheduling') }, { key: 'Draining', label: t('shared.drainWorker') },
    { key: 'Disabled', label: t('shared.deactivate') }, { key: 'revoke-api', label: t('shared.revokeWorkerAPIToken') }
  ].map(action => ({ ...action, reason: t('shared.currentAdministrationUnavailable') }));
  const mainActions = actions.filter(action => ['Enabled', 'Draining', 'Disabled', 'revoke-api'].includes(action.key));
  const appearance = { Enabled: ['primary', Play], Draining: ['secondary', PauseCircle], Disabled: ['secondary-destructive', Stop], 'revoke-api': ['tertiary-destructive', Trash01] };
  function actionButton(action) {
    const [color, iconLeading] = appearance[action.key] ?? ['secondary', Trash01];
    return <div key={action.key} className={`poc-control-action ${action.key === 'revoke-api' ? 'poc-revoke-action' : ''}`}>
      <Button color={color} size="md" className="w-full justify-start" iconLeading={iconLeading} isDisabled={!!action.reason || !administration?.onAction} aria-describedby={`poc-action-${action.key}`} onPress={() => administration?.onAction(action.key)}>
        {action.key === 'revoke-api' ? t('workers.revokeApiToken') : localizeText(action.label)}
      </Button>
      <p className="sr-only" id={`poc-action-${action.key}`}>{localizeText(action.reason || t('shared.confirmationRequired'))}</p>
    </div>;
  }
  return <TableCard.Root className="poc-dashboard-panel poc-control-panel poc-control-rail">
    <PanelHeading id="poc-controls-heading" title={t('shared.workerControls')} />
    <div className="poc-control-content">
      <div className="poc-primary-controls">{mainActions.filter(action => action.key !== 'revoke-api').map(actionButton)}</div>
      <div className="poc-danger-controls">{mainActions.filter(action => action.key === 'revoke-api').map(actionButton)}</div>
      <div className="poc-activation-note">
        <span aria-hidden="true">i</span>
        <div>
          <p>{t('workers.activationPrerequisite')}</p>
          {administration?.message && <p className="poc-control-message" role="status" aria-live="polite">{localizeText(administration.message)}</p>}
          <Button color="tertiary" size="sm" className="poc-refresh-button" isDisabled={administration?.pending || !administration?.onRefresh} onPress={() => administration?.onRefresh()}>{t('shared.refreshAuthoritativeState')}</Button>
        </div>
      </div>
    </div>
  </TableCard.Root>;
}

function DeliveryControl({ administration }) {
  useLanguage();
  const action = administration?.actions?.find(item => item.key === 'revoke-delivery');
  if (!action) return null;
  return <div className="poc-extra-control">
    <Button color="tertiary-destructive" size="md" className="w-full justify-start" iconLeading={Trash01} isDisabled={!!action.reason || !administration?.onAction} aria-describedby="poc-delivery-action-reason" onPress={() => administration?.onAction('revoke-delivery')}>
      {localizeText(action.label)}
    </Button>
    <p id="poc-delivery-action-reason" className="sr-only">{localizeText(action.reason || t('shared.confirmationRequired'))}</p>
  </div>;
}

export function CapabilityCard({ capability, commands, children }) {
  useLanguage();
  const { definition, state, availableActions } = capability;
  const matching = commands?.filter(command => command.request.capabilityId === definition.id)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const pending = matching?.filter(command => ['Pending', 'Running'].includes(command.status)) ?? [];
  const latest = matching?.[0];
  return <TableCard.Root className="poc-capability"><TableCard.Header title={definition.displayName} /><div className="p-4">
    <dl className="poc-facts">
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.installation')}</dt><dd className="text-sm text-secondary"><Status value={state.installation} /> · {state.detectedVersion || t('shared.versionUnknown')}</dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.health')}</dt><dd className="text-sm text-secondary"><Status value={state.health} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.authentication')}</dt><dd className="text-sm text-secondary"><Status value={state.authentication ?? (definition.requiresAuthentication ? 'Unknown' : t('shared.notApplicable'))} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.configuration')}</dt><dd className="text-sm text-secondary"><Status value={state.configuration ?? (definition.requiresConfiguration ? 'Unknown' : t('shared.notApplicable'))} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.update')}</dt><dd className="text-sm text-secondary"><Status value={state.update} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.detected')}</dt><dd className="text-sm text-secondary">{timestamp(state.detectedAtUtc)}</dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.operation')}</dt><dd className="text-sm text-secondary"><Status value={state.operation?.state} /> {localizeText(state.operation?.action)}</dd></div>
    </dl>
    {pending.map(command => <p key={command.id}><Status value={command.status} /> {localizeText(command.request.action)}</p>)}
    {!pending.length && latest && <p>{t('shared.latestCommand')} <Status value={latest.status} /> {localizeText(latest.request.action)}</p>}
    {commands === null && <p className="text-sm text-tertiary">{t('shared.commandHistoryUnavailable')}</p>}
    {children}
    <AdvancedDisclosure title={t('shared.diagnosticContext')}><p>{t('shared.availableTypedActions')} {availableActions?.length ? availableActions.map(statusLabel).join(', ') : t('shared.noneReported')}</p><p>{localizeText(state.diagnosticCode || t('shared.noDiagnosticCodeReported'))}{state.operation?.diagnosticCode && ` · ${state.operation.diagnosticCode}`}</p>{latest && <p>{t('shared.command')} {latest.id} · {localizeText(latest.diagnostic)}</p>}</AdvancedDisclosure>
  </div></TableCard.Root>;
}

export { CapabilityCard as Capability };

export function WorkerDetail({ id, observations, loading, diagnostics = null, nodes = null, nodeCommands = null, executions = null, projects = null, administration = null, readOnly = false, administrationHref = null, workersHref = '/workers', now = Date.now(), preparation = null, provisioning = null }) {
  useLanguage();
  const worker = observations?.find(item => item.workerId === id);
  const node = nodes?.find(item => item.id === id && item.kind === 'worker');
  const items = workerExecutions(executions, id);
  const active = items.filter(item => ['Assigned', 'Running'].includes(item.state));
  const recent = items.filter(item => terminalStates.includes(item.state));
  const commands = nodeCommands?.filter(command => command.request.nodeId === id) ?? null;
  return <section className="poc-worker-detail-screen" aria-label={t('shared.workerDetail')}>
    {!worker ? <div className="poc-worker-state"><p role="status">{loading ? t('shared.loadingCurrentWorkerObservations') : observations ? t('shared.workerUnavailableOrDeletedReturnToWorkersToRefreshTheInventory') : t('shared.currentWorkerObservationsUnavailableSignInOrRefreshToRecoverCurrentState')}</p></div> : <>
      <WorkerHeading worker={worker} node={node} now={now} readOnly={readOnly} administrationHref={administrationHref} workersHref={workersHref} />
      <div className={readOnly ? 'poc-worker-layout poc-worker-read-only' : 'poc-worker-layout'}>
        <div className="poc-worker-main poc-main">
          <CurrentExecutions items={active} projects={projects} now={now} worker={worker} unavailable={executions === null} />
          <RecentExecutions items={recent} projects={projects} now={now} loading={executions === null} />
          <CapabilitiesPanel node={node} />
          <WorkerResources worker={worker} now={now} />
        </div>
        {!readOnly && <aside className="poc-worker-rail poc-secondary-rail" aria-label={t('workers.workerControlsAndReadiness')}>
          <ControlRail administration={administration} />
          <ReadinessPanel worker={worker} node={node} diagnostics={diagnostics} now={now} />
        </aside>}
      </div>
      <div className="poc-worker-extra">
        {diagnostics && <AdvancedDisclosure title={t('shared.readinessEvidence')}>
          <p>{t('shared.reportedReadinessEvidenceCodexPreflight')} {diagnostics.aiAgentReady ? t('shared.yes') : t('shared.no')} · {t('shared.gitHubAccess')} {diagnostics.gitHubReady ? t('shared.yes') : t('shared.no')} · {t('shared.gitAccess')} {diagnostics.gitReady ? t('shared.yes') : t('shared.no')}</p>
          <p>{t('shared.configurationSynchronization')} {localizeText(diagnostics.configurationSynchronization)}</p>
          {diagnostics.latestProvisioningOperation && <p>{localizeText(diagnostics.latestProvisioningOperation.action)} · {localizeText(diagnostics.latestProvisioningOperation.status)}</p>}
        </AdvancedDisclosure>}
        {!readOnly && <>
          <AdvancedDisclosure title={t('workers.currentControlState')}>
            <dl className="poc-control-facts">
              <div><dt>{t('shared.schedulingPolicy')}</dt><dd><Status value={administration?.worker?.schedulingPolicy ?? t('shared.unavailable')} />{administration?.worker?.schedulingPolicy === 'Draining' && <p>{administration.worker.activeAssignments === 0 ? t('shared.drainedSchedulingIsPaused') : t('shared.drainingAssignments', { count: administration.worker.activeAssignments })}</p>}</dd></div>
              <div><dt>{t('shared.credentialDeliveryAuthorization')}</dt><dd><Status value={administration?.delivery?.status ?? t('shared.unavailable')} />{administration?.delivery?.revokedAtUtc && <p>{t('shared.revoked')} {timestamp(administration.delivery.revokedAtUtc)}</p>}</dd></div>
              <div><dt>{t('shared.workerAPIToken')}</dt><dd><Status value={administration?.worker?.authenticationCredentialStatus ?? t('shared.unavailable')} />{administration?.worker?.authenticationCredentialRevokedAtUtc && <p>{t('shared.revoked')} {timestamp(administration.worker.authenticationCredentialRevokedAtUtc)}</p>}</dd></div>
            </dl>
            <p id="poc-policy-effect">{t('shared.schedulingControlsAffectNewAssignmentsExistingAssignmentsAndLeasesAreNotCancelled')}</p>
            <p id="poc-delivery-effect">{t('shared.revokingDeliveryAuthorizationStopsFutureServerCredentialDeliveryWorkerAPIAuthenticationAnd')}</p>
            <p id="poc-token-effect">{t('shared.revokingWorkerAPIAuthenticationDeniesCallsUsingItsTokenActiveLeasesMay')}</p>
            {administration?.actions?.map(action => <p key={action.key}>{localizeText(action.label)} · {localizeText(action.reason || t('shared.confirmationRequired'))}</p>)}
            <DeliveryControl administration={administration} />
          </AdvancedDisclosure>
        </>}
        <AdvancedDisclosure title={t('shared.observationDetails')}>
          <p>{t('shared.connectivityExecutionPrerequisitesAndSchedulingPolicyAreSeparateObservationsStaleEvidenceDoes')}</p>
          {node && <p>{t('shared.executionPrerequisites')} {localizeText(node.executionReadiness)} · {t('shared.node')} {localizeText(node.connectivity)}</p>}
          {node?.provisioningReadiness && <p>{t('shared.provisioningReadiness')} {localizeText(node.provisioningReadiness)}</p>}
        </AdvancedDisclosure>
        {preparation}
        {node?.capabilities?.length ? <AdvancedDisclosure title={t('shared.capabilityDetails')}>{node.capabilities.map(capability => <CapabilityCard key={capability.definition.id} capability={capability} commands={commands} />)}</AdvancedDisclosure> : null}
        {commands === null ? <AdvancedDisclosure title={t('shared.operationHistory')}><p>{t('shared.commandHistoryUnavailable')}</p></AdvancedDisclosure> : <AdvancedDisclosure title={t('shared.operationHistory')}>
          {commands.length ? commands.slice(0, 8).map(command => <p key={command.id}>{localizeText(node?.capabilities?.find(item => item.definition.id === command.request.capabilityId)?.definition.displayName ?? command.request.capabilityId)} · {localizeText(command.request.action)} · {localizeText(command.status)}</p>) : <p>{t('nodes.noCommandsReportedInBoundedNodeHistory')}</p>}
        </AdvancedDisclosure>}
        {provisioning && <AdvancedDisclosure title={t('nodes.nodeProvisioning')}>{provisioning}</AdvancedDisclosure>}
      </div>
    </>}
  </section>;
}
