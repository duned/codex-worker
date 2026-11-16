import { useEffect, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import type { WorkerObservation } from '../shared/api/contracts';
import { useRuntime } from '../shared/api/session';
import { accepted, pairingAuthorization, record } from '../shared/api/validation';
import { toast, useAction } from '../shared/actions';
import { t, useLanguage } from '../shared/i18n';
import { Field, inputClass, Notice, SidePanel } from '../shared/ui';

/** Edit name and capacity. The Server rejects the save if the Worker configuration changed meanwhile. */
export function WorkerPanel({ open, onClose, worker }: { open: boolean; onClose(): void; worker: WorkerObservation }) {
  useLanguage();
  const action = useAction();
  const [name, setName] = useState(''), [capacity, setCapacity] = useState('1'), [slots, setSlots] = useState('1');
  useEffect(() => { if (open) { setName(worker.displayName ?? ''); setCapacity(String(worker.configuredMaximumCapacity ?? worker.maximumCapacity ?? 1)); setSlots(String(worker.workerExecutionSlots ?? worker.maximumCapacity ?? 1)); } }, [open, worker]);
  const valid = name.trim() && Number(capacity) >= 1 && Number(capacity) <= 8 && Number(slots) >= 1 && Number(slots) <= 8 && worker.configurationRevision;
  return <SidePanel open={open} onClose={onClose} title={t('workers.edit')}
    footer={<><Button color="secondary" onClick={onClose}>{t('common.cancel')}</Button>
      <Button color="primary" isDisabled={!valid || !!action.busy} onClick={() => void action.run(`worker-${worker.workerId}`, `/api/v1/workers/${encodeURIComponent(worker.workerId)}/settings`, 'PUT', {
        displayName: name.trim(), maximumParallelExecutions: Number(capacity), workerExecutionSlots: Number(slots), expectedRevision: worker.configurationRevision
      }, 'workers.saved').then(done => { if (done) onClose(); })}>{t('common.save')}</Button></>}>
    <Field label={t('workers.name')}><input className={inputClass} value={name} onChange={event => setName(event.target.value)} /></Field>
    <Field label={t('workers.slotsField')} hint={t('workers.slotsHint')}><input className={inputClass} type="number" min={1} max={8} value={slots} onChange={event => setSlots(event.target.value)} /></Field>
    <Field label={t('workers.capacityField')} hint={t('workers.capacityHint')}><input className={inputClass} type="number" min={1} max={8} value={capacity} onChange={event => setCapacity(event.target.value)} /></Field>
    {!worker.configurationRevision && <Notice tone="warn">{t('workers.noRevision')}</Notice>}
  </SidePanel>;
}

/** Pair a new node: paste the public request the node prints, then copy the short-lived authorization back to it. */
export function ConnectWorkerPanel({ open, onClose }: { open: boolean; onClose(): void }) {
  useLanguage();
  const runtime = useRuntime();
  const [request, setRequest] = useState(''), [authorization, setAuthorization] = useState(''), [error, setError] = useState(''), [pending, setPending] = useState(false);
  useEffect(() => { if (!open) { setRequest(''); setAuthorization(''); setError(''); } }, [open]);
  const origin = window.location.origin;
  async function authorize() {
    setPending(true); setError(''); setAuthorization('');
    try {
      const value = record(JSON.parse(request));
      if (value.server !== origin) throw new Error(t('workers.pairingWrongServer', { origin }));
      const result = await runtime.mutate('/api/v1/workers/onboarding/authorize', '/api/v1/workers/onboarding/authorize', 'POST', value, pairingAuthorization);
      setAuthorization(result.authorization);
      setTimeout(() => setAuthorization(''), result.lifetimeSeconds * 1000);
    } catch (failure) {
      setError(failure instanceof SyntaxError ? t('workers.pairingInvalid') : failure instanceof Error ? failure.message : t('common.actionFailed'));
      try { await runtime.reconcile('/api/v1/workers/onboarding/authorize', async () => { await runtime.read('/api/v1/workers', new AbortController().signal, accepted); }); } catch { /* keep lock */ }
    } finally { setPending(false); }
  }
  return <SidePanel open={open} onClose={onClose} title={t('workers.connect')} footer={<Button color="secondary" onClick={onClose}>{t('common.close')}</Button>}>
    <p className="text-sm text-secondary">{t('workers.connectStep1')}</p>
    <pre className="overflow-x-auto rounded-lg bg-secondary p-3 font-mono text-xs text-secondary">{`codex-worker register --server ${origin} --pair`}</pre>
    <Field label={t('workers.pairingRequest')} hint={t('workers.pairingRequestHint')}>
      <textarea className={`${inputClass} min-h-24 font-mono text-xs`} value={request} onChange={event => setRequest(event.target.value)} placeholder='{"contractVersion":1,"workerId":"…","operation":"enroll","server":"…"}' />
    </Field>
    <Button color="primary" isDisabled={!request.trim() || pending} onClick={() => void authorize()}>{t('workers.authorize')}</Button>
    {authorization && <div className="flex flex-col gap-2"><span className="text-sm font-medium text-secondary">{t('workers.authorizationCode')}</span>
      <code className="rounded-lg bg-secondary p-3 font-mono text-sm break-all text-primary">{authorization}</code>
      <Button size="sm" color="secondary" onClick={() => void navigator.clipboard?.writeText(authorization).then(() => toast(t('common.copied')), () => undefined)}>{t('common.copy')}</Button>
      <p className="text-sm text-tertiary">{t('workers.authorizationHint')}</p></div>}
    {error && <Notice tone="bad">{error}</Notice>}
  </SidePanel>;
}
