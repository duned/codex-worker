import type { NodeSummary, WorkerObservation } from '../../shared/api/contracts';
import { t, useLanguage } from '../../shared/i18n';
import { timestamp, workerConnectionStatus } from '../../model';

export function WorkerResources({ worker, node, now }: { worker: WorkerObservation; node?: NodeSummary; now: number }) {
  useLanguage();
  const resources = worker.hostResources;
  const unavailable = t('workers.unavailable');
  const bytes = (value?: number) => value == null ? unavailable : `${(value / 1024 ** 3).toFixed(1)} GiB`;
  const percent = (value?: number) => value == null ? unavailable : `${value.toFixed(1)}%`;
  const measured = resources?.measuredAtUtc;
  const measuredAt = measured ? Date.parse(measured) : NaN;
  const connection = workerConnectionStatus(worker, node);
  const metricsUnavailable = connection.state === 'Disconnected' || connection.state === 'Unknown';
  const stale = connection.state !== 'Connected' || (!!resources && (!Number.isFinite(measuredAt) || now - measuredAt > 120000));
  const sampleLabel = stale ? ` (${t('workers.lastSample')})` : '';
  const measuredAge = Number.isFinite(measuredAt) ? relativeAge(measuredAt, now) : unavailable;
  const facts = [
    [t('workers.platform'), worker.platform ?? unavailable],
    [t('workers.logicalCpus'), resources?.logicalCpuCount ?? unavailable],
    [t('workers.cpuUsage') + sampleLabel, metricsUnavailable ? unavailable : percent(resources?.cpuUsagePercent)],
    [t('workers.ramCapacity'), bytes(resources?.totalMemoryBytes)],
    [t('workers.memoryUsage') + sampleLabel, metricsUnavailable ? unavailable : `${percent(resources?.memoryUsagePercent)} · ${bytes(resources?.usedMemoryBytes)}`],
    [t('workers.diskCapacity'), bytes(resources?.diskTotalBytes)],
    [t('workers.diskFree') + sampleLabel, metricsUnavailable ? unavailable : bytes(resources?.diskAvailableBytes)],
  ];
  return <section className="rounded-xl border border-secondary bg-primary p-5 my-4">
    <h2 className="text-lg font-semibold text-primary">{t('workers.hostResources')}</h2>
    <p className="text-sm text-tertiary">{t('workers.lastMeasured')} {measured ? <time dateTime={measured}>{timestamp(measured)}</time> : unavailable}{measured && ` · ${measuredAge}`}{stale && ` · ${t('workers.staleOrUnavailable')}`}</p>
    <dl className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">{facts.map(([label, value]) => <div key={label}><dt className="text-sm text-tertiary">{label}</dt><dd className="text-sm font-medium text-primary">{value}</dd></div>)}</dl>
  </section>;
}

function relativeAge(measuredAt: number, now: number) {
  const seconds = Math.max(0, Math.floor((now - measuredAt) / 1000));
  if (seconds < 60) return t('workers.secondsAgo', { count: seconds });
  if (seconds < 3600) return t('workers.minutesAgo', { count: Math.floor(seconds / 60) });
  return t('workers.hoursAgo', { count: Math.floor(seconds / 3600) });
}
