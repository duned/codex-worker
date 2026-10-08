import { ExternalLink } from '../../shared/Actions';
import { useEffect, useState } from 'react';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { command, commands, nodes, serverGitHubConnection } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { serverGitHubReadiness } from '../server/readiness';
import { useServerConnection } from '../server/useServerConnection';
import { connectionPath } from '../server/connection';
import { activeCommand, expiredCommand, nodeActions } from '../provisioning/actions';
import { Capability } from '../../detail.jsx';
import { ActionDialog } from '../../shared/Dialogs';
import { Notice, AdvancedDisclosure, StatusBadge } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { Input } from '../../untitled/components/base/input/input';
import type { NodeCommandSummary } from '../../shared/api/contracts';
type Intent = { capabilityId: string; action: string } | { command: NodeCommandSummary; control: 'cancel' | 'reconcile' };
const fence = 'node:server';
function serverCommands(value: unknown) {
  const items = commands(value);
  if (items.some(c => c.request.nodeId !== 'server')) throw Error('Unexpected node history.');
  return items;
}
export function ServerPreparation() {
  const runtime = useRuntime(), session = useSession();
  const inventory = useApiRead('/api/v1/nodes', nodes);
  const history = useApiRead('/api/v1/nodes/server/commands', serverCommands);
  const connection = useServerConnection();
  const node = inventory.data?.find(n => n.id === 'server' && n.kind === 'server');
  const github = node?.capabilities.find(c => c.definition.id === 'github-cli');
  const readiness = serverGitHubReadiness(node, connection.data);
  const [intent, setIntent] = useState<Intent>();
  const [consent, setConsent] = useState(false), [elevation, setElevation] = useState(false), [quiescent, setQuiescent] = useState(false);
  const [repository, setRepository] = useState(''), [message, setMessage] = useState('');
  const [now, setNow] = useState(Date.now());
  const latest = connection.data?.commands.find(activeCommand) ?? connection.data?.commands[0];
  const allCommands = history.data;
  const busy = runtime.locked(fence) || !node || !allCommands || !connection.data || node.provisioningReadiness === 'busy' || allCommands.some(activeCommand) || !!connection.data.commands.some(activeCommand);
  const deadline = allCommands?.filter(activeCommand).map(c => Date.parse(c.deadlineUtc ?? '')).filter(t => Number.isFinite(t) && t > now).sort((a,b) => a-b)[0];
  useEffect(() => { if (!deadline) return; const timer = setTimeout(() => { setNow(Date.now()); setConsent(false); setElevation(false); setQuiescent(false); }, Math.max(1, deadline - Date.now())); return () => clearTimeout(timer); }, [deadline]);
  function close() { setIntent(undefined); setConsent(false); setElevation(false); setQuiescent(false); setRepository(''); }
  async function fresh(signal: AbortSignal) {
    const [currentNodes, currentCommands, currentConnection] = await Promise.all([
      runtime.read('/api/v1/nodes', signal, nodes), runtime.read('/api/v1/nodes/server/commands', signal, serverCommands), runtime.read(connectionPath, signal, serverGitHubConnection)
    ]);
    const current = currentNodes.find(n => n.id === 'server' && n.kind === 'server');
    if (!current) throw Error('Server observations unavailable.');
    return { node: current, commands: currentCommands, connection: currentConnection };
  }
  async function submit() {
    if (!intent) return;
    const control = 'control' in intent;
    const spec = !control ? nodeActions[intent.action] : undefined;
    const path = control ? `/api/v1/provisioning/commands/${encodeURIComponent(intent.command.id)}/${intent.control}${intent.control === 'reconcile' ? '?nodeQuiescent=true' : ''}` : '/api/v1/provisioning/commands';
    const body = control ? undefined : { nodeId: 'server', capabilityId: intent.capabilityId, action: spec?.action, timeoutSeconds: spec?.action === 'Login' ? 600 : 120,
      allowElevation: !!spec?.elevation && elevation, ...(spec?.action === 'VerifyRepositoryAccess' ? { repository } : {}) };
    try {
      await runtime.mutate(fence, path, 'POST', body, value => {
        const result = command(value);
        if (result.request.nodeId !== 'server' || (control ? result.id !== intent.command.id
          : result.request.capabilityId !== intent.capabilityId || result.request.action !== spec?.action)) throw Error('Unexpected operation confirmation.');
        return result;
      }, async signal => {
        const current = await fresh(signal);
        if (control) {
          const operation = current.commands.find(c => c.id === intent.command.id);
          if (!operation || (intent.control === 'cancel' ? operation.status !== 'Pending' : !quiescent || !expiredCommand(operation, Date.now()))) throw Error('Operation changed; refresh before continuing.');
        } else {
          const capability = current.node.capabilities.find(c => c.definition.id === intent.capabilityId);
          if (!spec || !capability?.availableActions.includes(intent.action) || current.node.provisioningReadiness === 'busy' || current.commands.some(activeCommand) || current.connection.commands.some(activeCommand)) throw Error('Action unavailable or node busy.');
          if (spec.elevation && (!elevation || !current.connection.elevationAllowed)) throw Error('Elevation requires explicit consent and local permission.');
          if (['Login', 'PrepareAuthentication', 'Install', 'Update', 'Uninstall', 'Configure', 'Logout', 'GenerateSshKey', 'RemoveSshKey'].includes(spec.action) && !current.connection.provisioningEnabled) throw Error('Local provisioning disabled.');
          if (['Login', 'PrepareAuthentication'].includes(spec.action) && !consent) throw Error('Explicit service-account authorization required.');
        }
      });
      setMessage('Operation confirmed. Current authentication evidence determines connection success.'); close();
    } catch { setMessage('Operation could not be confirmed. Refresh authoritative state to recover it before retrying.'); throw Error('Operation unavailable.'); }
  }
  async function refresh() {
    try {
      await runtime.reconcile(fence, async signal => { await fresh(signal); });
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(session.generation) });
      setMessage('Authoritative state refreshed. Retained operations must finish or be reconciled before retrying.');
    } catch { setMessage('Authoritative state unavailable. Operation lock retained.'); }
  }
  function offer(action: string, label: string) { return github?.availableActions.includes(action) && <Button key={action} color="secondary" isDisabled={busy} onPress={() => setIntent({ capabilityId: 'github-cli', action })}>{label}</Button>; }
  const prepared = latest?.status === 'Succeeded' && latest.request.action === 'PrepareAuthentication' || latest?.request.action === 'Login' && latest.diagnostic !== 'Denied';
  return <div className="space-y-6">
    <section aria-label="Server GitHub connection" className="space-y-4 rounded-xl border border-secondary bg-primary p-5">
      <h2 className="text-lg font-semibold text-primary">Server GitHub connection</h2>
      <StatusBadge tone={readiness.tone}>{readiness.complete ? 'Connected' : readiness.detail}</StatusBadge>
      <p className="text-sm text-secondary">Authentication uses the dedicated Server service account. Repository reads and Issue writes are checked in Projects; Worker login and repository push permission are separate.</p>
      {[inventory.error, history.error, connection.error].filter(Boolean).map((error, i) => <Notice error key={i}>{error}</Notice>)}
      {latest && <p className="text-sm text-secondary">{latest.request.action} · {latest.status}{activeCommand(latest) && ' · You may leave and return to recover this operation.'}</p>}
      {connection.challenge && <Notice>Open <ExternalLink className="underline" href="https://github.com/login/device">GitHub verification</ExternalLink> and enter <strong>{connection.challenge.userCode}</strong>. Only approve the login you started. Waiting for verification; expires {new Date(connection.challenge.deadline).toLocaleString()}.</Notice>}
      {latest && expiredCommand(latest, Math.max(now, Date.now())) && <Notice>Device login deadline expired. The code is no longer available. Verify the Server is quiescent before releasing its operation lock.</Notice>}
      <div className="flex flex-wrap gap-3">
        <Button color="secondary" onPress={() => { void refresh(); }}>Refresh authoritative state</Button>
        {offer('checkauthentication', 'Check Server authentication')}
        {!readiness.complete && connection.data?.provisioningEnabled && (github?.state.installation !== 'Installed'
          ? connection.data.elevationAllowed && offer('install', 'Install GitHub CLI')
          : prepared ? offer('login', 'Start device login') : offer('prepareauthentication', 'Connect · prepare authentication'))}
      </div>
      {connection.data && !connection.data.provisioningEnabled && <Notice>Local provisioning is disabled. Ask the Server administrator to enable service-account preparation. Authentication checks remain available.</Notice>}
      {!readiness.complete && github?.state.installation !== 'Installed' && connection.data && !connection.data.elevationAllowed && <Notice>Installation elevation is unavailable under local policy. Ask the Server administrator to install GitHub CLI, then refresh.</Notice>}
      {!readiness.complete && github?.state.installation === 'Installed' && (!github.availableActions.includes('login') || !github.availableActions.includes('prepareauthentication')) && <Notice>The supported device flow is unavailable. Ask the Server administrator to inspect service-account configuration and local provisioning policy.</Notice>}
      {!readiness.complete && <p className="text-sm text-tertiary">Preparation creates private product-managed GitHub configuration and refuses unrelated operator authentication. Installation requires separate elevation consent and node-local permission.</p>}
    </section>
    <section className="space-y-4"><h2 className="text-lg font-semibold text-primary">Server capabilities</h2>
      {node ? <><p className="text-sm text-secondary">{node.displayName ?? 'Server'} · {node.connectivity} · {node.observationsStale ? 'Stale observations' : 'Current observations'}</p>
        <AdvancedDisclosure title="Advanced capabilities and typed actions"><p>Service health: {node.health ?? 'Unavailable'} · Execution readiness: {node.executionReadiness} · Provisioning readiness: {node.provisioningReadiness ?? 'Unavailable'}</p><div className="grid gap-4 lg:grid-cols-2">{node.capabilities.map(capability => <div key={capability.definition.id} className="min-w-0 space-y-3">
          <Capability capability={capability} commands={allCommands ?? null} />
          <div className="flex flex-wrap gap-2">{capability.availableActions.filter(action => Object.hasOwn(nodeActions, action)).map(action => <Button key={action} color="secondary" isDisabled={busy} onPress={() => setIntent({ capabilityId: capability.definition.id, action })}>{nodeActions[action].label}</Button>)}</div>
        </div>)}</div></AdvancedDisclosure></> : <Notice>Server capabilities unavailable.</Notice>}
      {allCommands?.filter(c => activeCommand(c)).map(c => <div key={c.id} className="space-y-2"><StatusBadge tone="warning">{c.request.action} · {c.status}</StatusBadge>
        {(c.status === 'Pending' || expiredCommand(c, Math.max(now, Date.now()))) && <Button color="secondary" isDisabled={runtime.locked(fence)} onPress={() => setIntent({ command: c, control: c.status === 'Pending' ? 'cancel' : 'reconcile' })}>{c.status === 'Pending' ? 'Cancel queued operation' : 'Reconcile after node quiescence'}</Button>}
      </div>)}
      <AdvancedDisclosure title="Advanced operation history"><p>Bounded Server history; active operations are retained. No total-history count is available.</p>{allCommands?.length ? allCommands.map(c => <div key={c.id} className="break-all border-t border-secondary py-3"><p>{c.request.capabilityId} · {c.request.action} · {c.status}</p><p>ID {c.id} · queued {c.createdAtUtc} · started {c.startedAtUtc ?? 'Not reported'} · deadline {c.deadlineUtc ?? 'Not reported'} · completed {c.completedAtUtc ?? 'Not reported'} · {c.diagnostic ?? 'No diagnostic'}</p>{c.failureDetail && <p>{c.failureDetail.description}</p>}{c.publicIdentity && <pre className="whitespace-pre-wrap break-all">{c.publicIdentity.publicKey}\n{c.publicIdentity.fingerprint}</pre>}</div>) : <p>No operation history available.</p>}</AdvancedDisclosure>
    </section>
    {message && <Notice>{message}</Notice>}
    {intent && <ActionDialog isOpen title={'control' in intent ? intent.control === 'cancel' ? 'Cancel queued operation' : 'Reconcile after node quiescence' : nodeActions[intent.action].label}
      description={'control' in intent ? 'Running operations stop at their deadline. Reconciliation releases the command lock only after you verify that the node mutation has stopped.' : 'Only this advertised typed action is submitted. Server authorization does not override node-local policy. No shell command or secret is accepted.'}
      actionLabel="Confirm operation" onClose={close} onSubmit={submit}
      disabled={'control' in intent ? intent.control === 'reconcile' && !quiescent : (!!nodeActions[intent.action].elevation && !elevation) || (['login', 'prepareauthentication'].includes(intent.action) && !consent) || (intent.action === 'verifyrepositoryaccess' && !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_.-]{1,100}$/.test(repository))}
      destructive={'control' in intent || !!nodeActions['action' in intent ? intent.action : '']?.destructive}>
      {'control' in intent ? intent.control === 'reconcile' && <Checkbox label="I verified the Server is quiescent and the mutation has stopped" isSelected={quiescent} onChange={setQuiescent} /> : <>
        {['login', 'prepareauthentication'].includes(intent.action) && <Checkbox label="Authorize preparation and device login in the Server service account" isSelected={consent} onChange={setConsent} />}
        {nodeActions[intent.action].elevation && <Checkbox label="Authorize installation/configuration elevation under local policy" isSelected={elevation} onChange={setElevation} />}
        {intent.action === 'verifyrepositoryaccess' && <Input label="Repository (owner/repository)" value={repository} onChange={setRepository} maxLength={140} />}
      </>}
    </ActionDialog>}
  </div>;
}
