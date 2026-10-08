import { useLanguage } from '../i18n';
import { createContext, useContext, useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { QueryClientProvider, useMutation, useQuery } from '@tanstack/react-query';
import { DashboardRuntime, queryKeys } from './runtime';
import type { Validator } from './client';
const SessionContext = createContext<DashboardRuntime | null>(null);
export function SessionProvider({ children }: { children: ReactNode }) {
  useLanguage();
  const [runtime] = useState(() => new DashboardRuntime());
  useEffect(() => {
    void runtime.session('GET');
    const show = (event: PageTransitionEvent) => { if (event.persisted) void runtime.session('GET'); };
    window.addEventListener('pagehide', runtime.suspend); window.addEventListener('pageshow', show);
    return () => {
      window.removeEventListener('pagehide', runtime.suspend); window.removeEventListener('pageshow', show); runtime.dispose();
    };
  }, [runtime]);
  return <SessionContext.Provider value={runtime}><QueryClientProvider client={runtime.queries}>{children}</QueryClientProvider></SessionContext.Provider>;
}
export function useRuntime() {
  const runtime = useContext(SessionContext);
  if (!runtime) throw new Error('Session provider missing.');
  return runtime;
}
export function useSession() {
  const runtime = useRuntime();
  const state = useSyncExternalStore(runtime.subscribe, runtime.snapshot);
  return { ...state, onSignIn: (token: string) => runtime.session('POST', token),
    onRestore: () => runtime.session('GET'), onSignOut: state.logoutAvailable ? () => runtime.session('DELETE') : undefined };
}
export function useApiRead<T>(path: string, validate: Validator<T>) {
  const runtime = useRuntime(), session = useSession();
  const query = useQuery({ queryKey: queryKeys.read(session.generation, path), enabled: session.authenticated,
    queryFn: ({ signal }) => runtime.read(path, signal, validate) });
  return { data: session.authenticated && !query.isError ? query.data : undefined,
    updatedAt: query.dataUpdatedAt, error: query.error?.message, loading: query.isPending, stale: query.isStale, refetch: query.refetch };
}
// Callers provide fresh uncached Server checks and an explicit reconciliation
// read. Timers, cache refreshes and render can never clear an uncertain lock.
export function useApiMutation<T>(resource: string, path: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', validate: Validator<T>, authoritativeCheck: (signal: AbortSignal) => Promise<void>) {
  const runtime = useRuntime(); useSession();
  const mutation = useMutation({ mutationFn: (body: unknown) => runtime.mutate(resource, path, method, body, validate, authoritativeCheck), retry: false });
  return { ...mutation, locked: runtime.locked(resource), reconcile: (read: (signal: AbortSignal) => Promise<void>) => runtime.reconcile(resource, read) };
}
