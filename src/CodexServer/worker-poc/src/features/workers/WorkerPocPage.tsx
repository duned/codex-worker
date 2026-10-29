import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useMutation } from '@tanstack/react-query';
import { queryKeys } from '../../shared/api/runtime';
import { useState } from 'react';
import { ConfirmationDialog } from '../../shared/Dialogs';
import { WorkerDetailPage } from './WorkerDetailPage';
import { WorkerEditor } from './WorkerEditor';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { worker, diagnostics, deliveryAuthorization, version as validateVersion } from '../../shared/api/validation';
import type { WorkerAdministration, WorkerReadiness, DeliveryAuthorization } from '../../shared/api/contracts';
import { ApiError } from '../../shared/api/client';
const actions = [
  { key: 'Enabled', label: t("workers.activateScheduling") }, { key: 'Draining', label: t("workers.drainWorker") },
  { key: 'Disabled', label: t("workers.deactivate") }, { key: 'revoke-api', label: t("workers.revokeWorkerAPIToken") },
  { key: 'repair-delivery', label: t('workers.repairDeliveryAuthorization') },
  { key: 'revoke-delivery', label: t("workers.revokeDeliveryAuthorization") }
];
function reason(key: string, registry?: WorkerAdministration, readiness?: WorkerReadiness, delivery?: DeliveryAuthorization) {
  if (!registry) return t("workers.currentRegistryStateUnavailableRefreshToRecoverIt");
  if (key === 'repair-delivery') return registry.authenticationCredentialStatus === 'active' ? '' : t('workers.noActiveWorkerAPITokenIsRegistered');
  if (key === 'revoke-delivery') return ['active', 'pending', 'failed'].includes(delivery?.status ?? '') ? '' : t("workers.noActiveCredentialDeliveryAuthorizationIsReported");
  if (key === 'revoke-api') return registry.authenticationCredentialStatus === 'active' ? '' : t("workers.noActiveWorkerAPITokenIsRegistered");
  if (registry.schedulingPolicy === key) return t("workers.thisSchedulingPolicyIsAlreadyApplied");
  if (key === 'Enabled' && readiness?.canActivate !== true) return t('workers.activationBlocked', { reasons: readiness?.activationBlockingReasons?.map(localizeText).join('; ') || t("workers.readinessEvidenceUnavailable") });
  return '';
}
export interface AdministrationPresentation {
  worker?: WorkerAdministration; delivery?: DeliveryAuthorization; pending: boolean; needsRefresh: boolean; message: string;
  actions: { key: string; label: string; reason: string }[];
  onEdit(): void;
  onAction(key: string): void; onRefreshStatus(): Promise<void>;
}
export function WorkerPocPage({ id }: { id: string }) {
  useLanguage();
  const runtime = useRuntime(), session = useSession();
  const base = `/api/v1/workers/${encodeURIComponent(id)}`, resource = base;
  const registry = useApiRead(base, worker), readiness = useApiRead(`${base}/diagnostics`, diagnostics);
  const serverVersion = useApiRead('/api/version', validateVersion);
  const delivery = useApiRead(`${base}/credential-access`, deliveryAuthorization);
  const [selected, setSelected] = useState<string>(), [pending, setPending] = useState(false), [message, setMessage] = useState('');
  const [editing, setEditing] = useState(false);
  const editingWorker = registry.retainedData;
  const locked = runtime.locked(resource);
  const refreshStatus = async () => {
    if (pending) throw new ApiError(t('workers.statusRefreshUnavailableWhileAnActionIsRunning'));
    const generation = session.generation;
    await runtime.reconcile(resource, async signal => {
      const [value, authorization] = await Promise.all([
        runtime.read(base, signal, worker),
        runtime.read(`${base}/credential-access`, signal, deliveryAuthorization)
      ]);
      if (runtime.snapshot().generation !== generation) return;
      runtime.queries.setQueryData(queryKeys.read(generation, `${base}/credential-access`), authorization);
      runtime.queries.setQueryData(queryKeys.read(generation, base), value);
    });
  };
  const operation = useMutation({ retry: false, mutationFn: async (key: string) => {
    if (key === 'Enabled' || key === 'Draining' || key === 'Disabled') {
      return runtime.mutate(resource, `${base}/scheduling-policy`, 'PUT', { policy: key }, worker);
    }
    const check = async (signal: AbortSignal) => {
      const current = await runtime.read(base, signal, worker);
      const authorization = key === 'revoke-delivery'
        ? await runtime.read(`${base}/credential-access`, signal, deliveryAuthorization)
        : undefined;
      const blocked = reason(key, current, readiness.data, authorization);
      if (blocked) throw new ApiError(blocked);
    };
    if (key === 'repair-delivery') return runtime.mutate(resource, `${base}/credential-access/repair`, 'POST', undefined, deliveryAuthorization, check);
    if (key === 'revoke-delivery') return runtime.mutate(resource, `${base}/credential-access/revoke`, 'POST', undefined, deliveryAuthorization, check);
    return runtime.mutate(resource, `${base}/authentication/revoke`, 'POST', undefined, worker, check);
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
    onEdit: () => { if (registry.data && !pending && !locked) setEditing(true); },
    actions: actions.map(action => ({ ...action, reason: pending ? t("workers.anAdministrationOperationIsPending") : locked
      ? t("workers.refreshAuthoritativeStateBeforeAnotherActionThePreviousOperationWillNotBe") : reason(action.key, registry.data, readiness.data, delivery.data) })),
    onAction: key => { if (actions.some(action => action.key === key) && !pending && !locked && !reason(key, registry.data, readiness.data, delivery.data)) setSelected(key); },
    onRefreshStatus: refreshStatus
  };
  return <><WorkerDetailPage workerId={id} administration={administration} />
    {editing && editingWorker && <WorkerEditor key={id} worker={editingWorker} workerStatusError={!!registry.error} diagnostics={readiness.data}
      diagnosticsLoading={readiness.loading} diagnosticsError={!!readiness.error}
      serverVersion={serverVersion.data?.version} serverVersionLoading={serverVersion.loading || serverVersion.fetching} onClose={() => setEditing(false)} />}
    <ConfirmationDialog key={selected ?? 'closed'} isOpen={!!selected} onClose={() => setSelected(undefined)}
      title={actions.find(action => action.key === selected)?.label ?? t("workers.confirmAction")} actionLabel={t("workers.confirm")}
      destructive={selected === 'revoke-api' || selected === 'revoke-delivery' || selected === 'Disabled'} disabled={pending || locked}
      description={selected === 'repair-delivery' ? t('workers.repairDeliveryDescription') : selected === 'revoke-delivery' ? t("workers.stopFutureDeliveryOfServerManagedCredentialsToThisWorkerWorkerAPI") : selected === 'revoke-api'
        ? t("workers.callsUsingThisTokenWillBeDeniedAndActiveLeasesMayExpire")
        : selected === 'Enabled' ? t("workers.allowNewAssignmentsAfterCurrentServerReadinessValidationExistingAssignmentsAndLeases")
        : selected === 'Draining' ? t("workers.pauseNewAssignmentsWhileExistingAssignmentsKeepTheirLeasesAndFinishThis")
        : t("workers.stopNewAssignmentsExistingAssignmentsAndLeasesAreNotCancelledCurrentServer")}
      onSubmit={submit} />
  </>;
}
