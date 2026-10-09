import { t, useLanguage, statusLabel, resources, type TranslationKey } from './i18n';
import { useApiRead } from './api/session';
import { serverStatus } from './api/validation';
import { timestamp } from '../model';
import { serverStatusPresentation } from './server-status-model.mjs';
import type { ServerStatus as Status } from './api/contracts';

function localizedServerState(state: string) {
  const key = `status.${state.toLowerCase()}` as TranslationKey;
  return Object.hasOwn(resources.en, key) ? t(key) : statusLabel(state);
}

function statusLabelText(status: ReturnType<typeof serverStatusPresentation>) {
  switch (status.kind) {
    case 'ready': return t('shared.serverReady');
    case 'unavailable': return t('shared.serverStatusUnavailable');
    case 'loading': return t('shared.serverStatusLoading');
    case 'unknown': return t('shared.serverStatusUnknown');
    case 'offline': return t('shared.serverOffline');
    case 'not-ready': return t('shared.serverState', { state: localizedServerState(status.state) });
  }
}

function toneDot(tone: ReturnType<typeof serverStatusPresentation>['tone']) {
  return tone === 'success' ? 'bg-success-solid' : tone === 'warning' ? 'bg-warning-solid' : tone === 'error' ? 'bg-error-solid' : 'bg-secondary-solid';
}

export function ServerStatusView({ data, hasError, loading, updatedAt }: { data?: Status; hasError: boolean; loading: boolean; updatedAt: number }) {
  useLanguage();
  const status = serverStatusPresentation(data, hasError, loading, updatedAt);
  const version = typeof data?.version === 'string' ? data.version.trim().replace(/^v/i, '') : '';
  const timestampIso = Number.isFinite(updatedAt) && updatedAt > 0 ? new Date(updatedAt).toISOString() : undefined;
  return <section aria-label={t('shared.serverConnection')} className="grid min-h-[58px] min-w-0 grid-cols-[minmax(0,1fr)_auto] items-center gap-x-2 gap-y-1 rounded-[10px] border border-secondary bg-secondary px-3 py-2.5">
    <div className="flex min-w-0 items-center gap-2">
      <span aria-hidden="true" className={`size-2.5 shrink-0 rounded-full ring-4 ${toneDot(status.tone)} ${status.tone === 'success' ? 'ring-bg-success-primary' : status.tone === 'warning' ? 'ring-bg-warning-primary' : status.tone === 'error' ? 'ring-bg-error-primary' : 'ring-secondary'}`} />
      <span role="status" className="truncate text-sm font-semibold text-primary">{statusLabelText(status)}</span>
    </div>
    {version && <span className="row-span-2 rounded-md border border-secondary bg-primary px-2 py-1 text-[10px] font-semibold leading-none text-secondary">v{version}</span>}
    <p className="min-w-0 truncate text-[10px] leading-4 text-tertiary">{t('shared.updated')}{' '}{timestampIso ? <time dateTime={timestampIso}>{timestamp(timestampIso)}</time> : t('shared.unavailable')}{status.freshness === 'stale' && <span className="ml-1 text-warning-primary">· {t('status.stale')}</span>}</p>
  </section>;
}
export function ServerStatus() {
  useLanguage();
  const query = useApiRead('/api/status', serverStatus);
  return <ServerStatusView data={query.hasError ? query.retainedData : query.data} hasError={query.hasError} loading={query.loading} updatedAt={query.updatedAt} />;
}
