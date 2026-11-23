import { useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useApiRead } from '../shared/api/session';
import type { Validator } from '../shared/api/client';
import { record } from '../shared/api/validation';
import { useExecutions, useWorkers } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { isActive, isOnline, scheduling, schedulingBlockerText, schedulingTone, slots } from '../shared/model';
import { Ago, Card, DotText, Empty, Filters, HeadRow, matches, PageHeader, Pill, ReadState, Row, RowLink, Rows, Slots } from '../shared/ui';
import { ConnectWorkerPanel } from './WorkerPanel';

export interface Observer { observerId: string; name: string | null; version: string | null; freshness: string; lastReceivedAtUtc: string | null; maximumCapacity: number | null; activeExecutions: number | null; reportedReadiness: string | null }
export const observers: Validator<Observer[]> = value => {
  const items = Array.isArray(value) ? value : record(value).items;
  if (!Array.isArray(items)) throw new Error('Invalid observers.');
  items.forEach(item => { if (typeof record(item).observerId !== 'string') throw new Error('Invalid observer.'); });
  return items as Observer[];
};
const columns = 'md:grid-cols-[minmax(0,1.5fr)_minmax(0,1.1fr)_minmax(0,1.6fr)_90px_minmax(0,1.1fr)]';

export function WorkersPage() {
  useLanguage();
  const workers = useWorkers(), executions = useExecutions(), standalone = useApiRead('/api/v1/observers?limit=100', observers);
  const [query, setQuery] = useState(''), [status, setStatus] = useState(''), [connecting, setConnecting] = useState(false);
  const running = (executions.data ?? []).filter(isActive);
  const rows = (workers.data ?? []).filter(worker => {
    const tags = [isOnline(worker) ? 'online' : 'offline', scheduling(worker)];
    return (!status || tags.includes(status)) && matches(`${worker.displayName ?? ''} ${worker.workerId}`, query);
  });
  const standaloneRows = (standalone.data ?? []).filter(item => (!status || status === (item.freshness === 'fresh' ? 'online' : 'offline')) && matches(`${item.name ?? ''} ${item.observerId}`, query));
  return <>
    <PageHeader title={t('workers.title')} description={t('workers.description')} actions={<Button color="primary" onClick={() => setConnecting(true)}>{t('workers.connect')}</Button>} />
    <Card flush>
      <Filters query={query} onQuery={setQuery} status={status} onStatus={setStatus} placeholder={t('workers.search')} options={[
        { value: 'online', label: t('workers.online') }, { value: 'offline', label: t('workers.offline') }, { value: 'accepting', label: t('scheduling.accepting') },
        { value: 'paused', label: t('scheduling.paused') }, { value: 'blocked', label: t('scheduling.blocked') }]} />
      <ReadState read={workers}><Rows>
        <HeadRow className={columns}><span>{t('workers.worker')}</span><span>{t('workers.connection')}</span><span>{t('workers.activity')}</span><span>{t('workers.slots')}</span><span>{t('workers.scheduling')}</span></HeadRow>
        {rows.map(worker => {
          const mine = running.filter(item => item.assignedWorkerId === worker.workerId), state = scheduling(worker), use = slots(worker);
          return <RowLink key={worker.workerId} to={`/workers/${encodeURIComponent(worker.workerId)}`} className={`grid-cols-1 ${columns}`}>
            <span className="min-w-0"><span className="block font-medium text-primary">{worker.displayName || worker.workerId.slice(0, 8)}</span>
              <span className="text-sm text-tertiary">{t('workers.managed')}{worker.workerVersion ? ` · v${worker.workerVersion}` : ''}</span></span>
            <span><DotText tone={isOnline(worker) ? 'ok' : 'idle'}>{t(isOnline(worker) ? 'workers.online' : 'workers.offline')}</DotText>
              <span className="block text-sm text-tertiary">{t('workers.lastSignal')} <Ago value={worker.lastHeartbeatAtUtc} /></span></span>
            <span className="text-sm text-tertiary">{!isOnline(worker) ? t('workers.notAvailable') : (worker.schedulingBlocker && schedulingBlockerText(worker.schedulingBlocker)) ?? (mine.length
              ? t('workers.workingOn', { list: mine.map(item => `#${item.workReference?.id}`).join(', ') }) : t('workers.idle'))}</span>
            <span className="flex flex-col gap-1"><span className="text-sm font-medium text-primary tabular-nums">{use.used} / {use.total}</span><Slots used={use.used} total={use.total} /></span>
            <span><Pill tone={schedulingTone[state]}>{t(`scheduling.${state}`)}</Pill></span>
          </RowLink>;
        })}
        {standaloneRows.map(item => <Row key={item.observerId} className={`grid-cols-1 ${columns}`}>
          <span className="min-w-0"><span className="block font-medium text-primary">{item.name || item.observerId.slice(0, 8)}</span>
            <span className="text-sm text-tertiary">{t('workers.standalone')}{item.version ? ` · v${item.version}` : ''}</span></span>
          <span><DotText tone={item.freshness === 'fresh' ? 'ok' : 'idle'}>{t(item.freshness === 'fresh' ? 'workers.online' : 'workers.offline')}</DotText>
            <span className="block text-sm text-tertiary">{t('workers.lastSignal')} <Ago value={item.lastReceivedAtUtc ?? undefined} /></span></span>
          <span className="text-sm text-tertiary">{t('workers.standaloneActivity', { n: item.activeExecutions ?? 0 })}</span>
          <span className="flex flex-col gap-1"><span className="text-sm font-medium text-primary tabular-nums">{item.activeExecutions ?? 0} / {item.maximumCapacity ?? 1}</span><Slots used={item.activeExecutions ?? 0} total={item.maximumCapacity ?? 1} /></span>
          <span><Pill tone="idle">{t('workers.ownSchedule')}</Pill></span>
        </Row>)}
        {!rows.length && !standaloneRows.length && <Empty>{workers.data?.length ? t('common.noMatches') : t('workers.none')}</Empty>}
      </Rows></ReadState>
    </Card>
    <ConnectWorkerPanel open={connecting} onClose={() => setConnecting(false)} />
  </>;
}
