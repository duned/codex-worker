import { StatusBadge, PageHeading, AdvancedDisclosure } from './shared/Presentation';
import { Button } from './untitled/components/base/buttons/button';
import { Table, TableCard } from './untitled/components/application/table/table';
import { FeaturedIcon } from './untitled/components/foundations/featured-icon/featured-icon';
import { Activity, Terminal } from '@untitledui/icons';

import { duration, issueLink, statusColor, terminalStates, timestamp, workerExecutions } from './model.js';

function Status({ value }) { return <StatusBadge tone={statusColor(value)}>{value ?? 'Unknown'}</StatusBadge>; }
function Fact({ label, children }) { return <div className="poc-fact"><dt className="mb-1 text-sm text-tertiary">{label}</dt><dd className="text-sm text-secondary">{children}</dd></div>; }
function Execution({ item, projects, now, active }) {
  const project = projects?.find(project => project.id === item.projectId);
  const work = item.workReference;
  const url = issueLink(work, project?.repository);
  const identity = work?.type === 'github-issue' ? `Issue #${work.id}` : `${work?.type ?? 'Work'} ${work?.id ?? 'Unknown'}`;
  return <TableCard.Root className="poc-current"><div className="poc-execution p-5">
    <div><p className="poc-project mb-1 text-sm font-medium text-tertiary">{project?.name ?? item.projectId}</p>
      <h4 className="text-lg font-semibold text-primary">{url ? <a href={url} target="_blank" rel="noopener noreferrer">{identity}</a> : identity}</h4>
      <p className="poc-muted text-sm text-tertiary">{work?.type === 'github-issue' && 'Issue title unavailable · '}{!url && work?.type === 'github-issue' && 'Issue link unavailable · '}<a href={'/executions/' + encodeURIComponent(item.id)}>Execution details</a></p>
    </div>
    <div className="space-y-2"><Status value={item.state} />{active && <p>Current stage: <strong className="text-brand-secondary">{item.currentStage || 'Not reported'}</strong></p>}
      <p className="poc-muted text-sm text-tertiary">{active ? 'Elapsed' : 'Duration'}: {duration(item, now)}</p>
      <p className="poc-muted text-sm text-tertiary">{active ? item.startedAtUtc ? 'Started' : 'Assigned' : item.completedAtUtc ? 'Completed' : 'Requested'}: {timestamp(active ? item.startedAtUtc ?? item.assignedAtUtc : item.completedAtUtc ?? item.createdAtUtc)}</p>
      {item.recoveryState && <p>Recovery: {item.recoveryState}</p>}
    </div>
  <p className="col-span-full text-xs text-tertiary">Only the reported current stage is available; stage history is not provided.</p></div></TableCard.Root>;
}
function Capability({ capability, commands }) {
  const { definition, state, availableActions } = capability;
  const matching = commands?.filter(command => command.request.capabilityId === definition.id)
    .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const pending = matching?.filter(command => ['Pending', 'Running'].includes(command.status)) ?? [];
  const latest = matching?.[0];
  return <TableCard.Root className="poc-capability"><TableCard.Header title={definition.displayName} contentTrailing={<FeaturedIcon icon={Terminal} color="gray" theme="modern" size="sm" />} /><div className="p-4">
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
    {commands === null && <p className="poc-muted text-sm text-tertiary">Command history unavailable.</p>}
    <p>Available typed actions: {availableActions?.length ? availableActions.join(', ') : 'None reported'}</p>
    <AdvancedDisclosure title="Diagnostic context"><p>{state.diagnosticCode || 'No diagnostic code reported'}{state.operation?.diagnosticCode && ' · ' + state.operation.diagnosticCode}</p>{latest && <p>Command {latest.id} · {latest.diagnostic}</p>}</AdvancedDisclosure>
  </div></TableCard.Root>;
}
function ControlRail({ administration }) {
  const worker = administration?.worker;
  const actions = administration?.actions ?? [
    { key: 'Enabled', label: 'Activate scheduling' }, { key: 'Draining', label: 'Drain worker' },
    { key: 'Disabled', label: 'Deactivate' }, { key: 'revoke-api', label: 'Revoke Worker API token' }
  ].map(action => ({ ...action, reason: 'Current administration evidence unavailable. Refresh to recover it.' }));
  return <aside className="poc-control-rail" aria-label="Worker controls"><TableCard.Root>
    <TableCard.Header title="Worker controls" /><div className="p-4">
    <dl className="poc-facts">
      <Fact label="Scheduling policy"><Status value={worker?.schedulingPolicy ?? 'Unavailable'} />{worker?.schedulingPolicy === 'Draining' && <p>{worker.activeAssignments === 0 ? 'Drained; scheduling is paused.' : `Draining; ${worker.activeAssignments} active assignment(s) keep their leases.`}</p>}</Fact>
      <Fact label="Worker API token"><Status value={worker?.authenticationCredentialStatus ?? 'Unavailable'} />{worker?.authenticationCredentialRevokedAtUtc && <p>Revoked: {timestamp(worker.authenticationCredentialRevokedAtUtc)}</p>}</Fact>
    </dl>
    <details className="my-4 text-xs text-tertiary"><summary className="font-medium">Control effects</summary><p id="poc-policy-effect" className="mt-2">Scheduling controls affect new assignments. Existing assignments and leases are not cancelled. Activation requires current Server readiness evidence and validation.</p>
    <p id="poc-token-effect">Revoking Worker API authentication denies calls using its token; active leases may expire into recovery. It does not revoke credential-delivery authorization, node login or provider credentials.</p></details>
    <div className="poc-controls">{actions.map(action => <div key={action.key}>
      <Button color="secondary" size="sm" className="w-full whitespace-normal text-center" isDisabled={!!action.reason || !administration?.onAction} aria-describedby={`${action.key === 'revoke-api' ? 'poc-token-effect' : 'poc-policy-effect'} poc-action-${action.key}`} onPress={() => administration?.onAction(action.key)}>{action.label}</Button>
      <p id={'poc-action-' + action.key} className="poc-muted text-sm text-tertiary">{action.reason || 'Confirmation required.'}</p>
    </div>)}</div>
    <p role="status" aria-live="polite">{administration?.message}</p>
    <Button color="secondary" size="sm" className="w-full whitespace-normal text-center" isDisabled={administration?.pending || !administration?.onRefresh} onPress={() => administration?.onRefresh()}>Refresh authoritative state</Button></div></TableCard.Root>
  </aside>;
}
export function WorkerDetail({ id, observations, loading, diagnostics = null, nodes = null, nodeCommands = null, executions = null, projects = null, administration = null, readOnly = false, administrationHref = null, workersHref = '/workers', now = Date.now() }) {
  const worker = observations?.find(item => item.workerId === id);
  const node = nodes?.find(item => item.id === id && item.kind === 'worker');
  const items = workerExecutions(executions, id);
  const active = items.filter(item => ['Assigned', 'Running'].includes(item.state));
  const recent = items.filter(item => terminalStates.includes(item.state)).slice(0, 10);
  const commands = nodeCommands?.filter(command => command.request.nodeId === id) ?? null;
  const recentRows = recent.map(item => {
    const project = projects?.find(project => project.id === item.projectId);
    const url = issueLink(item.workReference, project?.repository);
    const identity = item.workReference?.type === 'github-issue' ? `Issue #${item.workReference.id}` : item.workReference?.id ?? 'Work';
    return {
      id: item.id,
      reference: <><p className="font-medium text-primary">{project?.name ?? item.projectId}</p>{url ? <a className="text-brand-secondary font-semibold" href={url} target="_blank" rel="noopener noreferrer">{identity}</a> : identity}<p><a href={'/executions/' + encodeURIComponent(item.id)}>Execution details</a></p></>,
      outcome: <><Status value={item.state} />{item.recoveryState && <p>Recovery: {item.recoveryState}</p>}</>,
      timing: <><p>Duration: {duration(item, now)}</p><p>{item.completedAtUtc ? 'Completed' : 'Requested'}: {timestamp(item.completedAtUtc ?? item.createdAtUtc)}</p></>
    };
  });
  return <section className="poc-card text-sm text-secondary" aria-label="Worker detail proof of concept">
    <PageHeading title={worker?.displayName || 'Worker details'} resourceId={id}
      breadcrumbs={[{ label: 'Workers', href: workersHref }, { label: 'Detail preview' }]}
      actions={<Button href={administrationHref ?? '/workers/' + encodeURIComponent(id)} color="secondary" size="sm">Open Worker detail and administration</Button>} />
    {!worker ? <p role="status">{loading ? 'Loading current Worker observations…' : observations ? 'Worker unavailable or deleted. Return to Workers to refresh the inventory.' : 'Current Worker observations unavailable. Sign in or refresh to recover current state.'}</p> : <div className={readOnly ? "poc-layout poc-read-only" : "poc-layout"}><div className="poc-main">
      <TableCard.Root><div className="p-5"><dl className="poc-status-grid">
        <Fact label="Connection"><Status value={worker.availability} />{node && <p>Node: {node.connectivity}</p>}</Fact>
        <Fact label="Lifecycle / readiness"><Status value={worker.lifecycleState} /><p>Execution prerequisites: <Status value={node?.executionReadiness ?? 'Unavailable'} /></p><p>Scheduling: {worker.schedulingPolicy ?? 'Unknown'}</p></Fact>
        <Fact label="Observation freshness"><Status value={node ? node.observationsStale ? 'Stale' : 'Current' : 'Unknown'} /><p>Last heartbeat: {timestamp(worker.lastHeartbeatAtUtc)}</p></Fact>
        <Fact label="Worker slots"><strong>{worker.activeExecutions ?? 'Unknown'} / {worker.maximumCapacity ?? worker.capacity ?? 'Unknown'} active</strong><p>{worker.availableCapacity ?? 'Unknown'} available · {worker.activeAssignments ?? 'Unknown'} active Server assignments</p></Fact>
      </dl>
      <p className="poc-muted mt-4">Connectivity, execution prerequisites and scheduling policy are separate observations. Stale evidence does not establish current readiness.</p></div></TableCard.Root>
      <section className="poc-section"><div className="mb-3 flex items-center gap-3"><FeaturedIcon icon={Activity} color="brand" theme="light" size="sm" /><h2 className="text-lg font-semibold text-primary">Current execution{active.length > 1 ? 's' : ''}</h2></div>
        {executions === null ? <p>Current execution data unavailable. Refresh to recover reported stages.</p> : active.length ? active.map(item => <Execution key={item.id} item={item} projects={projects} now={now} active />) : <p>No current execution in the latest 50 Server requests. {worker.activeAssignments > 0 || worker.activeExecutions > 0 ? 'The Worker reports active work; its execution detail is unavailable in this bounded view.' : 'No active work reported by this Worker.'}</p>}
      </section>
      <TableCard.Root size="sm"><TableCard.Header title="Recent executions" description="Up to 10 outcomes for this Worker from the latest 50 Server requests. Older or unreported executions may be absent." />
        {executions === null ? <p className="p-5">Execution history unavailable. Refresh to try again.</p> : recent.length ? <>
          <div className="hidden sm:block"><Table aria-label="Recent executions" size="sm"><Table.Header>
            <Table.Head id="work" isRowHeader label="Project / Issue" /><Table.Head id="outcome" label="Outcome" /><Table.Head id="time" label="Time" />
          </Table.Header><Table.Body>{recentRows.map(row => <Table.Row key={row.id} id={row.id}>
            <Table.Cell>{row.reference}</Table.Cell><Table.Cell>{row.outcome}</Table.Cell><Table.Cell>{row.timing}</Table.Cell>
          </Table.Row>)}</Table.Body></Table></div>
          <div className="divide-y divide-secondary sm:hidden">{recentRows.map(row => <article key={row.id} className="p-4">
            <div className="flex items-start justify-between gap-3"><div>{row.reference}</div><div>{row.outcome}</div></div>
            <div className="mt-3 text-sm text-tertiary">{row.timing}</div>
          </article>)}</div>
        </> : <p className="p-5">No recent terminal executions for this Worker in this view.</p>}
      </TableCard.Root>
      <section className="poc-section"><h2 className="mb-3 text-lg font-semibold text-primary">Capabilities and provisioning</h2><p>Provisioning readiness: <Status value={node?.provisioningReadiness ?? 'Unavailable'} /> · Latest operation: <Status value={diagnostics?.provisioningState ?? 'Unavailable'} /></p>
        {diagnostics ? <><p>Reported readiness evidence: Codex preflight {diagnostics.aiAgentReady ? 'present' : 'absent'} · GitHub access {diagnostics.gitHubReady ? 'present' : 'absent'} · Git access {diagnostics.gitReady ? 'present' : 'absent'}</p><p>Configuration synchronization: <Status value={diagnostics.configurationSynchronization} /></p>{diagnostics.latestProvisioningOperation && <p>{diagnostics.latestProvisioningOperation.action} · {diagnostics.latestProvisioningOperation.status}</p>}</> : <p>Worker readiness diagnostics unavailable.</p>}
        {node ? node.capabilities?.length ? <div className="poc-capabilities mt-4">{node.capabilities.map(capability => <Capability key={capability.definition.id} capability={capability} commands={commands} />)}</div> : <p>No capabilities reported.</p> : <p>Capability observations unavailable. Refresh provisioning state.</p>}
      </section>
    </div>{!readOnly && <ControlRail administration={administration} />}</div>}
  </section>;
}
