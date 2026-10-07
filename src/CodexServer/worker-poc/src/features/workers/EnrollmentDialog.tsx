import { useEffect, useRef, useState } from 'react';
import { FormDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../untitled/components/base/input/input';
import { Notice } from '../../shared/Presentation';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { pairingAuthorization, version, workers } from '../../shared/api/validation';
import { readPairingRequest, registrationInstruction, restorePairingRequest, pairingDraftKey, pairingAcknowledged } from './enrollment';
export function EnrollmentDialog({ onClose }: { onClose(): void }) {
  const runtime = useRuntime(), session = useSession(), release = useApiRead('/api/version', version);
  const [draft] = useState(() => { try { return restorePairingRequest(sessionStorage.getItem(pairingDraftKey), window.location.origin); } catch { return undefined; } });
  const [operation, setOperation] = useState<'enroll' | 'associate'>(draft?.operation ?? 'enroll'), [step, setStep] = useState(draft ? 3 : 1);
  const [request, setRequest] = useState(draft ? JSON.stringify(draft) : ''), [authorization, setAuthorization] = useState(''), [reveal, setReveal] = useState(false);
  const [message, setMessage] = useState(draft ? 'Public request restored. Resume the node with empty input and Check progress before authorizing again.' : ''), [workerId, setWorkerId] = useState(''), [pending, setPending] = useState(false);
  const [mustCheck, setMustCheck] = useState(!!draft);
  const expiry = useRef<ReturnType<typeof setTimeout>>(undefined), mounted = useRef(true), busy = useRef(false);
  useEffect(() => () => { mounted.current = false; clearTimeout(expiry.current); }, []);
  const clear = () => { clearTimeout(expiry.current); setAuthorization(''); setReveal(false); };
  const resource = '/api/v1/workers/onboarding/authorize';
  async function act(authorize: boolean) {
    if (busy.current) return;
    busy.current = true; clear(); setPending(true); setWorkerId('');
    const generation = session.generation;
    const current = () => mounted.current && runtime.snapshot().generation === generation;
    try {
      const value = readPairingRequest(request, operation, window.location.origin);
      if (authorize) {
        setMustCheck(true);
        const result = await runtime.mutate(resource, resource, 'POST', value, pairingAuthorization, async signal => {
          const inventory = await runtime.read('/api/v1/workers', signal, workers);
          const registered = inventory.find(item => item.workerId === value.workerId);
          if (registered && operation === 'enroll') throw new Error('Identity already registered. Resume on the node with empty input before requesting another authorization.');
          if ((registered?.activeAssignments ?? 0) > 0) throw new Error('Drain and reconcile active assignments before association.');
        });
        if (!current()) return;
        setAuthorization(result.authorization); setStep(4);
        expiry.current = setTimeout(() => { setAuthorization(''); setReveal(false); }, result.lifetimeSeconds * 1000);
        setMessage('Paste this short-lived authorization into the hidden node prompt. Wait for acknowledgement, start the service, then Check progress. Authorization is never saved by the browser.');
      } else {
        await runtime.reconcile(resource, async signal => {
          const inventory = await runtime.read('/api/v1/workers', signal, workers);
          if (!current()) return;
          const registered = inventory.find(item => item.workerId === value.workerId);
          const connected = pairingAcknowledged(registered);
          setWorkerId(connected ? value.workerId : '');
          setMessage(connected ? 'Registration acknowledged and communication observed. Readiness and scheduling remain separate.'
            : 'Waiting for registration acknowledgement or communication. Resume the same node operation with empty input first. Request fresh authorization only after retained credential reconciliation fails.');
        });
        if (current()) setMustCheck(false);
      }
    } catch (error) { if (current()) setMessage(`${error instanceof Error ? error.message : 'Result unavailable.'} Preserve local pending state; check progress before authorizing again.`); }
    finally { busy.current = false; if (current()) setPending(false); }
  }
  return <FormDialog isOpen title="Add Worker" description="Machine → Local registration → Authorize / resume → Node acknowledgement" onClose={onClose} pending={pending}>
    <Notice>Step {step} of 4 · {['Machine', 'Local registration', 'Authorize / resume', 'Node acknowledgement'][step - 1]}</Notice>
    {step === 1 && <><label className="text-sm text-secondary">Machine<select aria-label="Machine" className="w-full rounded-lg border border-secondary bg-primary p-2" value={operation} onChange={event => { clear(); setRequest(''); setMustCheck(false); try { sessionStorage.removeItem(pairingDraftKey); } catch { /* Storage unavailable; public draft remains transient. */ } setOperation(event.target.value as 'enroll' | 'associate'); }}><option value="enroll">Enroll a new Worker</option><option value="associate">Associate an existing Worker safely</option></select></label>
      <p>{operation === 'enroll' ? 'Supported installer: Ubuntu 24.04 x86_64. Use the matching published release and verified checksum, pairing as the service account. Source builds require a matching local Worker.' : 'Drain and reconcile leases with the previous Server, then stop the service before association. Identity, configuration, history and uncertain resources are retained. Previous Server credentials are not revoked. Local policy requires node administrator consent.'}</p></>}
    {step === 2 && <><p>Run on the intended node with explicit local consent and a trusted Server certificate.</p><pre className="whitespace-pre-wrap break-all text-xs text-secondary">{release.error ?? registrationInstruction(release.data?.version ?? '', operation, window.location.origin)}</pre></>}
    {step >= 3 && <><label className="text-sm text-secondary">Public pairing request<textarea aria-label="Public pairing request" className="w-full rounded-lg border border-secondary bg-primary p-2" value={request} onChange={event => { clear(); setWorkerId(''); setRequest(event.target.value); try { const value = readPairingRequest(event.target.value, operation, window.location.origin); sessionStorage.setItem(pairingDraftKey, JSON.stringify(value)); } catch { try { sessionStorage.removeItem(pairingDraftKey); } catch { /* Storage unavailable. */ } } }} disabled={pending} /></label>
      <Button isDisabled={pending || mustCheck || runtime.locked(resource)} onPress={() => { void act(true); }}>Authorize pairing</Button>
      {authorization && <><Input label="Short-lived authorization" type={reveal ? 'text' : 'password'} value={authorization} isReadOnly autoComplete="off" /><Button color="secondary" onPress={() => setReveal(!reveal)}>Reveal / hide authorization</Button></>}
      <Button color="secondary" isDisabled={pending} onPress={() => { void act(false); }}>Check progress</Button>
      {workerId && <Button href={`/workers/${encodeURIComponent(workerId)}?step=preparation`}>Continue preparation</Button>}</>}
    {message && <Notice>{message}</Notice>}
    <div className="flex flex-wrap gap-3"><Button color="secondary" isDisabled={pending} onPress={() => { clear(); onClose(); }}>Close</Button>
      {step > 1 && <Button color="secondary" isDisabled={pending} onPress={() => { clear(); setStep(step - 1); }}>Back</Button>}
      {step < 3 && <Button onPress={() => setStep(step + 1)}>Next</Button>}</div>
  </FormDialog>;
}
