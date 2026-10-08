import { t, useLanguage, localizeText } from '../../shared/i18n';
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
import { Input } from '../../shared/Input';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { CapabilityCard } from '../../detail.jsx';
import { commandActive, commandExpired, deviceLoginUrl, nodeActions, provisioningReason } from './provisioning';
type Selection = { capabilityId: string; action: string } | { command: NodeCommandSummary; action: 'cancel' | 'reconcile' };
/** Node-scoped operations and capability views are shared with Settings. */
export function NodeProvisioning({ nodeId, repository = '' }: { nodeId: string; repository?: string }) {
  useLanguage();
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
      if (runtime.snapshot().generation === generation) setMessage(t("nodes.authoritativeOperationStateRefreshedReviewRetainedOperationsBeforeRetrying"));
    } catch { if (runtime.snapshot().generation === generation) setMessage(t("nodes.operationStateUnavailableNoCommandWasResubmitted")); }
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
          if (spec?.elevation && !elevation) throw new ApiError(t("nodes.explicitElevationConsentRequired"));
          if (selection.action === 'verifyrepositoryaccess' && !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(targetRepository.trim())) throw new ApiError(t("nodes.enterAGitHubOwnerRepository"));
        } else {
          const current = retained.find(item => item.id === selection.command.id);
          if (!current || current.request.nodeId !== nodeId || (selection.action === 'cancel' ? current.status !== 'Pending' : !quiescent || !commandExpired(current, Date.now()))) throw new ApiError(t("nodes.currentCommandStateOrExplicitNodeQuiescenceConfirmationRequired"));
        }
      });
      if (runtime.snapshot().generation === generation) setMessage(t("nodes.operationAcceptedRetainedProgressIsRefreshedAutomaticallyLeavingOrReloadingDoesNot"));
    } catch (error) { if (runtime.snapshot().generation === generation) setMessage(error instanceof ApiError ? error.message : t("nodes.operationResultUnavailableRefreshAuthoritativeOperationStateBeforeRetrying")); throw error; }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  function controls(capability: Capability) {
    const matching = commands.data?.filter(item => item.request.nodeId === nodeId && item.request.capabilityId === capability.definition.id) ?? [];
    const actions = capability.availableActions.filter(action => Object.hasOwn(nodeActions, action));
    return <div className="mt-4 space-y-3">
      {matching.filter(commandActive).map(item => {
        const expired = commandExpired(item, Date.now()), loginUrl = item.loginInstructions && deviceLoginUrl(item.loginInstructions.verificationUri);
        return <div key={item.id} className="space-y-2"><p>{localizeText(item.request.action)}: {localizeText(item.status)}{expired && t('nodes.deadlineExpired')}</p>
          {!locked && item.status === 'Running' && !expired && Date.parse(item.deadlineUtc ?? '') > Date.now() && loginUrl && <p>{t("nodes.onlyApproveTheLoginYouStartedOnThisNodeOpen")}{' '}<ExternalLink href={loginUrl}>{t("nodes.deviceLogin")}</ExternalLink>{' '}{t("nodes.andEnter")}{' '}<strong>{item.loginInstructions?.userCode}</strong>{t("nodes.expires")}{' '}{timestamp(item.deadlineUtc)}.</p>}
          {(item.status === 'Pending' || expired) && <Button color="secondary" isDisabled={pending || locked || !commands.data || !node} onPress={() => choose({ command: item, action: expired ? 'reconcile' : 'cancel' })}>{expired ? t("nodes.reconcileAfterNodeQuiescence") : t("nodes.cancelQueuedOperation")}</Button>}
        </div>;
      })}
      <p>{t("nodes.nodeLocalPermissionIsRequired")}</p>
      <AdvancedDisclosure title={t("nodes.localPreparationAndAuthorization")}><p>{t("nodes.serverPermissionCannotOverrideLocalPolicyAuthenticationMustUseTheNodeService")}</p></AdvancedDisclosure>
      <div className="flex flex-wrap gap-2">{actions.map(action => <Button key={action} color="secondary" size="sm" isDisabled={pending || locked || !!provisioningReason(node, commands.data, capability.definition.id, action)} onPress={() => choose({ capabilityId: capability.definition.id, action })}>{localizeText(nodeActions[action].label)}</Button>)}</div>
      {!actions.length && <p>{t("nodes.noSupportedRemoteActionsAvailablePrepareThisToolInTheNodeService")}</p>}
      <AdvancedDisclosure title={t("nodes.operationHistoryAndPublicSSHIdentities")}>{matching.length ? matching.map(item => <div key={item.id} className="break-all"><p>{localizeText(item.request.action)} · {localizeText(item.status)} · {localizeText(item.diagnostic ?? t("nodes.noDiagnosticReported"))}</p><p>{t("nodes.iD")}{' '}{item.id} · {timestamp(item.createdAtUtc)}</p>{item.publicIdentity && <pre className="whitespace-pre-wrap">{item.publicIdentity.publicKey}{'\n'}{item.publicIdentity.fingerprint}</pre>}</div>) : <p>{t("nodes.noCommandsReportedInBoundedNodeHistory")}</p>}</AdvancedDisclosure>
    </div>;
  }
  const spec = selected && 'capabilityId' in selected ? nodeActions[selected.action] : undefined;
  return <section aria-label={t("nodes.nodeProvisioning")} className="space-y-4">
    {(nodes.error || commands.error) && <Notice error>{t("nodes.capabilityOrOperationStateUnavailableRefreshBeforeAnotherAction")}</Notice>}
    {message && <Notice>{message}</Notice>}
    <Button color="secondary" isDisabled={pending} onPress={() => { void refresh(); }}>{t("nodes.refreshAuthoritativeOperationState")}</Button>
    {node ? node.capabilities.length ? <div className="poc-capabilities">{node.capabilities.map(capability => <CapabilityCard key={capability.definition.id} capability={capability} commands={commands.data ?? null}>{controls(capability)}</CapabilityCard>)}</div> : <Notice>{t("nodes.noCapabilitiesReported")}</Notice> : <Notice>{t("nodes.capabilityObservationsUnavailable")}</Notice>}
    <ActionDialog key={selected ? JSON.stringify(selected) : 'closed'} isOpen={!!selected} onClose={() => { setSelected(undefined); setElevation(false); setQuiescent(false); }}
      title={spec?.label ?? (selected?.action === 'cancel' ? t("nodes.cancelQueuedOperation") : t("nodes.reconcileAfterNodeQuiescence"))} actionLabel={t("nodes.confirm")} destructive={!!spec?.destructive || selected?.action === 'reconcile'} disabled={pending || locked || (!!spec?.elevation && !elevation) || (selected?.action === 'reconcile' && !quiescent)} onSubmit={submit}
      description={selected?.action === 'reconcile' ? t("nodes.releaseTheRetainedOperationLockOnlyAfterIndependentlyVerifyingTheNodeIs") : selected?.action === 'cancel' ? t("nodes.cancelThisQueuedCommandRunningMutationsAreNotCancelled") : t("nodes.runOnlyThisAdvertisedTypedActionNodeLocalPermissionAndServiceAccount")}>
      {spec?.elevation && <Checkbox label={t("nodes.authorizeElevationForThisAction")} isSelected={elevation} onChange={setElevation} />}
      {selected?.action === 'reconcile' && <Checkbox label={t("nodes.iVerifiedTheNodeIsQuiescentAndTheMutationHasStopped")} isSelected={quiescent} onChange={setQuiescent} />}
      {selected?.action === 'verifyrepositoryaccess' && <Input label={t("nodes.gitHubRepositoryOwnerRepository")} value={targetRepository} onChange={setRepository} isRequired hint={t("nodes.readVerificationDoesNotProvePushPermissionAndNeverPerformsATest")} />}
    </ActionDialog>
  </section>;
}
