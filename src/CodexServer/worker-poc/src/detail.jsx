import { Badge } from './badge.jsx';
import { duration, issueLink, statusColor, terminalStates, timestamp, workerExecutions } from './model.js';

function Status({ value }) { return <Badge color={statusColor(value)}>{value ?? 'Unknown'}</Badge>; }
function Fact({ label, children }) { return <div className="poc-fact"><dt>{label}</dt><dd>{children}</dd></div>; }
function Execution({ item, projects, now, active }) {
  const project = projects?.find(project => project.id === item.projectId);
  const work = item.workReference;
  const url = issueLink(work, project?.repository);
  const identity = work?.type === 'github-issue' ? `Issue #${work.id}` : `${work?.type ?? 'Work'} ${work?.id ?? 'Unknown'}`;
  return <article className={active ? 'poc-execution poc-current' : 'poc-execution'}>
    <div><p className="poc-project">{project?.name ?? item.projectId}</p>
      <h4>{url ? <a href={url} target="_blank" rel="noopener noreferrer">{identity}</a> : identity}</h4>
      <p className="poc-muted">{work?.type === 'github-issue' && 'Issue title unavailable · '}{!url && work?.type === 'github-issue' && 'Issue link unavailable · '}<a href={'/executions/' + encodeURIComponent(item.id)}>Execution details</a></p>
    </div>
    <div><Status value={item.state} />{active && <p>Current stage: <strong>{item.currentStage || 'Not reported'}</strong></p>}
      <p className="poc-muted">{active ? 'Elapsed' : 'Duration'}: {duration(item, now)}</p>
      <p className="poc-muted">{active ? item.startedAtUtc ? 'Started' : 'Assigned' : item.completedAtUtc ? 'Completed' : 'Requested'}: {timestamp(active ? item.startedAtUtc ?? item.assignedAtUtc : item.completedAtUtc ?? item.createdAtUtc)}</p>
      {item.recoveryState && <p>Recovery: {item.recoveryState}</p>}
    </div>
  </article>;
}
function Capability({ capability, commands }) {
  const { definition, state, availableActions } = capability;
  const matching = commands?.filter(command => command.request.capabilityId === definition.id)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const pending = matching?.filter(command => ['Pending', 'Running'].includes(command.status)) ?? [];
  const latest = matching?.[0];
  return <article className="poc-capability">
    <h4>{definition.displayName}</h4>
    <dl className="poc-facts">
      <Fact label="Installation"><Status value={state.installation} /> · {state.detectedVersion || 'Version unknown'}</Fact>
      <Fact label="Health"><Status value={state.health} /></Fact>
      <Fact label="Authentication"><Status value={state.authentication ?? (definition.requiresAuthentication ? 'Unknown' : 'Not applicable')} /></Fact>
      <Fact label="Configuration"><Status value={state.configuration ?? (definition.requiresConfiguration ? 'Unknown' : 'Not applicable')} /></Fact>
      <Fact label="Update"><Status value={state.update} /></Fact>
      <Fact label="Detected">{timestamp(state.detectedAtUtc)}</Fact>
      <Fact label="Operation"><Status value={state.operation?.state} /> {state.operation?.action}</Fact>
    </dl>
    {pending.map(command => <p key={command.id}><Status value={command.status} /> {command.request.action}</p>)}
    {!pending.length && latest && <p>Latest command: <Status value={latest.status} /> {latest.request.action}</p>}
    {commands === null && <p className="poc-muted">Command history unavailable.</p>}
    <p>Available typed actions: {availableActions?.length ? availableActions.join(', ') : 'None reported'}</p>
    <details><summary>Diagnostic context</summary><p>{state.diagnosticCode || 'No diagnostic code reported'}{state.operation?.diagnosticCode && ' · ' + state.operation.diagnosticCode}</p>{latest && <p>Command {latest.id} · {latest.diagnostic}</p>}</details>
  </article>;
}
function ControlRail({ administration }) {
  const worker = administration?.worker;
  const actions = administration?.actions ?? [
    { key: 'Enabled', label: 'Activate scheduling' }, { key: 'Draining', label: 'Drain worker' },
    { key: 'Disabled', label: 'Deactivate' }, { key: 'revoke-api', label: 'Revoke Worker API token' }
  ].map(action => ({ ...action, reason: 'Current administration evidence unavailable. Refresh to recover it.' }));
  return <aside className="poc-control-rail" aria-labelledby="poc-controls-title">
    <h3 id="poc-controls-title">Worker controls</h3>
    <dl className="poc-facts">
      <Fact label="Scheduling policy"><Status value={worker?.schedulingPolicy ?? 'Unavailable'} />{worker?.schedulingPolicy === 'Draining' && <p>{worker.activeAssignments === 0 ? 'Drained; scheduling is paused.' : `Draining; ${worker.activeAssignments} active assignment(s) keep their leases.`}</p>}</Fact>
      <Fact label="Worker API token"><Status value={worker?.authenticationCredentialStatus ?? 'Unavailable'} />{worker?.authenticationCredentialRevokedAtUtc && <p>Revoked: {timestamp(worker.authenticationCredentialRevokedAtUtc)}</p>}</Fact>
    </dl>
    <p id="poc-policy-effect">Scheduling controls affect new assignments. Existing assignments and leases are not cancelled. Activation requires current Server readiness evidence and validation.</p>
    <p id="poc-token-effect">Revoking Worker API authentication denies calls using its token; active leases may expire into recovery. It does not revoke credential-delivery authorization, node login or provider credentials.</p>
    <div className="poc-controls">{actions.map(action => <div key={action.key}>
      <button type="button" className="poc-secondary" disabled={!!action.reason || !administration?.onAction} aria-describedby={`${action.key === 'revoke-api' ? 'poc-token-effect' : 'poc-policy-effect'} poc-action-${action.key}`} onClick={() => administration?.onAction(action.key)}>{action.label}</button>
      <p id={'poc-action-' + action.key} className="poc-muted">{action.reason || 'Confirmation required.'}</p>
    </div>)}</div>
    <p role="status" aria-live="polite">{administration?.message}</p>
    <button type="button" className="poc-secondary" disabled={administration?.pending || !administration?.onRefresh} onClick={() => administration?.onRefresh()}>Refresh authoritative state</button>
  </aside>;
}
export function WorkerDetail({ id, observations, loading, diagnostics = null, nodes = null, nodeCommands = null, executions = null, projects = null, administration = null, now = Date.now() }) {
  const worker = observations?.find(item => item.workerId === id);
  const node = nodes?.find(item => item.id === id && item.kind === 'worker');
  const items = workerExecutions(executions, id);
  const active = items.filter(item => ['Assigned', 'Running'].includes(item.state));
  const recent = items.filter(item => terminalStates.includes(item.state)).slice(0, 10);
  const commands = nodeCommands?.filter(command => command.request.nodeId === id) ?? null;
  return <section className="poc-card" aria-label="Worker detail proof of concept">
    <header><p className="poc-eyebrow">Worker detail · proof of concept</p><h2>{worker?.displayName || 'Worker details'}</h2><p className="poc-muted poc-identity">ID {id}</p>
      <a href={'/workers/' + encodeURIComponent(id)}>Open Worker detail and administration</a></header>
    {!worker ? <p role="status">{loading ? 'Loading current Worker observations…' : observations ? 'Worker unavailable or deleted. Return to Workers to refresh the inventory.' : 'Current Worker observations unavailable. Sign in or refresh to recover current state.'}</p> : <div className="poc-layout"><div className="poc-main">
      <dl className="poc-status-grid">
        <Fact label="Connection"><Status value={worker.availability} />{node && <p>Node: {node.connectivity}</p>}</Fact>
        <Fact label="Lifecycle / readiness"><Status value={worker.lifecycleState} /><p>Execution prerequisites: <Status value={node?.executionReadiness ?? 'Unavailable'} /></p><p>Scheduling: {worker.schedulingPolicy ?? 'Unknown'}</p></Fact>
        <Fact label="Observation freshness"><Status value={node ? node.observationsStale ? 'Stale' : 'Current' : 'Unknown'} /><p>Last heartbeat: {timestamp(worker.lastHeartbeatAtUtc)}</p></Fact>
        <Fact label="Worker slots"><strong>{worker.activeExecutions ?? 'Unknown'} / {worker.maximumCapacity ?? worker.capacity ?? 'Unknown'} active</strong><p>{worker.availableCapacity ?? 'Unknown'} available · {worker.activeAssignments ?? 'Unknown'} active Server assignments</p></Fact>
      </dl>
      <p className="poc-muted">Connectivity, execution prerequisites and scheduling policy are separate observations. Stale evidence does not establish current readiness.</p>
      <section className="poc-section"><h3>Current execution{active.length > 1 ? 's' : ''}</h3>
        {executions === null ? <p>Current execution data unavailable. Refresh to recover reported stages.</p> : active.length ? active.map(item => <Execution key={item.id} item={item} projects={projects} now={now} active />) : <p>No current execution in the latest 50 Server requests. {worker.activeAssignments > 0 || worker.activeExecutions > 0 ? 'The Worker reports active work; its execution detail is unavailable in this bounded view.' : 'No active work reported by this Worker.'}</p>}
      </section>
      <section className="poc-section"><h3>Recent executions</h3><p className="poc-muted">Up to 10 outcomes for this Worker from the latest 50 Server requests. Older or unreported executions may be absent.</p>
        {executions === null ? <p>Execution history unavailable. Refresh to try again.</p> : recent.length ? recent.map(item => <Execution key={item.id} item={item} projects={projects} now={now} />) : <p>No recent terminal executions for this Worker in this view.</p>}
      </section>
      <section className="poc-section"><h3>Capabilities and provisioning</h3><p>Provisioning readiness: <Status value={node?.provisioningReadiness ?? 'Unavailable'} /> · Latest operation: <Status value={diagnostics?.provisioningState ?? 'Unavailable'} /></p>
        {diagnostics ? <><p>Reported readiness evidence: Codex preflight {diagnostics.aiAgentReady ? 'present' : 'absent'} · GitHub access {diagnostics.gitHubReady ? 'present' : 'absent'} · Git access {diagnostics.gitReady ? 'present' : 'absent'}</p><p>Configuration synchronization: <Status value={diagnostics.configurationSynchronization} /></p>{diagnostics.latestProvisioningOperation && <p>{diagnostics.latestProvisioningOperation.action} · {diagnostics.latestProvisioningOperation.status}</p>}</> : <p>Worker readiness diagnostics unavailable.</p>}
        {node ? node.capabilities?.length ? <div className="poc-capabilities">{node.capabilities.map(capability => <Capability key={capability.definition.id} capability={capability} commands={commands} />)}</div> : <p>No capabilities reported.</p> : <p>Capability observations unavailable. Refresh provisioning state.</p>}
      </section>
    </div><ControlRail administration={administration} /></div>}
  </section>;
}
