import { useEffect, useRef, useState } from 'react';
import { useApiRead, useRuntime } from '../../shared/api/session';
import { Notice } from '../../shared/Presentation';
import { t, useLanguage } from '../../shared/i18n';
import { Button } from '../../untitled/components/base/buttons/button';

export type Journal = { state: string; lines: string[] };
export function journalPath(workerId: string, refresh = false) {
  return `/api/v1/workers/${encodeURIComponent(workerId)}/journal?lines=100${refresh ? '&refresh=true' : ''}`;
}
export function journal(value: unknown): Journal {
  const item = value as Journal;
  if (!item || !['pending', 'ready', 'unsupported', 'denied', 'error', 'unavailable', 'busy'].includes(item.state) ||
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
          : data.state === 'denied' ? t('workers.journalDenied') : data.state === 'error' ? t('workers.journalError') : t('workers.journalFailure');
    return <Notice error>{message}</Notice>;
  }
  if (!data.lines.length) return <p role="status">{t('workers.journalEmpty')}</p>;
  return <pre tabIndex={0} className="poc-worker-journal-output" aria-label={t('workers.journalLabel')}>{data.lines.join('\n')}</pre>;
}
function JournalRead({ workerId }: { workerId: string }) {
  const runtime = useRuntime();
  const [requestStarted, setRequestStarted] = useState(false);
  const [refreshing, setRefreshing] = useState(true);
  const [refreshError, setRefreshError] = useState<string>();
  const [timedOut, setTimedOut] = useState(false);
  const refreshController = useRef<AbortController | undefined>(undefined);
  const result = useApiRead(journalPath(workerId), journal, requestStarted);
  useEffect(() => {
    const controller = new AbortController();
    refreshController.current = controller;
    void runtime.read(journalPath(workerId, true), controller.signal, journal)
      .then(() => { if (!controller.signal.aborted) setRequestStarted(true); })
      .catch(() => { if (!controller.signal.aborted) { setRefreshError(t('workers.journalFailure')); setRequestStarted(true); } })
      .finally(() => {
        if (!controller.signal.aborted) setRefreshing(false);
        if (refreshController.current === controller) refreshController.current = undefined;
      });
    return () => { controller.abort(); refreshController.current?.abort(); refreshController.current = undefined; };
  }, [runtime, workerId]);
  useEffect(() => {
    if (!requestStarted || result.data?.state !== 'pending') return;
    const timer = setTimeout(() => setTimedOut(true), 80000);
    return () => clearTimeout(timer);
  }, [requestStarted, result.data?.state]);
  useEffect(() => {
    if (!requestStarted || result.data?.state !== 'pending' || timedOut || refreshing) return;
    const timer = setTimeout(() => { void result.refetch(); }, 2000);
    return () => clearTimeout(timer);
  }, [requestStarted, result.data, result.refetch, result.fetching, result.updatedAt, timedOut, refreshing]);
  async function refresh() {
    if (refreshing || result.fetching) return;
    const controller = new AbortController();
    refreshController.current?.abort();
    refreshController.current = controller;
    setRefreshing(true);
    setRefreshError(undefined);
    setTimedOut(false);
    try {
      await runtime.read(journalPath(workerId, true), controller.signal, journal);
      if (!controller.signal.aborted) {
        setRequestStarted(true);
        await result.refetch();
      }
    } catch {
      if (!controller.signal.aborted) setRefreshError(t('workers.journalFailure'));
    } finally {
      if (!controller.signal.aborted) setRefreshing(false);
      if (refreshController.current === controller) refreshController.current = undefined;
    }
  }
  return <div className="poc-worker-journal-content">
    <div className="poc-worker-journal-actions"><Button color="secondary" size="sm" isDisabled={refreshing || result.fetching} aria-busy={refreshing || result.fetching} onPress={() => { void refresh(); }}>{refreshing || result.fetching ? t('workers.journalLoading') : t('workers.journalRefresh')}</Button></div>
    <JournalContent data={refreshing ? undefined : result.data} error={refreshError ?? result.error} timedOut={timedOut && result.data?.state === 'pending'} />
  </div>;
}
export function WorkerJournal({ workerId, openRequest = 0 }: { workerId: string; openRequest?: number }) {
  useLanguage();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (openRequest > 0) setOpen(true); }, [openRequest]);
  return <section className="poc-worker-information-section" aria-labelledby="poc-worker-journal-heading">
    <div className="poc-worker-information-heading"><h3 id="poc-worker-journal-heading">{t('workers.journalHeading')}</h3>
      <Button color="secondary" size="sm" aria-expanded={open} onPress={() => setOpen(!open)}>{open ? t('workers.hideJournal') : t('workers.loadJournal')}</Button></div>
    {open && <JournalRead key={workerId} workerId={workerId} />}</section>;
}
