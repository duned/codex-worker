import { t, useLanguage, statusLabel, localizeText } from './i18n';
import { useApiRead, useSession } from './api/session';
import { serverStatus } from './api/validation';
import { timestamp } from '../model';
import { StatusBadge } from './Presentation';
import type { ServerStatus as Status } from './api/contracts';

export function serverStatusPresentation(data: Status | undefined, error: string | undefined, live: string) {
  if (error) return { label: t("shared.serverStatusUnavailableRefreshFailed"), tone: 'warning' as const };
  if (!data) return { label: t("shared.serverStatusUnknown"), tone: 'gray' as const };
  if (data.state === 'offline') return { label: t("shared.serverOffline"), tone: 'error' as const };
  if (live !== 'Live · connected') return { label: t('shared.liveUnavailable', { state: statusLabel(data.state) }), tone: 'warning' as const };
  if (data.state === 'running') return { label: t("shared.serverLive"), tone: 'success' as const };
  return { label: t('shared.serverState', { state: statusLabel(data.state) }), tone: 'gray' as const };
}
export function ServerStatusView({ data, error, live, updatedAt }: { data?: Status; error?: string; live: string; updatedAt: number }) {
  useLanguage();
  const status = serverStatusPresentation(data, error, live);
  return <section aria-label={t("shared.serverConnection")} className="space-y-2 border-t border-secondary pt-3 text-xs text-tertiary">
    <div className="flex flex-wrap items-center gap-2"><StatusBadge tone={status.tone}>{status.label}</StatusBadge>{data?.version && <span>v{data.version.replace(/^v/, '')}</span>}</div>
    <p>{t("shared.lastUpdated")}{' '}{updatedAt > 0 ? <time dateTime={new Date(updatedAt).toISOString()}>{timestamp(new Date(updatedAt).toISOString())}</time> : localizeText('Unknown')}</p>
    {error && updatedAt > 0 && <p>{t("shared.lastSuccessfulObservationIsStale")}</p>}
  </section>;
}
export function ServerStatus() {
  useLanguage();
  const query = useApiRead('/api/status', serverStatus);
  const session = useSession();
  return <ServerStatusView data={query.data} error={query.error} live={session.live} updatedAt={query.updatedAt} />;
}
