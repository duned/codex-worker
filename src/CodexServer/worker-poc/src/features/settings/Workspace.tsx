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
    if (runtime.locked(key)) throw Error('Refresh authoritative state before retrying.');
    setAttempts(old => ({ ...old, [key]: attempt }));
    const path = attempt.kind === 'create' ? '/api/v1/credentials' : credentialPath(attempt.before.id) + (attempt.kind === 'replace' ? '/secret' : attempt.kind === 'assign' ? '/assignment' : '/revoke');
    const body = attempt.kind === 'create' ? { provider: attempt.provider, type: attempt.type, secret: { value: secret } }
      : attempt.kind === 'replace' ? { value: secret } : attempt.kind === 'assign' ? { workerId: attempt.workerId } : undefined;
    try {
      await runtime.mutate(key, path, attempt.kind === 'replace' || attempt.kind === 'assign' ? 'PUT' : 'POST', body, value => {
        const saved = credential(value);
        if (!credentialOutcome(attempt, [saved])) throw Error('Unexpected credential confirmation.');
        return saved;
      }, async signal => {
        if (attempt.kind === 'create') attempt.existingIds = (await runtime.read('/api/v1/credentials', signal, credentials)).map(c => c.id);
        else {
          const current = await runtime.read(credentialPath(attempt.before.id), signal, credential);
          if (current.id !== attempt.before.id || current.version !== attempt.before.version || current.status !== attempt.before.status || current.assignedWorkerId !== attempt.before.assignedWorkerId) throw Error('Credential changed. Refresh before continuing.');
          if (attempt.kind === 'assign' && !((await runtime.read('/api/v1/workers', signal, workers)).some(w => w.workerId === attempt.workerId))) throw Error('Worker unavailable.');
        }
      });
      forget(key); setMessage('Credential action confirmed. Provider-side authorization is unchanged.');
    } catch { if (!runtime.locked(key)) forget(key); setMessage('Action could not be confirmed. Refresh authoritative metadata before another write.'); throw Error('Credential action unavailable.'); }
  }
  async function reconcile(key: string) {
    try {
      await runtime.reconcile(key, async signal => {
        const current = await runtime.read('/api/v1/credentials', signal, credentials);
        if (!credentialOutcome(attempts[key], current)) throw Error('Current metadata does not establish the outcome. Lock retained; inspect Server credential administration.');
      });
      forget(key); setMessage('Credential outcome confirmed from current metadata. No secret was retrieved.');
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(session.generation) });
    } catch { setMessage('Current metadata does not establish the outcome. Lock retained; inspect Server credential administration.'); }
  }
  return { attempts, message, submit, reconcile, locked: (id = 'new') => runtime.locked(`credential:${id}`) };
}
const Context = createContext<ReturnType<typeof useOwner> | null>(null);
export function SettingsWorkspace({ children }: { children: ReactNode }) { const owner = useOwner(); return <Context.Provider value={owner}>{children}</Context.Provider>; }
export function useSettings() { const value = useContext(Context); if (!value) throw Error('Settings owner missing.'); return value; }
