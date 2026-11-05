import { useState } from 'react';
import { serverStatus } from '../../shared/api/validation';
import { Link } from 'react-aria-components';
import { t, useLanguage, localizeText } from '../../shared/i18n';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { observers, observerDetail, observerHistory } from '../../shared/api/observers';
import type { ObserverSummary, ObserverExecutionObservation } from '../../shared/api/contracts';
import { PageHeading, Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import { ConfirmationDialog } from '../../shared/Dialogs';
import { ExternalLink } from '../../shared/Actions';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { observerConnection, observerIssueUrl } from './model';
import { PairingDialog } from './PairingDialog';
import { duration } from '../../model.js';

const time = (value?: string | null) => value ? new Date(value).toLocaleString() : t('shared.unknown');

function ObserverStatus({ observer, unavailable = false }: { observer: ObserverSummary; unavailable?: boolean }) {
  const connection = observerConnection(observer);
  return <div className="space-y-3">
    <div className="flex flex-wrap gap-2"><StatusBadge>{t('observers.standalone')}</StatusBadge><StatusBadge>{t('observers.readOnly')}</StatusBadge>
      <StatusBadge tone={unavailable ? 'gray' : connection === 'connected' ? 'success' : connection === 'stale' || connection === 'pending' || connection === 'awaiting' ? 'warning' : 'gray'}>{unavailable ? t('observers.unavailable') : t(`observers.state.${connection}`)}</StatusBadge></div>
    <p className="text-sm text-secondary">{unavailable ? t('observers.retained') : t(`observers.next.${connection}`)}</p>
    <dl className="grid gap-3 text-sm text-secondary sm:grid-cols-2">
      <div><dt className="text-tertiary">{t('observers.heartbeat')}</dt><dd>{time(observer.lastReceivedAtUtc)}</dd></div>
      <div><dt className="text-tertiary">{t('observers.snapshot')}</dt><dd>{time(observer.snapshotReceivedAtUtc)}</dd></div>
      <div><dt className="text-tertiary">{t('observers.lifecycle')}</dt><dd>{localizeText(observer.reportedLifecycle ?? t('shared.unknown'))}</dd></div>
      <div><dt className="text-tertiary">{t('observers.readiness')}</dt><dd>{localizeText(observer.reportedReadiness ?? t('shared.unknown'))}</dd></div>
      <div><dt className="text-tertiary">{t('observers.version')}</dt><dd>{observer.version ?? t('shared.unknown')}</dd></div>
      <div><dt className="text-tertiary">{t('observers.capacity')}</dt><dd>{observer.activeExecutions ?? t('shared.unknown')} / {observer.maximumCapacity ?? t('shared.unknown')}</dd></div>
      {observer.pairingExpiresAtUtc && <div><dt>{t('observers.authorization')}</dt><dd>{t('observers.expires', { time: time(observer.pairingExpiresAtUtc) })}</dd></div>}
    </dl>
  </div>;
}

function Revoke({ observerId, revoked, inventoryPath }: { observerId: string; revoked: boolean; inventoryPath?: string }) {
  const [confirm, setConfirm] = useState(false), [error, setError] = useState(false);
  const runtime = useRuntime(), session = useSession();
  const path = `/api/v1/observers/${encodeURIComponent(observerId)}/revoke`;
  return session.authenticated && !revoked ? <>
    <Button color="secondary-destructive" isDisabled={runtime.locked(path)} onPress={() => setConfirm(true)}>{t('observers.revoke')}</Button>
    {runtime.locked(path) && <Button color="secondary" onPress={() => { void runtime.reconcile(path, async signal => {
      const state = inventoryPath
        ? (await runtime.read(inventoryPath, signal, observers)).find(item => item.observerId === observerId)?.registrationState
        : (await runtime.read(`/api/v1/observers/${encodeURIComponent(observerId)}`, signal, observerDetail)).observer.registrationState;
      if (state !== 'revoked') throw new Error();
      setError(false);
    }).catch(() => setError(true)); }}>{t('observers.checkRevocation')}</Button>}
    {error && <Notice error>{t('observers.revocationError')}</Notice>}
    {confirm && <ConfirmationDialog isOpen title={t('observers.revoke')} description={t('observers.revokeHelp')} actionLabel={t('observers.revoke')} destructive onClose={() => setConfirm(false)} onSubmit={async () => {
      await runtime.mutate(path, path, 'POST', undefined, value => { if (value !== null) throw new Error(); return null; });
    }} />}
  </> : null;
}

export function ObserversSection() {
  useLanguage();
  const session = useSession(), [pair, setPair] = useState(false), [offset, setOffset] = useState(0);
  const inventory = useApiRead(`/api/v1/observers?limit=50&offset=${offset}`, observers), server = useApiRead('/api/status', serverStatus);
  if (!session.authenticated) return null;
  return <section className="mt-8 space-y-4" aria-labelledby="standalone-workers">
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 id="standalone-workers" className="text-lg font-semibold text-primary">{t('observers.title')}</h2>
      <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={inventory.fetching} onPress={() => { void inventory.refetch(); }}>{t('observers.refresh')}</Button><Button color="secondary" onPress={() => setPair(true)}>{t('observers.add')}</Button></div>
    </div>
    <p className="text-sm text-tertiary">{t('observers.scope')}</p>
    {inventory.error && <Notice error>{t('observers.readError')}</Notice>}
    {!inventory.data ? <ViewState title={inventory.error ? t('observers.unavailable') : t('observers.loading')} /> : inventory.data.length === 0 ? <Notice>{t('observers.empty')}</Notice> :
      <ul className="grid gap-4 lg:grid-cols-2">{inventory.data.map(observer => <li key={observer.observerId}><TableCard.Root><div className="space-y-4 p-5">
        <h3 className="break-all font-semibold text-primary">{observer.name ?? observer.observerId}</h3>
        <ObserverStatus observer={observer} unavailable={!!inventory.error || !!server.error} />
        <div className="flex flex-wrap gap-3">{!observer.pairingExpiresAtUtc && <Link className="text-sm font-semibold text-brand-secondary" href={`/workers?observer=${encodeURIComponent(observer.observerId)}`}>{t('observers.details')}</Link>}
          <Revoke inventoryPath={`/api/v1/observers?limit=50&offset=${offset}`} observerId={observer.observerId} revoked={observer.registrationState === 'revoked'} /></div>
      </div></TableCard.Root></li>)}</ul>}
    <div className="flex flex-wrap items-center gap-3"><span className="text-xs text-tertiary">{t('observers.window')}</span>
      <Button color="secondary" isDisabled={offset === 0 || inventory.fetching} onPress={() => setOffset(offset - 50)}>{t('observers.previous')}</Button>
      <Button color="secondary" isDisabled={inventory.data?.length !== 50 || inventory.fetching || offset >= 10000} onPress={() => setOffset(offset + 50)}>{t('observers.next')}</Button>
    </div>
    {pair && <PairingDialog key={session.generation} onClose={() => setPair(false)} />}
  </section>;
}

function ExecutionList({ items, current = false, stale = false, currentIds = [] }: {
  items: ObserverExecutionObservation[]; current?: boolean; stale?: boolean; currentIds?: string[];
}) {
  return items.length === 0 ? <Notice>{t('observers.noExecutions')}</Notice> : <ul className="divide-y divide-secondary">{items.map(({ execution: item, observedAtUtc, receivedAtUtc }) => {
    const href = observerIssueUrl(item.repository, item.issueNumber);
    return <li key={item.localExecutionId} className="space-y-3 py-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="min-w-0 max-w-full break-words text-sm text-secondary">{item.project || t('shared.unknown')} · {item.repository || t('shared.unknown')}</p>
        <div className="flex flex-wrap gap-2">
          {current && <StatusBadge tone={stale ? 'warning' : 'info'}>{t(stale ? 'observers.lastReportedWork' : 'observers.working')}</StatusBadge>}
          {!current && currentIds.includes(item.localExecutionId) && <StatusBadge>{t('observers.alsoCurrent')}</StatusBadge>}
          <StatusBadge tone={current && !stale ? 'warning' : 'gray'}>{localizeText(item.stage || t('shared.unknown'))}</StatusBadge>
          {item.outcome && <StatusBadge>{localizeText(item.outcome)}</StatusBadge>}
        </div>
      </div>
      <h3 className={`break-words font-semibold text-primary ${current ? 'text-xl' : 'text-base'}`}>{href ? <ExternalLink href={href}><span className="min-w-0 break-words">#{item.issueNumber}{item.issueTitle ? ` · ${item.issueTitle}` : ''}</span></ExternalLink> : <>#{item.issueNumber}{item.issueTitle ? ` · ${item.issueTitle}` : ''}</>}</h3>
      <dl className="grid gap-3 text-sm sm:grid-cols-2">
        <div><dt className="text-tertiary">{t('executions.started')}</dt><dd className="text-primary">{time(item.startedAtUtc)}</dd></div>
        <div><dt className="text-tertiary">{t(current || !item.completedAtUtc && !item.outcome ? 'observers.elapsedAtReport' : 'shared.durationLabel')}</dt><dd className="text-primary">{duration(item, Date.parse(item.completedAtUtc ?? observedAtUtc))}</dd></div>
        {item.completedAtUtc && <div><dt className="text-tertiary">{t('observers.completed')}</dt><dd>{time(item.completedAtUtc)}</dd></div>}
      </dl>
      <p className="text-sm text-tertiary">{t('observers.lastReported', { observed: time(observedAtUtc), received: time(receivedAtUtc) })}</p>
      {item.validationResult && <p className="text-sm text-secondary">{t('observers.validation', { state: localizeText(item.validationResult) })}</p>}
    </li>;
  })}</ul>;
}

function CurrentWork({ observer, items, unavailable }: { observer: ObserverSummary; items: ObserverExecutionObservation[]; unavailable: boolean }) {
  const stale = unavailable || observer.freshness !== 'fresh' || observer.registrationState === 'revoked';
  const idle = observer.snapshotReceivedAtUtc && observer.activeExecutions === 0;
  return <TableCard.Root><section className="p-5" aria-labelledby="observer-current-work">
    <h2 id="observer-current-work" className="mb-3 text-lg font-semibold text-primary">{t('observers.current')}</h2>
    {stale && (observer.snapshotReceivedAtUtc || items.length > 0) && <Notice>{t('observers.workStale', { time: time(observer.snapshotReceivedAtUtc) })}</Notice>}
    {items.length ? <ExecutionList items={items} current stale={stale} /> : <Notice>{t(idle ? 'observers.idle' : 'observers.workUnknown')}</Notice>}
  </section></TableCard.Root>;
}

export function ObserverDetailPage({ id }: { id: string }) {
  useLanguage();
  const path = `/api/v1/observers/${encodeURIComponent(id)}`;
  const server = useApiRead('/api/status', serverStatus);
  const detail = useApiRead(path, observerDetail), history = useApiRead(path + '/history?limit=20', observerHistory);
  return <section className="mx-auto max-w-[1132px] space-y-5">
    <PageHeading title={detail.data?.observer.name ?? t('observers.title')} resourceId={id} breadcrumbs={[{ label: t('workers.workers'), href: '/workers' }, { label: t('observers.standalone') }]}
      actions={<Button color="secondary" isDisabled={detail.fetching || history.fetching} onPress={() => { void detail.refetch(); void history.refetch(); }}>{t('observers.refresh')}</Button>} />
    <Notice>{t('observers.scope')}</Notice>
    {detail.error && <Notice error>{t('observers.readError')}</Notice>}
    {!detail.data ? <ViewState title={detail.error ? t('observers.unavailable') : t('observers.loading')} /> : <>
      <CurrentWork observer={detail.data.observer} items={detail.data.currentExecutions} unavailable={!!detail.error || !!server.error} />
      <TableCard.Root><div className="space-y-4 p-5"><ObserverStatus observer={detail.data.observer} unavailable={!!detail.error || !!server.error} />
        <Revoke observerId={id} revoked={detail.data.observer.registrationState === 'revoked'} />
      </div></TableCard.Root>
    </>}
    <TableCard.Root><div className="p-5"><h2 className="mb-3 text-lg font-semibold text-primary">{t('observers.recent')}</h2>
      <p className="mb-3 text-sm text-tertiary">{t('observers.historyHelp')}</p>
      {history.error && <Notice error>{t('observers.readError')}</Notice>}
      {!history.data ? <Notice>{history.error ? t('observers.unavailable') : t('observers.loading')}</Notice> : <>{history.error && <Notice>{t('observers.retained')}</Notice>}<ExecutionList items={history.data} currentIds={detail.data?.currentExecutions.map(item => item.execution.localExecutionId)} /></>}
    </div></TableCard.Root>
  </section>;
}
