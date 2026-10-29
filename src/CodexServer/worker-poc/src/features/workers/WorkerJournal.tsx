import { useEffect, useState } from 'react';
import { useApiRead } from '../../shared/api/session';
import { Notice } from '../../shared/Presentation';
import { t, useLanguage } from '../../shared/i18n';

export type Journal = { state: string; lines: string[] };
export function journal(value: unknown): Journal {
  const item = value as Journal;
  if (!item || !['pending', 'ready', 'unsupported', 'error', 'unavailable', 'busy'].includes(item.state) ||
    !Array.isArray(item.lines) || item.lines.length > 200 || item.lines.some(line => typeof line !== 'string' || line.length > 1000)) throw new Error('Invalid journal response.');
  return item;
}
export function JournalContent({ data, error, timedOut = false }: { data?: Journal; error?: string; timedOut?: boolean }) {
  useLanguage();
  if (error || timedOut) return <Notice error>{error ?? t('workers.journalTimeout')}</Notice>;
  if (!data || data.state === 'pending') return <p role="status">{t('workers.journalLoading')}</p>;
  if (data.state !== 'ready') {
    const message = data.state === 'unsupported' ? t('workers.journalUnsupported')
      : data.state === 'unavailable' ? t('workers.journalUnavailable')
        : data.state === 'busy' ? t('workers.journalBusy')
          : data.state === 'error' ? t('workers.journalError') : t('workers.journalFailure');
    return <Notice error>{message}</Notice>;
  }
  if (!data.lines.length) return <p role="status">{t('workers.journalEmpty')}</p>;
  return <pre tabIndex={0} aria-label={t('workers.journalLabel')} style={{ maxHeight: '32rem', overflow: 'auto', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{data.lines.join('\n')}</pre>;
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
export function WorkerJournal({ workerId, openRequest = 0 }: { workerId: string; openRequest?: number }) {
  useLanguage();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (openRequest > 0) setOpen(true); }, [openRequest]);
  return <section className="poc-card"><button type="button" aria-expanded={open} onClick={() => setOpen(!open)}>{t('workers.journalHeading')}</button>
    {open && <JournalRead key={workerId} workerId={workerId} />}</section>;
}
