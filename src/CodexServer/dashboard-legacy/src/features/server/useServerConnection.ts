import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useRuntime, useSession } from '../../shared/api/session';
import { queryKeys } from '../../shared/api/runtime';
import { connectionPath, connectionSnapshot, type Challenge } from './connection';
/** The shared runtime polls this active query; only safe metadata is cached. */
export function useServerConnection() {
  const runtime = useRuntime(), session = useSession();
  const mounted = useRef(true);
  const [challenge, setChallenge] = useState<Challenge>();
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const query = useQuery({ queryKey: [...queryKeys.read(session.generation, connectionPath), 'transient-view'], enabled: session.authenticated,
    queryFn: async ({ signal }) => {
      const snapshot = await runtime.read(connectionPath, signal, value => connectionSnapshot(value, Date.now()));
      if (mounted.current && !signal.aborted) setChallenge(snapshot.challenge);
      return snapshot.metadata;
    } });
  useEffect(() => {
    if (!challenge) return;
    const timer = setTimeout(() => setChallenge(undefined), Math.max(1, Math.min(2147483647, challenge.deadline - Date.now())));
    return () => clearTimeout(timer);
  }, [challenge]);
  return { data: session.authenticated && !query.isError ? query.data : undefined,
    challenge: session.authenticated && !query.isError && challenge && challenge.deadline > Date.now() ? challenge : undefined,
    error: query.error?.message, refetch: query.refetch };
}
