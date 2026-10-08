import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useMutation } from '@tanstack/react-query';
import { queryKeys } from '../../shared/api/runtime';
import { useState } from 'react';
import { ConfirmationDialog } from '../../shared/Dialogs';
import { WorkerDetailPage } from './WorkerDetailPage';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { worker, diagnostics, deliveryAuthorization } from '../../shared/api/validation';
import type { WorkerAdministration, WorkerReadiness, DeliveryAuthorization } from '../../shared/api/contracts';
import { ApiError } from '../../shared/api/client';
const actions = [
  { key: 'Enabled', label: t("workers.activateScheduling") }, { key: 'Draining', label: t("workers.drainWorker") },
  { key: 'Disabled', label: t("workers.deactivate") }, { key: 'revoke-api', label: t("workers.revokeWorkerAPIToken") },
  { key: 'revoke-delivery', label: t("workers.revokeDeliveryAuthorization") }
];
function reason(key: string, registry?: WorkerAdministration, readiness?: WorkerReadiness, delivery?: DeliveryAuthorization) {
  if (!registry) return t("workers.currentRegistryStateUnavailableRefreshToRecoverIt");
  if (key === 'revoke-delivery') return delivery?.status === 'active' ? '' : t("workers.noActiveCredentialDeliveryAuthorizationIsReported");
  if (key === 'revoke-api') return registry.authenticationCredentialStatus === 'active' ? '' : t("workers.noActiveWorkerAPITokenIsRegistered");
  if (registry.schedulingPolicy === key) return t("workers.thisSchedulingPolicyIsAlreadyApplied");
  if (key === 'Enabled' && readiness?.canActivate !== true) return t('workers.activationBlocked', { reasons: readiness?.activationBlockingReasons?.map(localizeText).join('; ') || t("workers.readinessEvidenceUnavailable") });
  return '';
}
export interface AdministrationPresentation {
  worker?: WorkerAdministration; delivery?: DeliveryAuthorization; pending: boolean; needsRefresh: boolean; message: string;
  actions: { key: string; label: string; reason: string }[];
  onAction(key: string): void; onRefresh(): void;
}
export function WorkerPocPage({ id }: { id: string }) {
  useLanguage();
  const runtime = useRuntime(), session = useSession();
  const base = `/api/v1/workers/${encodeURIComponent(id)}`, resource = base;
  const registry = useApiRead(base, worker), readiness = useApiRead(`${base}/diagnostics`, diagnostics);
  const delivery = useApiRead(`${base}/credential-access`, deliveryAuthorization);
  const [selected, setSelected] = useState<string>(), [pending, setPending] = useState(false), [message, setMessage] = useState('');
  const locked = runtime.locked(resource);
  const refresh = async () => {
    if (pending) return;
    const generation = session.generation;
    setPending(true);
    try {
      await runtime.reconcile(resource, async signal => {
        // Registry recovery establishes actual policy/token state. Missing
        // diagnostics block activation while preserving independent controls.
        const value = await runtime.read(base, signal, worker);
        const authorization = await runtime.read(`${base}/credential-access`, signal, deliveryAuthorization);
        runtime.queries.setQueryData(queryKeys.read(generation, `${base}/credential-access`), authorization);
        runtime.queries.setQueryData(queryKeys.read(generation, base), value);
      });
      await readiness.refetch();
      if (runtime.snapshot().generation === generation) setMessage(t("workers.authoritativeStateRefreshedReviewCurrentPolicyTokenAndReadinessBeforeAnotherAction"));
    } catch {
      if (runtime.snapshot().generation === generation) setMessage(t("workers.authoritativeStateUnavailableTheOperationHasNotBeenResubmitted"));
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  };
  const operation = useMutation({ retry: false, mutationFn: async (key: string) => {
    const check = async (signal: AbortSignal) => {
      const current = await runtime.read(base, signal, worker);
      const evidence = key === 'Enabled' ? await runtime.read(`${base}/diagnostics`, signal, diagnostics) : undefined;
      const authorization = key === 'revoke-delivery' ? await runtime.read(`${base}/credential-access`, signal, deliveryAuthorization) : undefined;
      const blocked = reason(key, current, evidence, authorization);
      if (blocked) throw new ApiError(blocked);
    };
    if (key === 'revoke-delivery') return runtime.mutate(resource, `${base}/credential-access/revoke`, 'POST', undefined, deliveryAuthorization, check);
    return runtime.mutate(resource, key === 'revoke-api' ? `${base}/authentication/revoke` : `${base}/scheduling-policy`,
      key === 'revoke-api' ? 'POST' : 'PUT', key === 'revoke-api' ? undefined : { policy: key }, worker, check);
  } });
  const submit = async () => {
    const key = selected, generation = session.generation;
    if (!key || pending || locked) return;
    setPending(true); setMessage(t("workers.administrationOperationPendingDoNotSubmitAnotherAction"));
    try {
      await operation.mutateAsync(key);
      if (runtime.snapshot().generation === generation) setMessage(t("workers.operationAcceptedCurrentServerObservationsAreRefreshing"));
    } catch (error) {
      if (runtime.snapshot().generation === generation) setMessage(error instanceof ApiError ? error.message : t("workers.operationResultUnavailableRefreshAuthoritativeStateBeforeRetrying"));
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  };
  const administration: AdministrationPresentation = {
    worker: registry.data, delivery: delivery.data, pending, needsRefresh: locked, message,
    actions: actions.map(action => ({ ...action, reason: pending ? t("workers.anAdministrationOperationIsPending") : locked
      ? t("workers.refreshAuthoritativeStateBeforeAnotherActionThePreviousOperationWillNotBe") : reason(action.key, registry.data, readiness.data, delivery.data) })),
    onAction: key => { if (actions.some(action => action.key === key) && !pending && !locked && !reason(key, registry.data, readiness.data, delivery.data)) setSelected(key); },
    onRefresh: () => { void refresh(); }
  };
  return <><WorkerDetailPage workerId={id} administration={administration} />
    <ConfirmationDialog key={selected ?? 'closed'} isOpen={!!selected} onClose={() => setSelected(undefined)}
      title={actions.find(action => action.key === selected)?.label ?? t("workers.confirmAction")} actionLabel={t("workers.confirm")}
      destructive={selected === 'revoke-api' || selected === 'revoke-delivery' || selected === 'Disabled'} disabled={pending || locked}
      description={selected === 'revoke-delivery' ? t("workers.stopFutureDeliveryOfServerManagedCredentialsToThisWorkerWorkerAPI") : selected === 'revoke-api'
        ? t("workers.callsUsingThisTokenWillBeDeniedAndActiveLeasesMayExpire")
        : selected === 'Enabled' ? t("workers.allowNewAssignmentsAfterCurrentServerReadinessValidationExistingAssignmentsAndLeases")
        : selected === 'Draining' ? t("workers.pauseNewAssignmentsWhileExistingAssignmentsKeepTheirLeasesAndFinishThis")
        : t("workers.stopNewAssignmentsExistingAssignmentsAndLeasesAreNotCancelledCurrentServer")}
      onSubmit={submit} />
  </>;
}
