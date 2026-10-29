import { WorkerResources } from './features/workers/WorkerResources';
import { useEffect, useState } from 'react';
import { t, useLanguage, localizeText, statusLabel } from './shared/i18n';
import { Link } from 'react-router-dom';
import { GuidDisplay } from './shared/GuidDisplay';
import { StatusBadge, AdvancedDisclosure } from './shared/Presentation';
import { Button } from './untitled/components/base/buttons/button';
import { TableCard } from './untitled/components/application/table/table';
import { Activity, ChevronDown, PauseCircle, Play, Shield01 } from '@untitledui/icons';
import { duration, issueLink, statusColor, isActiveExecutionState, terminalStates, timestamp, workerExecutions, currentWorkerExecutionReadiness, workerDetailStatuses, workerHeartbeatAge, workerManagedSettingsState, workerSlotSummary } from './model.js';
import { executionStages, executionTimeline } from './features/workers/execution-timeline.mjs';
import { IssueTitle } from './features/projects/IssueTitle';
import { Dropdown } from './untitled/components/base/dropdown/dropdown';
const managedFailureStages = new Set(['retrieval', 'contract-validation', 'synchronization']);
const managedFailureCodes = new Set(['server-unavailable', 'server-unauthorized', 'managed-contract-invalid',
  'managed-local-configuration-invalid', 'managed-synchronization-failed', 'project-capabilities-missing', 'project-preparation-failed']);

function statusTone(value) {
  if ([t('workers.fresh'), t('workers.ready'), t('workers.available'), t('workers.connected')].includes(value)) return 'success';
  if (value === t('workers.notReady')) return 'warning';
  return statusColor(value);
}

function freshnessLabel(state) {
  return state === 'Fresh' ? t('workers.fresh')
    : state === 'Stale' ? t('workers.staleHeartbeat') : t('shared.unknown');
}

function configurationFailureDiagnostic(diagnostics) {
  const managed = diagnostics?.workerReportedManagedDiagnostics;
  const details = [
    managedFailureStages.has(managed?.failureStage) ? t(`status.${managed.failureStage}`) : null,
    managedFailureCodes.has(managed?.diagnosticCode) ? t(`status.${managed.diagnosticCode}`) : null
  ].filter(Boolean);
  return details.length
    ? t('workers.configurationFailureDiagnostic', { detail: details.join(' · ') })
    : t('workers.configurationFailureGeneric');
}

function Status({ value, tone, active = false }) {
  useLanguage();
  return <StatusBadge compact tone={tone ?? statusTone(value)} withDot active={active}>{value ?? t('shared.unknown')}</StatusBadge>;
}

function stageLabel(stage) {
  const key = executionTimeline(stage).find(item => item.state === 'current')?.id;
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

function heartbeatLabel(state) {
  return state === 'Fresh' ? t('workers.heartbeatFresh')
    : state === 'Delayed' ? t('workers.heartbeatDelayed') : t('workers.heartbeatLost');
}

function HeartbeatWaveform({ worker, now }) {
  const heartbeat = workerHeartbeatAge(worker.lastHeartbeatAtUtc, now);
  const label = heartbeatLabel(heartbeat.state);
  const age = heartbeat.state === 'Lost' ? t('workers.heartbeatUnavailable') : relativeAge(worker.lastHeartbeatAtUtc, now);
  return <div className="poc-worker-heartbeat" role="group" aria-label={`${t('workers.lastHeartbeat')}: ${label}, ${age}`}>
    <svg className={`poc-heartbeat-waveform tone-${heartbeat.tone}`} viewBox="0 0 48 36" role="img" aria-label={`${t('workers.heartbeatStatus')}: ${label}`} focusable="false">
      <path d="M 2 18 L 9 18 L 16 4 L 23 32 L 30 10 L 37 18 L 46 18" />
    </svg>
    <span className="poc-worker-heartbeat-copy"><span>{t('workers.lastHeartbeat')}</span><strong>{heartbeat.state === 'Lost' ? label : age}</strong>{heartbeat.state === 'Lost' && <span>{age}</span>}<span className="sr-only">{label}</span></span>
  </div>;
}

function WorkerMoreActions({ administration }) {
  const actions = administration?.actions ?? [];
  const visibleActions = actions.filter(action => ['Enabled', 'Disabled', 'revoke-api', 'repair-delivery', 'revoke-delivery'].includes(action.key));
  if (!visibleActions.length) return null;
  return <>
    <Dropdown.Root>
      <Button color="secondary" iconTrailing={ChevronDown}>{t('workers.moreActions')}</Button>
      <Dropdown.Popover>
        <Dropdown.Menu onAction={key => administration?.onAction?.(String(key))}>
          {visibleActions.map(action => <Dropdown.Item key={action.key} id={action.key} label={action.key === 'revoke-api' ? t('workers.revokeApiToken') : localizeText(action.label)} selectionIndicator="none" isDisabled={!!action.reason || !administration?.onAction} aria-describedby={action.key === 'repair-delivery' || action.key === 'revoke-delivery' ? `poc-delivery-${action.key}-reason` : `poc-action-${action.key}`} />)}
        </Dropdown.Menu>
      </Dropdown.Popover>
    </Dropdown.Root>
    {visibleActions.map(action => <span key={`${action.key}-reason`} id={action.key === 'repair-delivery' || action.key === 'revoke-delivery' ? `poc-delivery-${action.key}-reason` : `poc-action-${action.key}`} className="sr-only">{localizeText(action.reason || t('shared.confirmationRequired'))}</span>)}
  </>;
}

function WorkerHeading({ worker, now, readOnly, administrationHref, workersHref, administration }) {
  useLanguage();
  return <header className="poc-worker-heading">
    <div className="poc-worker-identity">
      <nav aria-label={t('shared.breadcrumb')} className="poc-worker-breadcrumb">
        <a href={workersHref}>{t('shared.workers')}</a><span aria-hidden="true">/</span><span aria-current="page">{worker.displayName || t('workers.worker')}</span>
      </nav>
      <div className="poc-worker-title-row"><h1 tabIndex={-1}>{worker.displayName || t('shared.workerDetails')}</h1><HeartbeatWaveform worker={worker} now={now} /></div>
      <div className="poc-worker-meta">
        <span className="poc-worker-id"><GuidDisplay value={worker.workerId} /></span><CopyWorkerId id={worker.workerId} />{worker.workerVersion && <span>{t('workers.workerVersion')} {worker.workerVersion}</span>}
        {readOnly && <a className="poc-readonly-link" href={administrationHref ?? `/workers/${encodeURIComponent(worker.workerId)}`}>{t('shared.openWorkerDetailAndAdministration')}</a>}
      </div>
    </div>
    <div className="poc-worker-heading-actions">
      {administration?.worker && administration?.onEdit && <Button color="secondary" isDisabled={administration.pending || administration.needsRefresh} onPress={() => administration.onEdit()}>{t('workers.editWorker')}</Button>}
      <WorkerMoreActions administration={administration} />
    </div>
  </header>;
}

function WorkerStatusCard({ label, value, detail, tone = 'gray' }) {
  return <article className="poc-worker-status-card" aria-label={`${label}: ${value}`}>
    <span className="poc-worker-status-label">{label}</span>
    <span className="poc-worker-status-value"><span className={`poc-worker-card-dot tone-${tone}`} aria-hidden="true" />{value}</span>
    <span className="poc-worker-status-detail">{detail}</span>
  </article>;
}

function WorkerOperationalStatus({ worker, node, diagnostics }) {
  const statuses = workerDetailStatuses(worker, node, diagnostics);
  const connection = statuses.connection.state === 'Connected' ? t('workers.connected')
    : statuses.connection.state === 'Disconnected' ? t('workers.disconnected') : t('shared.unknown');
  const scheduling = statuses.scheduling.state === 'Active' ? t('workers.schedulingActive')
    : statuses.scheduling.state === 'Inactive' ? t('workers.schedulingInactive')
      : statuses.scheduling.state === 'Draining' ? t('workers.schedulingDraining') : t('shared.unknown');
  const readiness = currentWorkerExecutionReadiness(worker, node);
  const configuration = workerManagedSettingsState(worker, diagnostics);
  const slots = workerSlotSummary(worker);
  const occupied = slots.occupied == null ? t('shared.unavailable') : slots.occupied;
  const capacity = slots.capacity == null ? t('shared.unavailable') : slots.capacity;
  const slotDetail = slots.available == null ? t('workers.availableSlotsUnavailable')
    : slots.available === 0 ? t('workers.atCapacity') : t('workers.availableSlotCount', { count: slots.available });
  const connectionDetail = statuses.connection.state === 'Connected' ? t('workers.workerReachable')
    : statuses.connection.state === 'Disconnected' ? t('workers.workerNotReachable') : t('workers.connectionUnavailable');
  const readinessDetail = node ? t('workers.currentServerReadiness') : t('workers.readinessUnavailable');
  const settingsDetail = configuration === 'Standalone' ? t('workers.noManagedConfiguration')
    : configuration === 'Unknown' || configuration === 'Unavailable' ? t('workers.configurationStateUnavailable')
      : ['error', 'not-synchronized', 'out-of-sync'].includes(diagnostics?.configurationSynchronization?.toLowerCase()) ? configurationFailureDiagnostic(diagnostics)
        : localizeText(diagnostics.configurationSynchronization);
  const readinessLabel = localizeText(readiness);
  const configurationLabel = configuration === 'Synced' ? t('workers.managedSynced')
    : configuration === 'Cached' ? t('workers.managedCached')
      : configuration === 'Pending' ? t('workers.managedPending')
        : configuration === 'Needs attention' ? t('workers.managedNeedsAttention')
          : configuration === 'Standalone' ? t('workers.managedStandalone')
            : configuration === 'Unavailable' ? t('workers.unavailable') : t('shared.unknown');
  const settingsTone = configuration === 'Synced' ? 'success' : configuration === 'Needs attention' ? 'error' : configuration === 'Pending' || configuration === 'Cached' ? 'warning' : 'gray';
  return <section className="poc-operational-status" aria-labelledby="poc-operational-status-heading">
    <div className="poc-operational-status-heading"><h2 id="poc-operational-status-heading">{t('workers.operationalStatus')}</h2><p>{t('workers.operationalStatusSummary')}</p></div>
    <div className="poc-worker-status-grid">
      <WorkerStatusCard label={t('workers.connection')} value={connection} tone={statuses.connection.tone} detail={connectionDetail} />
      <WorkerStatusCard label={t('workers.schedulingStatus')} value={scheduling} tone={statuses.scheduling.tone} detail={scheduling === t('workers.schedulingActive') ? t('workers.acceptingAssignments') : scheduling === t('workers.schedulingDraining') ? t('workers.noNewAssignments') : t('workers.schedulingPolicyUnavailable')} />
      <WorkerStatusCard label={t('workers.executionReadiness')} value={readinessLabel} tone={readiness === 'ready' ? 'success' : readiness === 'not-ready' ? 'error' : 'gray'} detail={readinessDetail} />
      <WorkerStatusCard label={t('workers.managedSettings')} value={configurationLabel} tone={settingsTone} detail={settingsDetail} />
      <WorkerStatusCard label={t('workers.workSlots')} value={`${occupied} / ${capacity}`} tone={slots.available === 0 ? 'warning' : slots.available == null ? 'gray' : 'success'} detail={slotDetail} />
    </div>
  </section>;
}

function PanelHeading({ id, title, trailing }) {
  useLanguage();
  return <div className="poc-panel-heading">
    <h2 id={id}>{title}</h2>
    {trailing}
  </div>;
}

function WorkReference({ item, project, showTitle = false }) {
  useLanguage();
  const work = item.workReference;
  if (!work) return <span>{t('executions.workReferenceUnavailable')}</span>;
  const url = issueLink(work, item.projectId);
  if (work.type === 'github-issue' && showTitle) {
    return <>
      <IssueTitle projectId={item.projectId} repository={project?.repository} workReference={work} />
      {!url && <span className="block text-xs text-tertiary">{t('shared.issueLinkUnavailable')}</span>}
    </>;
  }
  const identity = work.type === 'github-issue' ? `${t('workers.issue')} #${work.id}` : `${localizeText(work.type)} ${work.id}`;
  return url ? <Link to={url}>{identity}</Link>
    : <span>{identity}{work.type === 'github-issue' && <span className="block text-xs text-tertiary">{t('shared.issueLinkUnavailable')}</span>}</span>;
}

function ExecutionTimeline({ item, now }) {
  useLanguage();
  const stages = executionTimeline(item.currentStage, item.stageStartedAtUtc, now);
  return <div className="poc-execution-timeline">
    <ol className="poc-stage-list" aria-label={t('workers.executionStages')}>
      {stages.map(stage => {
        const classes = [
          stage.state === 'completed' && 'is-complete',
          stage.state === 'current' && 'is-current',
          stage.connectorComplete && 'has-completed-connector'
        ].filter(Boolean).join(' ');
        const detail = stage.state === 'completed' ? t('workers.stageCompleted')
          : stage.state === 'current' ? t('workers.currentStageReported')
            : stage.state === 'pending' ? t('workers.stagePending') : t('workers.notReported');
        const markerState = stage.state === 'completed' ? 'is-complete' : stage.state === 'current' ? 'is-current' : '';
        return <li key={stage.id} className={classes} aria-current={stage.state === 'current' ? 'step' : undefined}>
          <span className={`poc-stage-marker ${markerState}`} aria-hidden="true">
            {stage.state === 'completed' ? <svg viewBox="0 0 16 16" focusable="false"><path d="m3.5 8.25 3 2.75 6-6" /></svg>
              : stage.state === 'current' ? <span className="poc-stage-active-dot" /> : null}
          </span>
          <span className="poc-stage-name">{localizeText(stage.label)}</span>
          <span className="poc-stage-detail">{detail}</span>
          {(stage.state === 'completed' || stage.state === 'current') &&
            <span className="poc-stage-duration">{stage.duration ?? t('shared.durationUnavailable')}</span>}
        </li>;
      })}
    </ol>
    <p className="poc-stage-history-note">{t('workers.stageTimingNote')}</p>
  </div>;
}

function CurrentExecution({ item, projects, now, showStatus }) {
  useLanguage();
  const [liveNow, setLiveNow] = useState(now);
  const active = isActiveExecutionState(item.state);
  useEffect(() => {
    if (!active) return undefined;
    const interval = window.setInterval(() => setLiveNow(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, [active]);
  const displayNow = Math.max(liveNow, now);
  const project = projects?.find(value => value.id === item.projectId);
  const stage = item.currentStage ? stageLabel(item.currentStage) : localizeText(item.state);
  return <article className="poc-current-execution">
    {showStatus && <div className="poc-current-execution-heading"><span>{t('workers.execution')}</span><Status value={stage} tone={isActiveExecutionState(item.state) ? 'info' : item.currentStage ? 'warning' : statusTone(item.state)} active={isActiveExecutionState(item.state)} /></div>}
    <dl className="poc-execution-fields">
      <div>
        <dt>{t('executions.project')}</dt>
        <dd><a href={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</a></dd>
      </div>
      <div>
        <dt>{t('workers.gitHubIssue')}</dt>
        <dd><WorkReference item={item} project={project} showTitle /></dd>
      </div>
      <div>
        <dt>{t('shared.elapsed')}</dt>
        <dd>{duration(item, displayNow)}</dd>
      </div>
    </dl>
    <ExecutionTimeline item={item} now={displayNow} />
    <p className="poc-current-execution-timing">{item.startedAtUtc ? `${t('executions.started')} ${relativeAge(item.startedAtUtc, now)}` : t('workers.executionStartNotReported')} · <a href={`/executions/${encodeURIComponent(item.id)}`}>{t('shared.executionDetails')}</a></p>
  </article>;
}

function CurrentExecutions({ items, projects, now, worker, unavailable }) {
  useLanguage();
  const currentStage = items.length === 1 ? items[0].currentStage ? stageLabel(items[0].currentStage) : localizeText(items[0].state) : null;
  const hasReportedWork = worker.activeAssignments > 0 || worker.activeExecutions > 0;
  const idleReported = worker.activeAssignments === 0 && worker.activeExecutions === 0;
  const trailing = unavailable ? <Status value={t('workers.statusUnavailable')} />
    : items.length ? <Status value={currentStage ?? t('workers.active')} tone={items[0].currentStage ? 'warning' : 'info'} active={isActiveExecutionState(items[0].state)} />
      : hasReportedWork ? <Status value={t('workers.executionDetailsUnavailable')} tone="warning" />
        : idleReported ? <Status value={t('workers.idle')} tone="success" /> : <Status value={t('workers.statusUnavailable')} />;
  return <TableCard.Root className="poc-dashboard-panel poc-current-panel">
    <PanelHeading id="poc-current-heading" title={t('shared.currentExecution')} trailing={trailing} />
    {unavailable ? <p className="poc-empty-panel">{t('shared.currentExecutionDataUnavailableRefreshToRecoverReportedStages')}</p>
      : items.length ? <div className="poc-current-executions-list">{items.map(item => <CurrentExecution key={item.id} item={item} projects={projects} now={now} showStatus={items.length > 1} />)}</div>
      : <div className="poc-current-empty"><strong>{hasReportedWork ? t('workers.executionDetailsUnavailable') : idleReported ? t('workers.noIssueRunning') : t('workers.executionDetailsUnavailable')}</strong><p>{hasReportedWork ? t('shared.theWorkerReportsActiveWorkItsExecutionDetailIsUnavailableInThis') : idleReported ? t('shared.noCurrentExecutionInTheLatest50ServerRequests') : t('workers.currentExecutionStateUnavailable')}</p><a href="/executions">{t('workers.viewAllExecutions')}<span aria-hidden="true"> →</span></a></div>}
  </TableCard.Root>;
}

function RecentExecutions({ items, projects, now, loading }) {
  useLanguage();
  const rows = items.slice(0, 3);
  const viewAll = <a className="poc-view-all" href="/executions">{t('workers.viewAllExecutions')}<span aria-hidden="true">→</span></a>;
  return <TableCard.Root className="poc-dashboard-panel poc-recent-panel">
    <PanelHeading id="poc-recent-heading" title={t('shared.recentExecutions')} trailing={viewAll} />
    {loading ? <p className="poc-empty-panel">{t('shared.executionHistoryUnavailableRefreshToTryAgain')}</p>
      : rows.length ? <ol className="poc-recent-list" aria-labelledby="poc-recent-heading">{rows.map(item => {
            const project = projects?.find(value => value.id === item.projectId);
            return <li className="poc-recent-row" key={item.id}>
              <div className="poc-recent-copy"><div className="poc-recent-issue"><WorkReference item={item} project={project} showTitle /></div>
                <p><a href={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</a><span aria-hidden="true"> · </span>{item.startedAtUtc ? timestamp(item.startedAtUtc) : t('workers.notReported')}<span aria-hidden="true"> · </span>{duration(item, now)}<span aria-hidden="true"> · </span><a href={`/executions/${encodeURIComponent(item.id)}`}><GuidDisplay value={item.id} /></a></p>
              </div>
              <div className="poc-recent-result"><Status value={item.state} />{item.recoveryState && <span className="poc-recent-recovery">{t('executions.recoveryStatus')}: {statusLabel(item.recoveryState)}</span>}</div>
            </li>;
          })}</ol> : <p className="poc-empty-panel">{t('shared.noRecentTerminalExecutionsForThisWorkerInThisView')}</p>}
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

function capabilityStatus(capability, id) {
  if (!capability) return t('shared.unknown');
  const { definition, state } = capability;
  if (!state.detectedAtUtc) return t('shared.unknown');
  const installed = state.installation?.toLowerCase() === 'installed';
  const healthy = state.health?.toLowerCase() === 'healthy';
  const authenticated = !definition.requiresAuthentication || state.authentication?.toLowerCase() === 'satisfied';
  const configured = !definition.requiresConfiguration || state.configuration?.toLowerCase() === 'satisfied';
  if (installed && healthy && authenticated && configured) return id === 'dotnet-sdk' ? t('workers.available') : t('workers.ready');
  if (!installed) return state.installation || t('shared.unknown');
  if (!healthy) return state.health || t('shared.unknown');
  if (!authenticated) return state.authentication || t('shared.unknown');
  if (!configured) return state.configuration || t('shared.unknown');
  return t('shared.unknown');
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

function CapabilityTile({ tile, capability }) {
  useLanguage();
  const name = localizeText(tile.name);
  const status = capabilityStatus(capability, tile.id);
  const tone = status === t('workers.ready') || status === t('workers.available') ? 'success' : statusTone(status);
  const lastReported = capability?.state.detectedAtUtc
    ? `${t('workers.lastReported')} ${timestamp(capability.state.detectedAtUtc)}`
    : t('workers.notChecked');
  return <article className="poc-service-tile" aria-label={`${name}: ${localizeText(status)}`}>
    <div className="poc-service-top">
      <ServiceMark type={tile.type} />
      <div className="poc-service-heading"><h3>{name}</h3><Status value={status} tone={tone} /></div>
    </div>
    <p className="poc-service-detail">{capabilityDetail(tile, capability)}</p>
    <p className="poc-service-support">{capabilitySupportingText(tile, capability)} · {lastReported}</p>
  </article>;
}

function CapabilitiesPanel({ node }) {
  useLanguage();
  const capabilityById = new Map((node?.capabilities ?? []).map(capability => [capability.definition.id, capability]));
  return <TableCard.Root className="poc-dashboard-panel poc-capabilities-panel">
    <PanelHeading id="poc-capabilities-heading" title={t('shared.capabilitiesAndProvisioning')} />
    {node ? <div className="poc-service-grid">{serviceTiles.map(tile => <CapabilityTile key={tile.id} tile={tile} capability={capabilityById.get(tile.id)} />)}</div>
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
  const hasReportedChecks = !!worker.lastHeartbeatAtUtc;
  const preflight = !diagnostics || !hasReportedChecks ? t('shared.unknown') : diagnostics.aiAgentReady ? t('workers.ready') : t('workers.notReady');
  const eligibleCount = diagnostics?.projects?.filter(project => project.isEligible).length;
  const eligibilityStatus = eligibleCount == null || !hasReportedChecks ? t('shared.unknown') : eligibleCount > 0 ? t('workers.ready') : t('workers.notReady');
  const eligible = eligibilityStatus === t('shared.unknown') ? t('workers.notChecked') : t('workers.eligibleProjects', { count: eligibleCount });
  const heartbeat = worker.lastHeartbeatAtUtc ? relativeAge(worker.lastHeartbeatAtUtc, now) : t('workers.heartbeatUnavailable');
  const freshness = workerDetailStatuses(worker, node, diagnostics).freshness;
  const preflightTone = preflight === t('workers.ready') ? 'success' : statusTone(preflight);
  const projectTone = statusTone(eligibilityStatus);
  const readinessValue = localizeText(currentWorkerExecutionReadiness(worker, node));
  const reportedAt = worker.lastHeartbeatAtUtc ? timestamp(worker.lastHeartbeatAtUtc) : t('workers.notChecked');
  return <TableCard.Root className="poc-dashboard-panel poc-readiness-panel">
    <PanelHeading id="poc-readiness-heading" title={t('workers.readinessChecks')} />
    <div className="poc-readiness-list">
      <ReadinessRow icon={<Shield01 />} title={t('workers.lastReportedExecutionPreflight')} detail={preflight === t('shared.unknown') ? t('workers.notChecked') : `${diagnostics.aiAgentReady ? t('workers.environmentAndToolsReady') : t('workers.preflightNotReady')} · ${t('workers.lastReported')} ${reportedAt}`} value={preflight} tone={preflightTone} />
      <ReadinessRow icon={<span className="poc-project-readiness-icon"><i/><i/></span>} title={t('workers.lastReportedProjectEligibility')} detail={eligibilityStatus === t('shared.unknown') ? eligible : `${eligible} · ${t('workers.lastReported')} ${reportedAt}`} value={eligibilityStatus} tone={projectTone} />
      <ReadinessRow icon={<Activity />} title={t('workers.currentExecutionReadiness')} detail={!node ? t('workers.readinessUnavailable') : node.connectivity === 'disconnected' ? t('workers.disconnectedBlocksReadiness') : node.observationsStale ? t('workers.observationIsStale') : t('workers.currentServerReadiness')} value={readinessValue} tone={statusTone(readinessValue)} />
      <ReadinessRow icon={<Activity />} title={t('workers.heartbeatFreshness')} detail={`${t('workers.lastHeartbeat')}: ${heartbeat}`} value={freshnessLabel(freshness.state)} tone={freshness.tone} />
    </div>
  </TableCard.Root>;
}

function SchedulingControls({ administration }) {
  useLanguage();
  const policy = administration?.worker?.schedulingPolicy;
  const actionKey = policy === 'Enabled' ? 'Draining' : 'Enabled';
  const action = administration?.actions?.find(item => item.key === actionKey);
  const currentPolicy = policy === 'Enabled' ? t('workers.schedulingActive')
    : policy === 'Draining' ? t('workers.schedulingDraining')
      : policy === 'Disabled' ? t('workers.schedulingInactive') : t('shared.unknown');
  const actionLabel = actionKey === 'Draining' ? t('workers.pauseNewAssignments') : t('workers.activateScheduling');
  return <TableCard.Root className="poc-dashboard-panel poc-control-panel poc-control-rail">
    <PanelHeading id="poc-controls-heading" title={t('workers.schedulingControls')} />
    <div className="poc-scheduling-content">
      <p className="poc-scheduling-state"><span className={`poc-worker-card-dot ${policy === 'Enabled' ? 'tone-success' : policy === 'Draining' ? 'tone-warning' : 'tone-gray'}`} aria-hidden="true" />{currentPolicy}{policy === 'Enabled' ? ` · ${t('workers.acceptingAssignments')}` : ''}</p>
      <Button color={actionKey === 'Draining' ? 'secondary' : 'primary'} size="md" className="w-full" iconLeading={actionKey === 'Draining' ? PauseCircle : Play}
        isDisabled={!action || !!action.reason || administration?.pending || administration?.needsRefresh || !administration?.onAction}
        aria-describedby="poc-scheduling-action-help" onPress={() => administration?.onAction?.(actionKey)}>{actionLabel}</Button>
      <p id="poc-scheduling-action-help" className="poc-scheduling-help">{actionKey === 'Draining' ? t('workers.pauseSchedulingHelp') : t('workers.activationPrerequisite')}<span className="sr-only">{localizeText(action?.reason || t('shared.confirmationRequired'))}</span></p>
      {administration?.message && <p className="poc-control-message" role="status" aria-live="polite">{localizeText(administration.message)}</p>}
    </div>
  </TableCard.Root>;
}

function WorkerStatusRefresh({ pending, message, updatedAt, onRefresh }) {
  useLanguage();
  const lastUpdated = updatedAt ? relativeAge(new Date(updatedAt).toISOString(), Date.now()) : t('workers.statusNotUpdated');
  return <TableCard.Root className="poc-dashboard-panel poc-control-panel poc-status-refresh-panel">
    <div className="poc-panel-heading"><h2>{t('workers.workerStatus')}</h2></div>
    <div className="poc-status-refresh-content">
      <p>{t('workers.workerStatusRefreshHelp')}</p>
      <p>{t('workers.workerStatusRefreshReadOnly')}</p>
      <Button color="secondary" size="md" className="w-full" isDisabled={pending} aria-busy={pending} onPress={onRefresh}>
        {pending ? t('workers.workerStatusRefreshing') : t('workers.reloadWorkerStatus')}
      </Button>
      <p className="poc-status-refresh-note">{t('workers.workerStatusRefreshDirect')}</p>
      {message && <p className="poc-control-message" role={message.failed ? 'alert' : 'status'} aria-live="polite">{message.text}</p>}
      <p className="poc-status-refresh-updated">{t('workers.statusLastUpdated', { time: lastUpdated })}</p>
    </div>
  </TableCard.Root>;
}

export function CapabilityCard({ capability, commands, children }) {
  useLanguage();
  const { definition, state, availableActions } = capability;
  const observed = !!state.detectedAtUtc;
  const matching = commands?.filter(command => command.request.capabilityId === definition.id)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const pending = matching?.filter(command => ['Pending', 'Running'].includes(command.status)) ?? [];
  const latest = matching?.[0];
  return <TableCard.Root className="poc-capability"><TableCard.Header title={definition.displayName} /><div className="p-4">
    <dl className="poc-facts">
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.installation')}</dt><dd className="text-sm text-secondary"><Status value={observed ? state.installation : t('shared.unknown')} /> · {state.detectedVersion || t('shared.versionUnknown')}</dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.health')}</dt><dd className="text-sm text-secondary"><Status value={observed ? state.health : t('shared.unknown')} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.authentication')}</dt><dd className="text-sm text-secondary"><Status value={observed ? state.authentication ?? (definition.requiresAuthentication ? 'Unknown' : t('shared.notApplicable')) : t('shared.unknown')} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.configuration')}</dt><dd className="text-sm text-secondary"><Status value={observed ? state.configuration ?? (definition.requiresConfiguration ? 'Unknown' : t('shared.notApplicable')) : t('shared.unknown')} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.update')}</dt><dd className="text-sm text-secondary"><Status value={observed ? state.update ?? 'Unknown' : t('shared.unknown')} /></dd></div>
      <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{t('shared.detected')}</dt><dd className="text-sm text-secondary">{state.detectedAtUtc ? timestamp(state.detectedAtUtc) : t('workers.notChecked')}</dd></div>
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

export function WorkerDetail({ id, observations, loading, diagnostics = null, nodes = null, nodeCommands = null, executions = null, projects = null, administration = null, readOnly = false, administrationHref = null, workersHref = '/workers', now = Date.now(), preparation = null, provisioning = null, restart = null, statusRefreshing = false, statusMessage = null, statusUpdatedAt = null, onRefreshStatus }) {
  useLanguage();
  const worker = observations?.find(item => item.workerId === id);
  const node = nodes?.find(item => item.id === id && item.kind === 'worker');
  const items = workerExecutions(executions, id);
  const active = items.filter(item => item.state === 'Assigned' || isActiveExecutionState(item.state));
  const recent = items.filter(item => terminalStates.includes(item.state));
  const commands = nodeCommands?.filter(command => command.request.nodeId === id) ?? null;
  return <section className="poc-worker-detail-screen" aria-label={t('shared.workerDetail')}>
    {!worker ? <>
      <div className="poc-worker-state"><p role="status">{loading ? t('shared.loadingCurrentWorkerObservations') : observations ? t('shared.workerUnavailableOrDeletedReturnToWorkersToRefreshTheInventory') : t('shared.currentWorkerObservationsUnavailableSignInOrRefreshToRecoverCurrentState')}</p></div>
      {!readOnly && <div className="poc-worker-recovery-actions"><WorkerStatusRefresh pending={statusRefreshing || administration?.pending} message={statusMessage} updatedAt={statusUpdatedAt} onRefresh={onRefreshStatus} /></div>}
    </> : <>
      <WorkerHeading worker={worker} now={now} readOnly={readOnly} administrationHref={administrationHref} workersHref={workersHref} administration={administration} />
      <WorkerOperationalStatus worker={worker} node={node} diagnostics={diagnostics} />
      <div className={readOnly ? 'poc-worker-layout poc-worker-read-only' : 'poc-worker-layout'}>
        <div className="poc-worker-main poc-main">
          <CurrentExecutions items={active} projects={projects} now={now} worker={worker} unavailable={executions === null} />
        </div>
        {!readOnly && <aside className="poc-worker-rail poc-secondary-rail" aria-label={t('workers.workerControlsAndReadiness')}>
          <SchedulingControls administration={administration} />
          {restart}
          <WorkerStatusRefresh pending={statusRefreshing || administration?.pending} message={statusMessage} updatedAt={statusUpdatedAt ?? observations?.updatedAt} onRefresh={onRefreshStatus} />
        </aside>}
      </div>
      <RecentExecutions items={recent} projects={projects} now={now} loading={executions === null} />
      <div className="poc-worker-lower-grid">
        <div className="poc-worker-technical-column">
          <WorkerResources worker={worker} node={node} now={now} />
          <CapabilitiesPanel node={node} />
        </div>
        <ReadinessPanel worker={worker} node={node} diagnostics={diagnostics} now={now} />
      </div>
      <div className="poc-worker-extra">
        {diagnostics && <AdvancedDisclosure title={t('shared.readinessEvidence')}>
          <p>{t('shared.reportedReadinessEvidenceCodexPreflight')} {worker.lastHeartbeatAtUtc ? diagnostics.aiAgentReady ? t('shared.yes') : t('shared.no') : t('workers.notChecked')} · {t('shared.gitHubAccess')} {worker.lastHeartbeatAtUtc ? diagnostics.gitHubReady ? t('shared.yes') : t('shared.no') : t('workers.notChecked')} · {t('shared.gitAccess')} {worker.lastHeartbeatAtUtc ? diagnostics.gitReady ? t('shared.yes') : t('shared.no') : t('workers.notChecked')}</p>
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
            {['pending', 'failed', 'not-configured', 'revoked'].includes(administration?.delivery?.status) && <p role="status">{t('workers.repairDeliveryDescription')}</p>}
            <p id="poc-delivery-effect">{t('shared.revokingDeliveryAuthorizationStopsFutureServerCredentialDeliveryWorkerAPIAuthenticationAnd')}</p>
            <p id="poc-token-effect">{t('shared.revokingWorkerAPIAuthenticationDeniesCallsUsingItsTokenActiveLeasesMay')}</p>
            {administration?.actions?.map(action => <p key={action.key}>{localizeText(action.label)} · {localizeText(action.reason || t('shared.confirmationRequired'))}</p>)}
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
