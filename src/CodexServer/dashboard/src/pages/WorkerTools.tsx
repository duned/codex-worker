import { useEffect, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead } from '../shared/api/session';
import { commands as validateCommands } from '../shared/api/validation';
import type { Capability, NodeCommandSummary, NodeSummary } from '../shared/api/contracts';
import { t, tryT, useLanguage } from '../shared/i18n';
import { commandActive, deviceLoginUrl, offeredToolActions, toolActions, toolCommand, type ToolAction } from '../shared/model';
import { Ago, Card, Check, Empty, Pill, ReadState, Rows } from '../shared/ui';

type Selection = { capabilityId: string; action: ToolAction };

/** A Worker's tools with the typed provisioning actions its node advertises (sign in, check, install, update). */
export function WorkerTools({ node, nodes, online }: {
  node?: NodeSummary; nodes: { loading: boolean; hasError: boolean; error?: string; refetch(): unknown }; online: boolean;
}) {
  useLanguage();
  const nodeId = node?.id ?? '';
  const commandsPath = `/api/v1/nodes/${encodeURIComponent(nodeId)}/commands`;
  const commands = useApiRead(commandsPath, validateCommands, !!node);
  const action = useAction();
  const [selected, setSelected] = useState<Selection>(), [elevation, setElevation] = useState(false);
  const anyActive = (commands.data ?? []).some(commandActive);
  const refetchCommands = commands.refetch, refetchNodes = nodes.refetch;

  // Command progress (including a device code) is not covered by Server freshness markers; follow it while active.
  useEffect(() => {
    if (!anyActive) return;
    const id = setInterval(() => { void refetchCommands(); void refetchNodes(); }, 3000);
    return () => clearInterval(id);
  }, [anyActive, refetchCommands, refetchNodes]);

  function choose(value?: Selection) { setElevation(false); setSelected(value); }
  async function submit(selection: Selection) {
    const spec = toolActions[selection.action];
    const done = await action.run(`node-${nodeId}`, '/api/v1/provisioning/commands', 'POST', {
      nodeId, capabilityId: selection.capabilityId, action: spec.command, timeoutSeconds: spec.timeoutSeconds,
      allowElevation: spec.elevation && elevation
    }, 'tools.requested');
    if (done) choose(undefined);
    await refetchCommands();
  }

  function row(capability: Capability) {
    const { latest, busy } = toolCommand(commands.data ?? [], nodeId, capability.definition.id);
    const ok = capability.state.installation === 'Installed' && capability.state.health === 'Healthy' && capability.state.authentication !== 'Required';
    const missing = capability.state.installation !== 'Installed';
    const offered = offeredToolActions(capability);
    const choosing = selected?.capabilityId === capability.definition.id ? selected : undefined;
    return <div key={capability.definition.id} className="flex flex-col gap-3 px-5 py-4">
      <div className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-3">
        <span className="font-medium text-primary">{capability.definition.displayName}</span>
        <span className="text-sm text-tertiary tabular-nums">{capability.state.detectedVersion ?? '—'}</span>
        <Pill tone={ok ? 'ok' : missing ? 'idle' : 'bad'}>{t(ok ? 'workers.toolReady' : missing ? 'workers.toolMissing' : capability.state.authentication === 'Required' ? 'workers.toolSignIn' : 'workers.toolProblem')}</Pill>
      </div>
      {latest && <CommandState command={latest} />}
      {offered.length > 0 && !choosing && <div className="flex flex-wrap gap-2">
        {offered.map(name => <Button key={name} size="sm" color="secondary" isDisabled={!online || busy || !!action.busy || !commands.data}
          onClick={() => choose({ capabilityId: capability.definition.id, action: name })}>{t(toolActions[name].label)}</Button>)}
      </div>}
      {choosing && <div className="rounded-lg bg-secondary p-4">
        <p className="text-sm text-secondary">{t(choosing.action === 'login' ? 'tools.confirmLogin' : 'tools.confirm', { action: t(toolActions[choosing.action].label), tool: capability.definition.displayName })}
          {choosing.action === 'login' && capability.definition.id === 'github-cli' && <> {t('tools.confirmLoginGitHub')}</>}</p>
        {toolActions[choosing.action].elevation && <div className="mt-3"><Check checked={elevation} onChange={setElevation}>{t('tools.elevation')}</Check></div>}
        <div className="mt-3 flex flex-wrap gap-2">
          <Button size="sm" color="primary" isDisabled={!!action.busy || (toolActions[choosing.action].elevation && !elevation)}
            onClick={() => void submit(choosing)}>{t('tools.run')}</Button>
          <Button size="sm" color="secondary" onClick={() => choose(undefined)}>{t('common.cancel')}</Button>
        </div>
      </div>}
    </div>;
  }

  return <Card title={t('workers.tools')} flush>
    <ReadState read={nodes}><Rows>
      {node?.capabilities.length ? node.capabilities.map(row) : <Empty>{t('workers.noTools')}</Empty>}
    </Rows></ReadState>
    {node && <p className="border-t border-secondary px-5 py-3 text-xs text-tertiary">{t(online ? 'tools.policyHint' : 'tools.offline')}</p>}
  </Card>;
}

function CommandState({ command }: { command: NodeCommandSummary }) {
  const active = commandActive(command);
  const label = tryT(`tools.action.${command.request.action}`, command.request.action);
  const login = command.loginInstructions, loginUrl = login && deviceLoginUrl(login.verificationUri);
  const deadline = Date.parse(command.deadlineUtc ?? '');
  const showCode = command.status === 'Running' && !!login && (!Number.isFinite(deadline) || deadline > Date.now());
  const tone = active ? 'run' : command.status === 'Succeeded' ? 'ok' : command.status === 'Cancelled' ? 'idle' : 'bad';
  const failure = command.failureDetail?.description ?? (command.status === 'Failed' || command.status === 'TimedOut'
    ? tryT(`tools.diagnostic.${command.diagnostic ?? ''}`, command.diagnostic ?? '') || undefined : undefined);
  return <div className="flex flex-col gap-2 text-sm">
    <p className="text-secondary"><Pill tone={tone}>{tryT(`tools.status.${command.status}`, command.status)}</Pill>{' '}{label}
      {' · '}<Ago value={command.completedAtUtc ?? command.startedAtUtc ?? command.createdAtUtc} /></p>
    {showCode && <div className="rounded-lg bg-secondary p-3" aria-live="polite">
      <p className="text-primary">{t('tools.deviceCode')}</p>
      <p className="mt-2 flex flex-wrap items-center gap-3">
        <strong className="font-mono text-lg tracking-widest text-primary">{login.userCode}</strong>
        {loginUrl ? <a className="font-semibold text-brand-secondary underline" href={loginUrl} target="_blank" rel="noreferrer noopener">{loginUrl}</a>
          : <span className="text-tertiary">{login.verificationUri}</span>}
      </p>
      {Number.isFinite(deadline) && <p className="mt-2 text-xs text-tertiary">{t('tools.deviceCodeExpires', { time: new Date(deadline).toLocaleTimeString() })}</p>}
    </div>}
    {active && !showCode && command.request.action === 'Login' && <p className="text-tertiary">{t('tools.waitingCode')}</p>}
    {failure && <p className="text-utility-red-500">{failure}</p>}
  </div>;
}
