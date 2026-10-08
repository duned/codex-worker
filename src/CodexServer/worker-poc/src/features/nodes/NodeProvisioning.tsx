import { timestamp } from '../../model';
import { ExternalLink } from '../../shared/Actions';
import { useEffect, useState } from 'react';
import { ActionDialog } from '../../shared/Dialogs';
import { AdvancedDisclosure, Notice } from '../../shared/Presentation';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { command as validateCommand, commands as validateCommands, nodes as validateNodes } from '../../shared/api/validation';
import { ApiError } from '../../shared/api/client';
import type { Capability, NodeCommandSummary } from '../../shared/api/contracts';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../untitled/components/base/input/input';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { CapabilityCard } from '../../detail.jsx';
import { commandActive, commandExpired, deviceLoginUrl, nodeActions, provisioningReason } from './provisioning';
type Selection = { capabilityId: string; action: string } | { command: NodeCommandSummary; action: 'cancel' | 'reconcile' };
/** Node-scoped operations and capability views are shared with Settings. */
export function NodeProvisioning({ nodeId, repository = '' }: { nodeId: string; repository?: string }) {
  const runtime = useRuntime(), session = useSession();
  const nodes = useApiRead('/api/v1/nodes', validateNodes), path = `/api/v1/nodes/${encodeURIComponent(nodeId)}/commands`;
  const commands = useApiRead(path, validateCommands), resource = path;
  const node = nodes.data?.find(item => item.id === nodeId);
  const [selected, setSelected] = useState<Selection>(), [message, setMessage] = useState(''), [pending, setPending] = useState(false);
  const [elevation, setElevation] = useState(false), [quiescent, setQuiescent] = useState(false), [targetRepository, setRepository] = useState(repository);
  const locked = runtime.locked(resource);
  const [now, setNow] = useState(Date.now());
  // Remove short-lived device instructions at their deadline even if an
  // unchanged poll does not render new query data. This timer never submits.
  useEffect(() => {
    const deadline = Math.min(...(commands.data ?? []).filter(commandActive).map(item => Date.parse(item.deadlineUtc ?? '')).filter(value => value > Date.now()));
    if (!Number.isFinite(deadline)) return;
    const timer = setTimeout(() => setNow(Date.now()), Math.min(2147483647, Math.max(1, deadline - Date.now())));
    return () => clearTimeout(timer);
  }, [commands.data, now]);
  function choose(value: Selection) { setElevation(false); setQuiescent(false); setRepository(repository); setSelected(value); }
  async function refresh() {
    if (pending) return;
    const generation = session.generation;
    setPending(true);
    try {
      await runtime.reconcile(resource, async signal => {
        await runtime.read('/api/v1/nodes', signal, validateNodes);
        await runtime.read(path, signal, validateCommands);
      });
      await Promise.all([nodes.refetch(), commands.refetch()]);
      if (runtime.snapshot().generation === generation) setMessage('Authoritative operation state refreshed. Review retained operations before retrying.');
    } catch { if (runtime.snapshot().generation === generation) setMessage('Operation state unavailable. No command was resubmitted.'); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  async function submit() {
    if (!selected || pending || locked) return;
    const selection = selected, generation = session.generation;
    setPending(true);
    try {
      const spec = 'capabilityId' in selection ? nodeActions[selection.action] : undefined;
      const endpoint = 'command' in selection
        ? `/api/v1/provisioning/commands/${encodeURIComponent(selection.command.id)}/${selection.action}${selection.action === 'reconcile' ? '?nodeQuiescent=true' : ''}`
        : '/api/v1/provisioning/commands';
      const body = 'capabilityId' in selection ? { nodeId, capabilityId: selection.capabilityId, action: spec?.command,
        timeoutSeconds: selection.action === 'login' ? 600 : 120, allowElevation: !!spec?.elevation && elevation,
        ...(selection.action === 'verifyrepositoryaccess' ? { repository: targetRepository.trim() } : {}) } : undefined;
      await runtime.mutate(resource, endpoint, 'POST', body, validateCommand, async signal => {
        const inventory = await runtime.read('/api/v1/nodes', signal, validateNodes);
        const retained = await runtime.read(path, signal, validateCommands);
        if ('capabilityId' in selection) {
          const reason = provisioningReason(inventory.find(item => item.id === nodeId), retained, selection.capabilityId, selection.action);
          if (reason) throw new ApiError(reason);
          if (spec?.elevation && !elevation) throw new ApiError('Explicit elevation consent required.');
          if (selection.action === 'verifyrepositoryaccess' && !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(targetRepository.trim())) throw new ApiError('Enter a GitHub owner/repository.');
        } else {
          const current = retained.find(item => item.id === selection.command.id);
          if (!current || current.request.nodeId !== nodeId || (selection.action === 'cancel' ? current.status !== 'Pending' : !quiescent || !commandExpired(current, Date.now()))) throw new ApiError('Current command state or explicit node quiescence confirmation required.');
        }
      });
      if (runtime.snapshot().generation === generation) setMessage('Operation accepted. Retained progress is refreshed automatically; leaving or reloading does not resubmit.');
    } catch (error) { if (runtime.snapshot().generation === generation) setMessage(error instanceof ApiError ? error.message : 'Operation result unavailable. Refresh authoritative operation state before retrying.'); throw error; }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  function controls(capability: Capability) {
    const matching = commands.data?.filter(item => item.request.nodeId === nodeId && item.request.capabilityId === capability.definition.id) ?? [];
    const actions = capability.availableActions.filter(action => Object.hasOwn(nodeActions, action));
    return <div className="mt-4 space-y-3">
      {matching.filter(commandActive).map(item => {
        const expired = commandExpired(item, Date.now()), loginUrl = item.loginInstructions && deviceLoginUrl(item.loginInstructions.verificationUri);
        return <div key={item.id} className="space-y-2"><p>{item.request.action}: {item.status}{expired && ' · Deadline expired; confirm the node operation stopped before reconciliation.'}</p>
          {!locked && item.status === 'Running' && !expired && Date.parse(item.deadlineUtc ?? '') > Date.now() && loginUrl && <p>Only approve the login you started on this node. Open <ExternalLink href={loginUrl}>device login</ExternalLink> and enter <strong>{item.loginInstructions?.userCode}</strong>. Expires {item.deadlineUtc}.</p>}
          {(item.status === 'Pending' || expired) && <Button color="secondary" isDisabled={pending || locked || !commands.data || !node} onPress={() => choose({ command: item, action: expired ? 'reconcile' : 'cancel' })}>{expired ? 'Reconcile after node quiescence' : 'Cancel queued operation'}</Button>}
        </div>;
      })}
      <p>Node-local permission is required.</p>
      <AdvancedDisclosure title="Local preparation and authorization"><p>Server permission cannot override local policy. Authentication must use the node service account; login alone does not prove execution readiness. Re-detect after local preparation. A Denied command requires the node administrator to permit that typed action or complete preparation locally.</p></AdvancedDisclosure>
      <div className="flex flex-wrap gap-2">{actions.map(action => <Button key={action} color="secondary" size="sm" isDisabled={pending || locked || !!provisioningReason(node, commands.data, capability.definition.id, action)} onPress={() => choose({ capabilityId: capability.definition.id, action })}>{nodeActions[action].label}</Button>)}</div>
      {!actions.length && <p>No supported remote actions available. Prepare this tool in the node service account or ask its administrator to permit the typed action. Project runtimes outside the catalog require local preparation.</p>}
      <AdvancedDisclosure title="Operation history and public SSH identities">{matching.length ? matching.map(item => <div key={item.id} className="break-all"><p>{item.request.action} · {item.status} · {item.diagnostic ?? 'No diagnostic reported'}</p><p>ID {item.id} · {timestamp(item.createdAtUtc)}</p>{item.publicIdentity && <pre className="whitespace-pre-wrap">{item.publicIdentity.publicKey}{'\n'}{item.publicIdentity.fingerprint}</pre>}</div>) : <p>No commands reported in bounded node history.</p>}</AdvancedDisclosure>
    </div>;
  }
  const spec = selected && 'capabilityId' in selected ? nodeActions[selected.action] : undefined;
  return <section aria-label="Node provisioning" className="space-y-4">
    {(nodes.error || commands.error) && <Notice error>Capability or operation state unavailable. Refresh before another action.</Notice>}
    {message && <Notice>{message}</Notice>}
    <Button color="secondary" isDisabled={pending} onPress={() => { void refresh(); }}>Refresh authoritative operation state</Button>
    {node ? node.capabilities.length ? <div className="poc-capabilities">{node.capabilities.map(capability => <CapabilityCard key={capability.definition.id} capability={capability} commands={commands.data ?? null}>{controls(capability)}</CapabilityCard>)}</div> : <Notice>No capabilities reported.</Notice> : <Notice>Capability observations unavailable.</Notice>}
    <ActionDialog key={selected ? JSON.stringify(selected) : 'closed'} isOpen={!!selected} onClose={() => { setSelected(undefined); setElevation(false); setQuiescent(false); }}
      title={spec?.label ?? (selected?.action === 'cancel' ? 'Cancel queued operation' : 'Reconcile after node quiescence')} actionLabel="Confirm" destructive={!!spec?.destructive || selected?.action === 'reconcile'} disabled={pending || locked || (!!spec?.elevation && !elevation) || (selected?.action === 'reconcile' && !quiescent)} onSubmit={submit}
      description={selected?.action === 'reconcile' ? 'Release the retained operation lock only after independently verifying the node is quiescent and its mutation has stopped.' : selected?.action === 'cancel' ? 'Cancel this queued command. Running mutations are not cancelled.' : 'Run only this advertised typed action. Node-local permission and service-account context are required. Removing tools or authentication may prevent execution; provider-side authorization and SSH registrations are separate.'}>
      {spec?.elevation && <Checkbox label="Authorize elevation for this action" isSelected={elevation} onChange={setElevation} />}
      {selected?.action === 'reconcile' && <Checkbox label="I verified the node is quiescent and the mutation has stopped" isSelected={quiescent} onChange={setQuiescent} />}
      {selected?.action === 'verifyrepositoryaccess' && <Input label="GitHub repository (owner/repository)" value={targetRepository} onChange={setRepository} isRequired hint="Read verification does not prove push permission and never performs a test push." />}
    </ActionDialog>
  </section>;
}
