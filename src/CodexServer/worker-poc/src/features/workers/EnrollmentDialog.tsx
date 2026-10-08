import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useEffect, useRef, useState } from 'react';
import { FormDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { Input } from '../../shared/Input';
import { Notice } from '../../shared/Presentation';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { pairingAuthorization, version, workers } from '../../shared/api/validation';
import { readPairingRequest, registrationInstruction, restorePairingRequest, pairingDraftKey, pairingAcknowledged } from './enrollment';
export function EnrollmentDialog({ onClose }: { onClose(): void }) {
  useLanguage();
  const runtime = useRuntime(), session = useSession(), release = useApiRead('/api/version', version);
  const [draft] = useState(() => { try { return restorePairingRequest(sessionStorage.getItem(pairingDraftKey), window.location.origin); } catch { return undefined; } });
  const [operation, setOperation] = useState<'enroll' | 'associate'>(draft?.operation ?? 'enroll'), [step, setStep] = useState(draft ? 3 : 1);
  const [request, setRequest] = useState(draft ? JSON.stringify(draft) : ''), [authorization, setAuthorization] = useState(''), [reveal, setReveal] = useState(false);
  const [message, setMessage] = useState(draft ? t("workers.publicRequestRestoredResumeTheNodeWithEmptyInputAndCheckProgress") : ''), [workerId, setWorkerId] = useState(''), [pending, setPending] = useState(false);
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
          if (registered && operation === 'enroll') throw new Error(t("workers.identityRegistered"));
          if ((registered?.activeAssignments ?? 0) > 0) throw new Error(t("workers.drainBeforeAssociation"));
        });
        if (!current()) return;
        setAuthorization(result.authorization); setStep(4);
        expiry.current = setTimeout(() => { setAuthorization(''); setReveal(false); }, result.lifetimeSeconds * 1000);
        setMessage(t("workers.pasteThisShortLivedAuthorizationIntoTheHiddenNodePromptWaitFor"));
      } else {
        await runtime.reconcile(resource, async signal => {
          const inventory = await runtime.read('/api/v1/workers', signal, workers);
          if (!current()) return;
          const registered = inventory.find(item => item.workerId === value.workerId);
          const connected = pairingAcknowledged(registered);
          setWorkerId(connected ? value.workerId : '');
          setMessage(connected ? t("workers.registrationAcknowledgedAndCommunicationObservedReadinessAndSchedulingRemainSeparate")
            : t("workers.waitingForRegistrationAcknowledgementOrCommunicationResumeTheSameNodeOperationWith"));
        });
        if (current()) setMustCheck(false);
      }
    } catch (error) { if (current()) setMessage(t('workers.enrollmentError', { error: error instanceof Error ? localizeText(error.message) : t("workers.resultUnavailable") })); }
    finally { busy.current = false; if (current()) setPending(false); }
  }
  return <FormDialog isOpen title={t("workers.addWorker")} description={t("workers.machineLocalRegistrationAuthorizeResumeNodeAcknowledgement")} onClose={onClose} pending={pending}>
    <Notice>{t("workers.step")}{' '}{step}{' '}{t("workers.of4")}{' '}{[t('workers.machine'), t("workers.localRegistration"), t("workers.authorizeResume"), t("workers.nodeAcknowledgement")][step - 1]}</Notice>
    {step === 1 && <><label className="text-sm text-secondary">{t("workers.machine")}<select aria-label={t("workers.machine")} className="w-full rounded-lg border border-secondary bg-primary p-2" value={operation} onChange={event => { clear(); setRequest(''); setMustCheck(false); try { sessionStorage.removeItem(pairingDraftKey); } catch { /* Storage unavailable; public draft remains transient. */ } setOperation(event.target.value as 'enroll' | 'associate'); }}><option value="enroll">{t("workers.enrollANewWorker")}</option><option value="associate">{t("workers.associateAnExistingWorkerSafely")}</option></select></label>
      <p>{operation === 'enroll' ? t("workers.supportedInstallerUbuntu2404X8664UseTheMatchingPublishedRelease") : t("workers.drainAndReconcileLeasesWithThePreviousServerThenStopTheService")}</p></>}
    {step === 2 && <><p>{t("workers.runOnTheIntendedNodeWithExplicitLocalConsentAndATrusted")}</p><pre className="whitespace-pre-wrap break-all text-xs text-secondary">{release.error ?? registrationInstruction(release.data?.version ?? '', operation, window.location.origin)}</pre></>}
    {step >= 3 && <><label className="text-sm text-secondary">{t("workers.publicPairingRequest")}<textarea aria-label={t("workers.publicPairingRequest")} className="w-full rounded-lg border border-secondary bg-primary p-2" value={request} onChange={event => { clear(); setWorkerId(''); setRequest(event.target.value); try { const value = readPairingRequest(event.target.value, operation, window.location.origin); sessionStorage.setItem(pairingDraftKey, JSON.stringify(value)); } catch { try { sessionStorage.removeItem(pairingDraftKey); } catch { /* Storage unavailable. */ } } }} disabled={pending} /></label>
      <Button isDisabled={pending || mustCheck || runtime.locked(resource)} onPress={() => { void act(true); }}>{t("workers.authorizePairing")}</Button>
      {authorization && <><Input label={t("workers.shortLivedAuthorization")} type={reveal ? 'text' : 'password'} value={authorization} isReadOnly autoComplete="off" /><Button color="secondary" onPress={() => setReveal(!reveal)}>{t("workers.revealHideAuthorization")}</Button></>}
      <Button color="secondary" isDisabled={pending} onPress={() => { void act(false); }}>{t("workers.checkProgress")}</Button>
      {workerId && <Button href={`/workers/${encodeURIComponent(workerId)}?step=preparation`}>{t("workers.continuePreparation")}</Button>}</>}
    {message && <Notice>{message}</Notice>}
    <div className="flex flex-wrap gap-3"><Button color="secondary" isDisabled={pending} onPress={() => { clear(); onClose(); }}>{t("workers.close")}</Button>
      {step > 1 && <Button color="secondary" isDisabled={pending} onPress={() => { clear(); setStep(step - 1); }}>{t("workers.back")}</Button>}
      {step < 3 && <Button onPress={() => setStep(step + 1)}>{t("workers.next")}</Button>}</div>
  </FormDialog>;
}
