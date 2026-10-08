import { t, useLanguage } from '../../shared/i18n';
import { createContext, useContext, useState, type ReactNode } from 'react';
import { useRuntime, useSession } from '../../shared/api/session';
import { queryKeys } from '../../shared/api/runtime';
import { workers } from '../../shared/api/validation';
import { credential, credentials, credentialPath, credentialOutcome, type CredentialAttempt } from './contracts';
function useOwner() {
  const runtime = useRuntime(); const session = useSession();
  const [attempts, setAttempts] = useState<Record<string, CredentialAttempt>>({});
  const [generation, setGeneration] = useState(session.generation);
  const [message, setMessage] = useState('');
  if (generation !== session.generation) { setGeneration(session.generation); setAttempts({}); setMessage(''); }
  function forget(key: string) { setAttempts(old => { const next = { ...old }; delete next[key]; return next; }); }
  async function submit(attempt: CredentialAttempt, secret?: string) {
    const key = `credential:${attempt.kind === 'create' ? 'new' : attempt.before.id}`;
    if (runtime.locked(key)) throw Error(t("settings.refreshAuthoritativeStateBeforeRetrying"));
    setAttempts(old => ({ ...old, [key]: attempt }));
    const path = attempt.kind === 'create' ? '/api/v1/credentials' : credentialPath(attempt.before.id) + (attempt.kind === 'replace' ? '/secret' : attempt.kind === 'assign' ? '/assignment' : '/revoke');
    const body = attempt.kind === 'create' ? { provider: attempt.provider, type: attempt.type, secret: { value: secret } }
      : attempt.kind === 'replace' ? { value: secret } : attempt.kind === 'assign' ? { workerId: attempt.workerId } : undefined;
    try {
      await runtime.mutate(key, path, attempt.kind === 'replace' || attempt.kind === 'assign' ? 'PUT' : 'POST', body, value => {
        const saved = credential(value);
        if (!credentialOutcome(attempt, [saved])) throw Error(t("settings.unexpectedCredentialConfirmation"));
        return saved;
      }, async signal => {
        if (attempt.kind === 'create') attempt.existingIds = (await runtime.read('/api/v1/credentials', signal, credentials)).map(c => c.id);
        else {
          const current = await runtime.read(credentialPath(attempt.before.id), signal, credential);
          if (current.id !== attempt.before.id || current.version !== attempt.before.version || current.status !== attempt.before.status || current.assignedWorkerId !== attempt.before.assignedWorkerId) throw Error(t("settings.credentialChangedRefreshBeforeContinuing"));
          if (attempt.kind === 'assign' && !((await runtime.read('/api/v1/workers', signal, workers)).some(w => w.workerId === attempt.workerId))) throw Error(t("settings.workerUnavailable"));
        }
      });
      forget(key); setMessage(t("settings.credentialActionConfirmedProviderSideAuthorizationIsUnchanged"));
    } catch { if (!runtime.locked(key)) forget(key); setMessage(t("settings.actionCouldNotBeConfirmedRefreshAuthoritativeMetadataBeforeAnotherWrite")); throw Error(t("settings.credentialActionUnavailable")); }
  }
  async function reconcile(key: string) {
    try {
      await runtime.reconcile(key, async signal => {
        const current = await runtime.read('/api/v1/credentials', signal, credentials);
        if (!credentialOutcome(attempts[key], current)) throw Error(t("settings.currentMetadataDoesNotEstablishTheOutcomeLockRetainedInspectServerCredential"));
      });
      forget(key); setMessage(t("settings.credentialOutcomeConfirmedFromCurrentMetadataNoSecretWasRetrieved"));
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(session.generation) });
    } catch { setMessage(t("settings.currentMetadataDoesNotEstablishTheOutcomeLockRetainedInspectServerCredential")); }
  }
  return { attempts, message, submit, reconcile, locked: (id = 'new') => runtime.locked(`credential:${id}`) };
}
const Context = createContext<ReturnType<typeof useOwner> | null>(null);
export function SettingsWorkspace({ children }: { children: ReactNode }) {
  useLanguage(); const owner = useOwner(); return <Context.Provider value={owner}>{children}</Context.Provider>; }
export function useSettings() { const value = useContext(Context); if (!value) throw Error(t("settings.settingsOwnerMissing")); return value; }
