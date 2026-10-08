import { useApiRead, useSession } from './api/session';
import { serverStatus } from './api/validation';
import { StatusBadge } from './Presentation';
import type { ServerStatus as Status } from './api/contracts';

export function serverStatusPresentation(data: Status | undefined, error: string | undefined, live: string) {
  if (error) return { label: 'Server status unavailable · refresh failed', tone: 'warning' as const };
  if (!data) return { label: 'Server status unknown', tone: 'gray' as const };
  if (data.state === 'offline') return { label: 'Server offline', tone: 'error' as const };
  if (live !== 'Live · connected') return { label: `Server ${data.state} · live updates unavailable`, tone: 'warning' as const };
  if (data.state === 'running') return { label: 'Server live', tone: 'success' as const };
  return { label: `Server ${data.state}`, tone: 'gray' as const };
}
export function ServerStatusView({ data, error, live, updatedAt }: { data?: Status; error?: string; live: string; updatedAt: number }) {
  const status = serverStatusPresentation(data, error, live);
  return <section aria-label="Server connection" className="space-y-2 border-t border-secondary pt-3 text-xs text-tertiary">
    <StatusBadge tone={status.tone}>{status.label}</StatusBadge>
    <p>Last updated: {updatedAt > 0 ? <time dateTime={new Date(updatedAt).toISOString()}>{new Date(updatedAt).toLocaleString()}</time> : 'Unknown'}</p>
    {error && updatedAt > 0 && <p>Last successful observation is stale.</p>}
  </section>;
}
export function ServerStatus() {
  const query = useApiRead('/api/status', serverStatus);
  const session = useSession();
  return <ServerStatusView data={query.data} error={query.error} live={session.live} updatedAt={query.updatedAt} />;
}
