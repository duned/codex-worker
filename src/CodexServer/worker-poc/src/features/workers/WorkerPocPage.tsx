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
  { key: 'Enabled', label: 'Activate scheduling' }, { key: 'Draining', label: 'Drain worker' },
  { key: 'Disabled', label: 'Deactivate' }, { key: 'revoke-api', label: 'Revoke Worker API token' },
  { key: 'revoke-delivery', label: 'Revoke delivery authorization' }
];
function reason(key: string, registry?: WorkerAdministration, readiness?: WorkerReadiness, delivery?: DeliveryAuthorization) {
  if (!registry) return 'Current registry state unavailable. Refresh to recover it.';
  if (key === 'revoke-delivery') return delivery?.status === 'active' ? '' : 'No active credential-delivery authorization is reported.';
  if (key === 'revoke-api') return registry.authenticationCredentialStatus === 'active' ? '' : 'No active Worker API token is registered.';
  if (registry.schedulingPolicy === key) return 'This scheduling policy is already applied.';
  if (key === 'Enabled' && readiness?.canActivate !== true) return `Activation blocked: ${readiness?.activationBlockingReasons?.join('; ') || 'Readiness evidence unavailable.'}`;
  return '';
}
export interface AdministrationPresentation {
  worker?: WorkerAdministration; delivery?: DeliveryAuthorization; pending: boolean; needsRefresh: boolean; message: string;
  actions: { key: string; label: string; reason: string }[];
  onAction(key: string): void; onRefresh(): void;
}
export function WorkerPocPage({ id }: { id: string }) {
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
      if (runtime.snapshot().generation === generation) setMessage('Authoritative state refreshed. Review current policy, token and readiness before another action.');
    } catch {
      if (runtime.snapshot().generation === generation) setMessage('Authoritative state unavailable. The operation has not been resubmitted.');
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
    setPending(true); setMessage('Administration operation pending. Do not submit another action.');
    try {
      await operation.mutateAsync(key);
      if (runtime.snapshot().generation === generation) setMessage('Operation accepted. Current Server observations are refreshing.');
    } catch (error) {
      if (runtime.snapshot().generation === generation) setMessage(error instanceof ApiError ? error.message : 'Operation result unavailable. Refresh authoritative state before retrying.');
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  };
  const administration: AdministrationPresentation = {
    worker: registry.data, delivery: delivery.data, pending, needsRefresh: locked, message,
    actions: actions.map(action => ({ ...action, reason: pending ? 'An administration operation is pending.' : locked
      ? 'Refresh authoritative state before another action. The previous operation will not be resubmitted.' : reason(action.key, registry.data, readiness.data, delivery.data) })),
    onAction: key => { if (actions.some(action => action.key === key) && !pending && !locked && !reason(key, registry.data, readiness.data, delivery.data)) setSelected(key); },
    onRefresh: () => { void refresh(); }
  };
  return <><WorkerDetailPage workerId={id} administration={administration} />
    <ConfirmationDialog key={selected ?? 'closed'} isOpen={!!selected} onClose={() => setSelected(undefined)}
      title={actions.find(action => action.key === selected)?.label ?? 'Confirm action'} actionLabel="Confirm"
      destructive={selected === 'revoke-api' || selected === 'revoke-delivery' || selected === 'Disabled'} disabled={pending || locked}
      description={selected === 'revoke-delivery' ? 'Stop future delivery of Server-managed credentials to this Worker. Worker API authentication, node login and provider-side authorization are unchanged. Credentials already delivered are not removed from the node.' : selected === 'revoke-api'
        ? 'Calls using this token will be denied and active leases may expire into recovery. Credential-delivery authorization, node login and provider credentials are unchanged.'
        : selected === 'Enabled' ? 'Allow new assignments after current Server readiness validation. Existing assignments and leases are not cancelled.'
        : selected === 'Draining' ? 'Pause new assignments while existing assignments keep their leases and finish. This does not cancel running work.'
        : 'Stop new assignments. Existing assignments and leases are not cancelled. Current Server validation still applies.'}
      onSubmit={submit} />
  </>;
}
