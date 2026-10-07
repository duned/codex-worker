import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

interface SessionDocument { csrfToken: string; expiresAtUtc: string }
interface SessionState { authenticated: boolean; pending: boolean; message: string }
interface Session extends SessionState {
  onSignIn(token: string): Promise<void>;
  onRestore(): Promise<void>;
  onSignOut?: () => Promise<void>;
  read<T>(path: string, signal: AbortSignal): Promise<T>;
}
const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ authenticated: false, pending: true, message: 'Checking administration session…' });
  const owner = useRef({ generation: 0, controller: new AbortController(), csrf: '', expires: 0 });
  const reset = useCallback(() => {
    owner.current.controller.abort();
    owner.current = { generation: owner.current.generation + 1, controller: new AbortController(), csrf: '', expires: 0 };
    return owner.current;
  }, []);
  const sessionRequest = useCallback(async (method: 'GET' | 'POST' | 'DELETE', token?: string) => {
    const csrf = owner.current.csrf;
    const current = reset();
    setState({ authenticated: false, pending: true, message: method === 'DELETE' ? 'Signing out…' : 'Checking administration session…' });
    try {
      const headers = new Headers();
      if (token) headers.set('Authorization', `Bearer ${token}`);
      if (method === 'DELETE') headers.set('X-Codex-CSRF', csrf);
      const response = await fetch('/api/v1/administration/session', {
        method, headers, credentials: 'same-origin', cache: 'no-store',
        signal: AbortSignal.any([current.controller.signal, AbortSignal.timeout(15000)])
      });
      if (current !== owner.current) return;
      if (!response.ok) throw new Error(`Session request rejected (HTTP ${response.status}). Use the configured HTTPS administration origin and check the Server session configuration.`);
      if (method === 'DELETE') {
        setState({ authenticated: false, pending: false, message: 'Signed out.' });
        return;
      }
      const document: SessionDocument = await response.json();
      if (current !== owner.current) return;
      const expires = Date.parse(document.expiresAtUtc);
      if (typeof document.csrfToken !== 'string' || !document.csrfToken || !Number.isFinite(expires) || expires <= Date.now()) throw new Error('Invalid Server session response.');
      current.csrf = document.csrfToken; current.expires = expires;
      setState({ authenticated: true, pending: false, message: '' });
    } catch (error) {
      if (current !== owner.current) return;
      // Retain the logout CSRF only in memory so a lost response can be retried.
      if (method === 'DELETE') current.csrf = csrf;
      setState({ authenticated: false, pending: false, message: error instanceof Error ? error.message : 'Session unavailable.' });
    }
  }, [reset]);
  useEffect(() => {
    void sessionRequest('GET');
    return () => { reset(); };
  }, [sessionRequest, reset]);
  useEffect(() => {
    if (!state.authenticated) return;
    const timer = setTimeout(() => {
      reset(); setState({ authenticated: false, pending: false, message: 'Session expired. Sign in again.' });
    }, Math.max(0, owner.current.expires - Date.now()));
    return () => clearTimeout(timer);
  }, [state.authenticated, reset]);
  const read = useCallback(async <T,>(path: string, signal: AbortSignal): Promise<T> => {
    if (!path.startsWith('/api/v1/') || !state.authenticated) throw new Error('Administration sign in required.');
    const current = owner.current;
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store',
      signal: AbortSignal.any([signal, current.controller.signal, AbortSignal.timeout(15000)]) });
    if (current !== owner.current || signal.aborted) throw new DOMException('Read cancelled.', 'AbortError');
    if (response.status === 401 || response.status === 403) {
      reset(); setState({ authenticated: false, pending: false, message: 'Session expired or rejected. Sign in again.' });
      throw new Error('Administration sign in required.');
    }
    if (!response.ok) throw new Error(`Resource unavailable (HTTP ${response.status}).`);
    const result: T = await response.json();
    if (current !== owner.current || signal.aborted) throw new DOMException('Read cancelled.', 'AbortError');
    return result;
  }, [state.authenticated, reset]);
  return <SessionContext.Provider value={{ ...state, read,
    onSignIn: token => sessionRequest('POST', token), onRestore: () => sessionRequest('GET'), onSignOut: state.authenticated || owner.current.csrf ? () => sessionRequest('DELETE') : undefined
  }}>{children}</SessionContext.Provider>;
}
export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) throw new Error('Session provider missing.');
  return session;
}
export function useApiRead<T>(path: string) {
  const { read, authenticated } = useSession();
  const [result, setResult] = useState<{ path: string; data?: T; error?: string }>({ path });
  useEffect(() => {
    const controller = new AbortController();
    setResult({ path });
    if (authenticated) void read<T>(path, controller.signal).then(data => {
      if (!controller.signal.aborted) setResult({ path, data });
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setResult({ path, error: error instanceof Error ? error.message : 'Read unavailable.' });
    });
    return () => controller.abort();
  }, [path, read, authenticated]);
  return result.path === path && authenticated ? result : { path };
}
