import { useEffect, useState } from 'react';
import { useApiRead } from '../../shared/api/session';
import { Notice } from '../../shared/Presentation';

export type Journal = { state: string; lines: string[] };
export function journal(value: unknown): Journal {
  const item = value as Journal;
  if (!item || !['pending', 'ready', 'unsupported', 'error', 'unavailable', 'busy'].includes(item.state) ||
    !Array.isArray(item.lines) || item.lines.length > 200 || item.lines.some(line => typeof line !== 'string' || line.length > 1000)) throw new Error('Invalid journal response.');
  return item;
}
export function JournalContent({ data, error, timedOut = false }: { data?: Journal; error?: string; timedOut?: boolean }) {
  if (error || timedOut) return <Notice error>{error ?? 'Worker did not respond in time. Close and reopen to try again.'}</Notice>;
  if (!data || data.state === 'pending') return <p role="status">Loading recent Worker journal…</p>;
  if (data.state !== 'ready') return <Notice error>{({ unsupported: 'Journal is supported only for the packaged Linux systemd Worker service.', unavailable: 'Worker is unavailable.', busy: 'Journal retrieval is busy. Try again.', error: 'Could not read the Worker journal. Check service journal permissions.' })[data.state] ?? 'Journal retrieval failed.'}</Notice>;
  if (!data.lines.length) return <p role="status">No journal entries.</p>;
  return <pre tabIndex={0} aria-label="Worker journal entries" style={{ maxHeight: '32rem', overflow: 'auto', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{data.lines.join('\n')}</pre>;
}
function JournalRead({ workerId }: { workerId: string }) {
  const result = useApiRead(`/api/v1/workers/${encodeURIComponent(workerId)}/journal`, journal);
  const [timedOut, setTimedOut] = useState(false);
  useEffect(() => { const timer = setTimeout(() => setTimedOut(true), 80000); return () => clearTimeout(timer); }, []);
  useEffect(() => {
    if (result.data?.state !== 'pending' || timedOut) return;
    const timer = setTimeout(() => { void result.refetch(); }, 2000);
    return () => clearTimeout(timer);
  }, [result.data, result.refetch, result.fetching, timedOut]);
  return <JournalContent data={result.data} error={result.error} timedOut={timedOut && result.data?.state === 'pending'} />;
}
export function WorkerJournal({ workerId }: { workerId: string }) {
  const [open, setOpen] = useState(false);
  return <section className="poc-card"><button type="button" aria-expanded={open} onClick={() => setOpen(!open)}>Recent Worker journal · latest 100 lines</button>
    {open && <JournalRead key={workerId} workerId={workerId} />}</section>;
}
