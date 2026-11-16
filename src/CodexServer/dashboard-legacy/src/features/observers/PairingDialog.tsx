import { useEffect, useRef, useState } from 'react';
import { t, useLanguage } from '../../shared/i18n';
import { useRuntime, useSession } from '../../shared/api/session';
import { observerAuthorization, observers, readObserverRequest, type ObserverAuthorization } from '../../shared/api/observers';
import { FormDialog } from '../../shared/Dialogs';
import { Notice } from '../../shared/Presentation';
import { Input } from '../../shared/Input';
import { Button } from '../../untitled/components/base/buttons/button';

export function PairingDialog({ onClose }: { onClose(): void }) {
  useLanguage();
  const runtime = useRuntime(), session = useSession();
  const [request, setRequest] = useState(''), [secret, setSecret] = useState<ObserverAuthorization>();
  const [pending, setPending] = useState(false), [message, setMessage] = useState('');
  const mounted = useRef(true), busy = useRef(false);
  const resource = '/api/v1/observers/pairing/authorize';
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!secret) return;
    const timer = setTimeout(() => setSecret(undefined), Math.max(0, Math.min(300000, Date.parse(secret.expiresAtUtc) - Date.now())));
    return () => clearTimeout(timer);
  }, [secret]);
  async function authorize() {
    if (busy.current) return;
    busy.current = true; setPending(true); setSecret(undefined); setMessage('');
    const generation = session.generation;
    try {
      const body = readObserverRequest(request, window.location.origin);
      const result = await runtime.mutate(resource, resource, 'POST', body, observerAuthorization);
      if (mounted.current && runtime.snapshot().generation === generation) {
        if (result.observerId !== body.observerId || result.server !== body.server) throw new Error();
        setSecret(result); setMessage(t('observers.secretHelp'));
      }
    } catch { if (mounted.current && runtime.snapshot().generation === generation) setMessage(t('observers.pairingError')); }
    finally { busy.current = false; if (mounted.current) setPending(false); }
  }
  async function check() {
    setPending(true); setSecret(undefined);
    try {
      await runtime.reconcile(resource, async signal => { await runtime.read('/api/v1/observers?limit=100', signal, observers); });
      if (mounted.current) setMessage(t('observers.checked'));
    } catch { if (mounted.current) setMessage(t('observers.pairingError')); }
    finally { if (mounted.current) setPending(false); }
  }
  async function copy() {
    if (!secret) return;
    try { await navigator.clipboard.writeText(secret.authorization); setMessage(t('observers.copied')); }
    catch { setMessage(t('observers.copyError')); }
  }
  return <FormDialog isOpen title={t('observers.add')} description={t('observers.pairHelp')} onClose={onClose} pending={pending}>
    <p className="text-sm text-secondary">{t('observers.commandHelp')}</p>
    <pre className="whitespace-pre-wrap break-all text-xs text-secondary">codex-worker reporting connect --server {window.location.origin}</pre>
    <label className="text-sm text-secondary">{t('observers.publicRequest')}<textarea className="w-full rounded-lg border border-secondary bg-primary p-2" value={request} maxLength={4096} disabled={pending || !!secret} onChange={event => setRequest(event.target.value)} /></label>
    <Button isDisabled={pending || !!secret || runtime.locked(resource)} onPress={() => { void authorize(); }}>{t('observers.authorize')}</Button>
    {secret && <>
      <Input label={t('observers.authorization')} value={secret.authorization} isReadOnly autoComplete="off" />
      <p className="text-sm text-secondary">{t('observers.expires', { time: new Date(secret.expiresAtUtc).toLocaleString() })}</p>
      <Button color="secondary" onPress={() => { void copy(); }}>{t('observers.copy')}</Button>
    </>}
    {message && <Notice>{message}</Notice>}
    <Button color="secondary" isDisabled={pending} onPress={() => { void check(); }}>{t('observers.clearCheck')}</Button>
    <Button color="secondary" isDisabled={pending} onPress={onClose}>{t('workers.close')}</Button>
  </FormDialog>;
}
