import { Link } from 'react-router-dom';
import { t, localizeText } from '../../shared/i18n';
import { StatusBadge } from '../../shared/Presentation';
import { timestamp } from '../../model';
import type { ObserverSummary, ObserverInventoryExecution } from '../../shared/api/contracts';
import { observerConnection, observerName } from './model';

export function ObserverSignals({ observer, unavailable = false }: { observer: ObserverSummary; unavailable?: boolean }) {
  const connection = observerConnection(observer);
  return <div className="space-y-2 text-xs text-tertiary">
    <div className="flex flex-wrap gap-2"><StatusBadge compact>{t('observers.standalone')}</StatusBadge><StatusBadge compact>{t('observers.readOnly')}</StatusBadge>
      <StatusBadge compact tone={!unavailable && connection === 'connected' ? 'success' : 'gray'}>{unavailable ? t('observers.unavailable') : t(`observers.state.${connection}`)}</StatusBadge>
      {(observer.activeExecutions ?? 0) > 0 && <span>{t('observers.workReported')}</span>}</div>
    <p>{t('observers.snapshot')}: {timestamp(observer.snapshotReceivedAtUtc ?? undefined)}</p>
    {unavailable && <p>{t('observers.retained')}</p>}
    <p>{t('observers.lifecycle')}: {localizeText(observer.reportedLifecycle ?? t('shared.unknown'))} · {t('observers.readiness')}: {localizeText(observer.reportedReadiness ?? t('shared.unknown'))}</p>
    <p>{t('observers.capacity')}: {observer.activeExecutions ?? t('shared.unknown')} / {observer.maximumCapacity ?? t('shared.unknown')}</p>
  </div>;
}
export function ObserverCard({ observer, unavailable }: { observer: ObserverSummary; unavailable?: boolean }) {
  return <Link to={`/workers?observer=${encodeURIComponent(observer.observerId)}`} className="block min-w-0 space-y-3 rounded-xl border border-secondary bg-primary p-3 hover:bg-secondary">
    <span className="block truncate font-medium text-primary">{observerName(observer)} <span className="text-xs font-normal text-tertiary">{observer.version ?? t('shared.unknown')}</span></span>
    <ObserverSignals observer={observer} unavailable={unavailable} />
  </Link>;
}
export function ObserverExecutionRow({ item, observer, unavailable }: { item: ObserverInventoryExecution; observer: ObserverSummary; unavailable?: boolean }) {
  const execution = item.observation.execution;
  return <li className="grid gap-2 border-b border-secondary px-3 py-3 last:border-0 sm:grid-cols-[minmax(9rem,1.2fr)_minmax(7rem,1fr)_minmax(7rem,1fr)_auto] sm:items-center">
    <div className="min-w-0 text-sm text-primary">#{execution.issueNumber} · {execution.issueTitle}<p className="text-xs text-tertiary">{execution.project} · {execution.repository}</p></div>
    <Link className="text-brand-secondary hover:underline" to={`/workers?observer=${encodeURIComponent(observer.observerId)}`}>{observerName(observer)}</Link>
    <div className="space-y-2"><StatusBadge compact>{t('observers.standalone')}</StatusBadge> <StatusBadge compact>{t('observers.readOnly')}</StatusBadge><p className="text-xs text-tertiary">{unavailable ? t('observers.retained') : t(`observers.state.${observerConnection(observer)}`)} · {timestamp(observer.snapshotReceivedAtUtc ?? undefined)}</p></div>
    <div className="text-sm"><StatusBadge compact>{localizeText(execution.outcome ?? execution.stage)}</StatusBadge><p className="text-xs text-tertiary">{timestamp(execution.completedAtUtc ?? execution.startedAtUtc)}</p></div>
  </li>;
}
