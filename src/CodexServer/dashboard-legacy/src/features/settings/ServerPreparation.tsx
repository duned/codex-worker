import { t, useLanguage, localizeText } from '../../shared/i18n';
import { timestamp } from '../../model';
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
import { Input } from '../../shared/Input';
import type { NodeCommandSummary } from '../../shared/api/contracts';
type Intent = { capabilityId: string; action: string } | { command: NodeCommandSummary; control: 'cancel' | 'reconcile' };
const fence = 'node:server';
function serverCommands(value: unknown) {
  const items = commands(value);
  if (items.some(c => c.request.nodeId !== 'server')) throw Error(t("settings.unexpectedNodeHistory"));
  return items;
}
export function ServerPreparation() {
  useLanguage();
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
    if (!current) throw Error(t("settings.serverObservationsUnavailable"));
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
          : result.request.capabilityId !== intent.capabilityId || result.request.action !== spec?.action)) throw Error(t("settings.unexpectedOperationConfirmation"));
        return result;
      }, async signal => {
        const current = await fresh(signal);
        if (control) {
          const operation = current.commands.find(c => c.id === intent.command.id);
          if (!operation || (intent.control === 'cancel' ? operation.status !== 'Pending' : !quiescent || !expiredCommand(operation, Date.now()))) throw Error(t("settings.operationChangedRefreshBeforeContinuing"));
        } else {
          const capability = current.node.capabilities.find(c => c.definition.id === intent.capabilityId);
          if (!spec || !capability?.availableActions.includes(intent.action) || current.node.provisioningReadiness === 'busy' || current.commands.some(activeCommand) || current.connection.commands.some(activeCommand)) throw Error(t("settings.actionUnavailableOrNodeBusy"));
          if (spec.elevation && (!elevation || !current.connection.elevationAllowed)) throw Error(t("settings.elevationRequiresExplicitConsentAndLocalPermission"));
          if (['Login', "PrepareAuthentication", 'Install', 'Update', 'Uninstall', 'Configure', 'Logout', 'GenerateSshKey', 'RemoveSshKey'].includes(spec.action) && !current.connection.provisioningEnabled) throw Error(t("settings.localProvisioningDisabled"));
          if (['Login', "PrepareAuthentication"].includes(spec.action) && !consent) throw Error(t("settings.explicitServiceAccountAuthorizationRequired"));
        }
      });
      setMessage(t("settings.operationConfirmedCurrentAuthenticationEvidenceDeterminesConnectionSuccess")); close();
    } catch { setMessage(t("settings.operationCouldNotBeConfirmedRefreshAuthoritativeStateToRecoverItBefore")); throw Error(t("settings.operationUnavailable")); }
  }
  async function refresh() {
    try {
      await runtime.reconcile(fence, async signal => { await fresh(signal); });
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(session.generation) });
      setMessage(t("settings.authoritativeStateRefreshedRetainedOperationsMustFinishOrBeReconciledBeforeRetrying"));
    } catch { setMessage(t("settings.authoritativeStateUnavailableOperationLockRetained")); }
  }
  function offer(action: string, label: string) { return github?.availableActions.includes(action) && <Button key={action} color="secondary" isDisabled={busy} onPress={() => setIntent({ capabilityId: 'github-cli', action })}>{label}</Button>; }
  const prepared = latest?.status === 'Succeeded' && latest.request.action === 'PrepareAuthentication' || latest?.request.action === 'Login' && latest.diagnostic !== 'Denied';
  return <div className="space-y-6">
    <section aria-label={t("settings.serverGitHubConnection")} className="space-y-4 rounded-xl border border-secondary bg-primary p-5">
      <h2 className="text-lg font-semibold text-primary">{t("settings.serverGitHubConnection")}</h2>
      <StatusBadge tone={readiness.tone}>{readiness.complete ? 'Connected' : readiness.detail}</StatusBadge>
      <p className="text-sm text-secondary">{t("settings.authenticationUsesTheDedicatedServerServiceAccountRepositoryReadsAndIssueWrites")}</p>
      {[inventory.error, history.error, connection.error].filter(Boolean).map((error, i) => <Notice error key={i}>{error}</Notice>)}
      {latest && <p className="text-sm text-secondary">{localizeText(latest.request.action)} · {localizeText(latest.status)}{activeCommand(latest) && ' · You may leave and return to recover this operation.'}</p>}
      {connection.challenge && <Notice>{t("settings.open")}{' '}<ExternalLink className="underline" href="https://github.com/login/device">{t("settings.gitHubVerification")}</ExternalLink>{' '}{t("settings.andEnter")}{' '}<strong>{connection.challenge.userCode}</strong>{t("settings.onlyApproveTheLoginYouStartedWaitingForVerificationExpires")}{' '}{timestamp(new Date(connection.challenge.deadline).toISOString())}.</Notice>}
      {latest && expiredCommand(latest, Math.max(now, Date.now())) && <Notice>{t("settings.deviceLoginDeadlineExpiredTheCodeIsNoLongerAvailableVerifyThe")}</Notice>}
      <div className="flex flex-wrap gap-3">
        <Button color="secondary" onPress={() => { void refresh(); }}>{t("settings.refreshAuthoritativeState")}</Button>
        {offer('checkauthentication', t("settings.checkServerAuthentication"))}
        {!readiness.complete && connection.data?.provisioningEnabled && (github?.state.installation !== 'Installed'
          ? connection.data.elevationAllowed && offer('install', t("settings.installGitHubCLI"))
          : prepared ? offer('login', t("settings.startDeviceLogin")) : offer('prepareauthentication', t("settings.connectPrepareAuthentication")))}
      </div>
      {connection.data && !connection.data.provisioningEnabled && <Notice>{t("settings.localProvisioningIsDisabledAskTheServerAdministratorToEnableServiceAccount")}</Notice>}
      {!readiness.complete && github?.state.installation !== 'Installed' && connection.data && !connection.data.elevationAllowed && <Notice>{t("settings.installationElevationIsUnavailableUnderLocalPolicyAskTheServerAdministratorTo")}</Notice>}
      {!readiness.complete && github?.state.installation === 'Installed' && (!github.availableActions.includes('login') || !github.availableActions.includes('prepareauthentication')) && <Notice>{t("settings.theSupportedDeviceFlowIsUnavailableAskTheServerAdministratorToInspect")}</Notice>}
      {!readiness.complete && <p className="text-sm text-tertiary">{t("settings.preparationCreatesPrivateProductManagedGitHubConfigurationAndRefusesUnrelatedOperatorAuthentication")}</p>}
    </section>
    <section className="space-y-4 rounded-xl border border-secondary bg-primary p-5"><h2 className="text-lg font-semibold text-primary">{t("settings.serverCapabilities")}</h2>
      {node ? <><p className="text-sm text-secondary">{node.displayName ?? t("settings.server")} · {localizeText(node.connectivity)} · {node.observationsStale ? t("settings.staleObservations") : t("settings.currentObservations")}</p>
        <p className="text-sm text-secondary">{t("settings.serviceHealth")}{' '}{localizeText(node.health ?? t("settings.unavailable"))}{' · '}{t("settings.executionReadiness")}{' '}{localizeText(node.executionReadiness)}{' · '}{t("settings.provisioningReadiness")}{' '}{localizeText(node.provisioningReadiness ?? t("settings.unavailable"))}</p><div className="grid min-w-0 gap-4">{node.capabilities.map(capability => <div key={capability.definition.id} className="min-w-0 space-y-3">
          <Capability capability={capability} commands={allCommands ?? null} />
          <div className="flex flex-wrap gap-2">{capability.availableActions.filter(action => Object.hasOwn(nodeActions, action)).map(action => <Button key={action} color="secondary" isDisabled={busy} onPress={() => setIntent({ capabilityId: capability.definition.id, action })}>{localizeText(nodeActions[action].label)}</Button>)}</div>
        </div>)}</div></> : <Notice>{t("settings.serverCapabilitiesUnavailable")}</Notice>}
      {allCommands?.filter(c => activeCommand(c)).map(c => <div key={c.id} className="space-y-2"><StatusBadge tone="warning">{localizeText(c.request.action)} · {localizeText(c.status)}</StatusBadge>
        {(c.status === 'Pending' || expiredCommand(c, Math.max(now, Date.now()))) && <Button color="secondary" isDisabled={runtime.locked(fence)} onPress={() => setIntent({ command: c, control: c.status === 'Pending' ? 'cancel' : 'reconcile' })}>{c.status === 'Pending' ? t("settings.cancelQueuedOperation") : t("settings.reconcileAfterNodeQuiescence")}</Button>}
      </div>)}
      <AdvancedDisclosure title={t("settings.advancedOperationHistory")}><p>{t("settings.boundedServerHistoryActiveOperationsAreRetainedNoTotalHistoryCountIs")}</p>{allCommands?.length ? allCommands.map(c => <div key={c.id} className="break-all border-t border-secondary py-3"><p>{c.request.capabilityId} · {localizeText(c.request.action)} · {localizeText(c.status)}</p><p>{t("settings.iD")}{' '}{c.id}{' '}{t("settings.queued")}{' '}{timestamp(c.createdAtUtc)}{' '}{t("settings.started")}{' '}{timestamp(c.startedAtUtc)}{' '}{t("settings.deadline")}{' '}{timestamp(c.deadlineUtc)}{' '}{t("settings.completed")}{' '}{timestamp(c.completedAtUtc)} · {localizeText(c.diagnostic ?? t("settings.noDiagnostic"))}</p>{c.failureDetail && <p>{c.failureDetail.description}</p>}{c.publicIdentity && <pre className="whitespace-pre-wrap break-all">{c.publicIdentity.publicKey}{'\n'}{c.publicIdentity.fingerprint}</pre>}</div>) : <p>{t("settings.noOperationHistoryAvailable")}</p>}</AdvancedDisclosure>
    </section>
    {message && <Notice>{message}</Notice>}
    {intent && <ActionDialog isOpen title={'control' in intent ? intent.control === 'cancel' ? t("settings.cancelQueuedOperation") : t("settings.reconcileAfterNodeQuiescence") : nodeActions[intent.action].label}
      description={'control' in intent ? t("settings.runningOperationsStopAtTheirDeadlineReconciliationReleasesTheCommandLockOnly") : t("settings.onlyThisAdvertisedTypedActionIsSubmittedServerAuthorizationDoesNotOverride")}
      actionLabel={t("settings.confirmOperation")} onClose={close} onSubmit={submit}
      disabled={'control' in intent ? intent.control === 'reconcile' && !quiescent : (!!nodeActions[intent.action].elevation && !elevation) || (['login', 'prepareauthentication'].includes(intent.action) && !consent) || (intent.action === 'verifyrepositoryaccess' && !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9_.-]{1,100}$/.test(repository))}
      destructive={'control' in intent || !!nodeActions['action' in intent ? intent.action : '']?.destructive}>
      {'control' in intent ? intent.control === 'reconcile' && <Checkbox label={t("settings.iVerifiedTheServerIsQuiescentAndTheMutationHasStopped")} isSelected={quiescent} onChange={setQuiescent} /> : <>
        {['login', 'prepareauthentication'].includes(intent.action) && <Checkbox label={t("settings.authorizePreparationAndDeviceLoginInTheServerServiceAccount")} isSelected={consent} onChange={setConsent} />}
        {nodeActions[intent.action].elevation && <Checkbox label={t("settings.authorizeInstallationConfigurationElevationUnderLocalPolicy")} isSelected={elevation} onChange={setElevation} />}
        {intent.action === 'verifyrepositoryaccess' && <Input label={t("settings.repositoryOwnerRepository")} value={repository} onChange={setRepository} maxLength={140} />}
      </>}
    </ActionDialog>}
  </div>;
}
