import type { WorkerObservation } from '../../shared/api/contracts';
import { t, useLanguage } from '../../shared/i18n';
import { timestamp } from '../../model';

export function WorkerResources({ worker, now }: { worker: WorkerObservation; now: number }) {
  useLanguage();
  const resources = worker.hostResources;
  const unavailable = t('workers.unavailable');
  const bytes = (value?: number) => value == null ? unavailable : `${(value / 1024 ** 3).toFixed(1)} GiB`;
  const percent = (value?: number) => value == null ? unavailable : `${value.toFixed(1)}%`;
  const measured = resources?.measuredAtUtc;
  const stale = measured && (worker.availability.toLowerCase() === 'stale' || now - Date.parse(measured) > 120000);
  const facts = [
    [t('workers.platform'), worker.platform ?? unavailable],
    [t('workers.logicalCpus'), resources?.logicalCpuCount ?? unavailable],
    [t('workers.cpuUsage'), percent(resources?.cpuUsagePercent)],
    [t('workers.memoryUsage'), `${percent(resources?.memoryUsagePercent)} · ${bytes(resources?.usedMemoryBytes)} / ${bytes(resources?.totalMemoryBytes)}`],
    [t('workers.diskCapacity'), `${bytes(resources?.diskAvailableBytes)} / ${bytes(resources?.diskTotalBytes)}`],
  ];
  return <section className="rounded-xl border border-secondary bg-primary p-5 my-4">
    <h2 className="text-lg font-semibold text-primary">{t('workers.hostResources')}</h2>
    <p className="text-sm text-tertiary">{t('workers.measuredAt')} {measured ? <time dateTime={measured}>{timestamp(measured)}</time> : unavailable}{stale && ` · ${t('workers.staleOrUnavailable')}`}</p>
    <dl className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">{facts.map(([label, value]) => <div key={label}><dt className="text-sm text-tertiary">{label}</dt><dd className="text-sm font-medium text-primary">{value}</dd></div>)}</dl>
  </section>;
}
