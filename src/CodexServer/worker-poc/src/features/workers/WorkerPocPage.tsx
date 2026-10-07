import { useMutation } from '@tanstack/react-query';
import { queryKeys } from '../../shared/api/runtime';
import { useState } from 'react';
import { Dialog, Heading, Modal, ModalOverlay } from 'react-aria-components';
import { Button } from '../../untitled/components/base/buttons/button';
import { WorkerDetailPage } from './WorkerDetailPage';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { worker, diagnostics } from '../../shared/api/validation';
import type { WorkerAdministration, WorkerReadiness } from '../../shared/api/contracts';
import { ApiError } from '../../shared/api/client';
const actions = [
  { key: 'Enabled', label: 'Activate scheduling' }, { key: 'Draining', label: 'Drain worker' },
  { key: 'Disabled', label: 'Deactivate' }, { key: 'revoke-api', label: 'Revoke Worker API token' }
];
function reason(key: string, registry?: WorkerAdministration, readiness?: WorkerReadiness) {
  if (!registry) return 'Current registry state unavailable. Refresh to recover it.';
  if (key === 'revoke-api') return registry.authenticationCredentialStatus === 'active' ? '' : 'No active Worker API token is registered.';
  if (registry.schedulingPolicy === key) return 'This scheduling policy is already applied.';
  if (key === 'Enabled' && readiness?.canActivate !== true) return `Activation blocked: ${readiness?.activationBlockingReasons?.join('; ') || 'Readiness evidence unavailable.'}`;
  return '';
}
export interface AdministrationPresentation {
  worker?: WorkerAdministration; pending: boolean; needsRefresh: boolean; message: string;
  actions: { key: string; label: string; reason: string }[];
  onAction(key: string): void; onRefresh(): void;
}
export function WorkerPocPage({ id }: { id: string }) {
  const runtime = useRuntime(), session = useSession();
  const base = `/api/v1/workers/${encodeURIComponent(id)}`, resource = base;
  const registry = useApiRead(base, worker), readiness = useApiRead(`${base}/diagnostics`, diagnostics);
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
        runtime.queries.setQueryData(queryKeys.read(generation, base), value);
      });
      await readiness.refetch();
      if (runtime.snapshot().generation === generation) setMessage('Authoritative state refreshed. Review current policy, token and readiness before another action.');
    } catch {
      if (runtime.snapshot().generation === generation) setMessage('Authoritative state unavailable. The operation has not been resubmitted.');
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  };
  const operation = useMutation({ retry: false, mutationFn: (key: string) => runtime.mutate(resource,
    key === 'revoke-api' ? `${base}/authentication/revoke` : `${base}/scheduling-policy`,
    key === 'revoke-api' ? 'POST' : 'PUT', key === 'revoke-api' ? undefined : { policy: key }, worker, async signal => {
      const current = await runtime.read(base, signal, worker);
      let evidence: WorkerReadiness | undefined;
      if (key === 'Enabled') evidence = await runtime.read(`${base}/diagnostics`, signal, diagnostics);
      const blocked = reason(key, current, evidence);
      if (blocked) throw new ApiError(blocked);
    }) });
  const submit = async () => {
    const key = selected, generation = session.generation;
    setSelected(undefined);
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
    worker: registry.data, pending, needsRefresh: locked, message,
    actions: actions.map(action => ({ ...action, reason: pending ? 'An administration operation is pending.' : locked
      ? 'Refresh authoritative state before another action. The previous operation will not be resubmitted.' : reason(action.key, registry.data, readiness.data) })),
    onAction: key => { if (actions.some(action => action.key === key) && !pending && !locked && !reason(key, registry.data, readiness.data)) setSelected(key); },
    onRefresh: () => { void refresh(); }
  };
  return <><WorkerDetailPage workerId={id} administration={administration} />
    <ModalOverlay isOpen={!!selected} onOpenChange={open => { if (!open) setSelected(undefined); }} isDismissable
      className="fixed inset-0 z-50 flex items-center justify-center bg-overlay/70 p-4 backdrop-blur-sm">
      <Modal className="w-full max-w-md rounded-xl bg-primary p-6 shadow-xl ring-1 ring-secondary">
        <Dialog className="flex flex-col gap-4 outline-none">
          <Heading slot="title" className="text-lg font-semibold text-primary">{actions.find(action => action.key === selected)?.label}</Heading>
          <p className="text-sm text-secondary">{selected === 'revoke-api'
            ? 'Calls using this token will be denied and active leases may expire into recovery. Credential-delivery authorization, node login and provider credentials are unchanged.'
            : 'This affects new assignments only. Existing assignments and leases are not cancelled. Current Server validation still applies.'}</p>
          <div className="flex justify-end gap-3"><Button color="secondary" onPress={() => setSelected(undefined)}>Cancel</Button>
            <Button color="primary" onPress={() => { void submit(); }}>Confirm</Button></div>
        </Dialog>
      </Modal>
    </ModalOverlay>
  </>;
}
