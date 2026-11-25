import { useApiRead } from '../shared/api/session';
import { statistics as validateStatistics } from '../shared/api/validation';
import { t, useLanguage } from '../shared/i18n';
import { duration, failureCauseText, firstAttemptRate, projectWeeks, usd, weekLabel } from '../shared/model';
import { Card, Empty, HeadRow, ReadState, Row, Rows } from '../shared/ui';

const columns = 'md:grid-cols-[90px_90px_110px_110px_minmax(0,1fr)_80px]';

/** Weekly history the Server computes from every finished execution of this project, newest week first. */
export function ProjectHistory({ projectId }: { projectId: string }) {
  const language = useLanguage();
  const report = useApiRead(`/api/v1/statistics?weeks=8&projectId=${encodeURIComponent(projectId)}`, validateStatistics);
  const weeks = projectWeeks(report.data, projectId);
  return <Card title={t('stats.title')} flush>
    <p className="px-5 py-4 text-sm text-tertiary">{t('stats.description')}</p>
    <ReadState read={report}><Rows>
      <HeadRow className={columns}><span>{t('stats.week')}</span><span>{t('stats.integrated')}</span><span>{t('stats.firstAttempt')}</span>
        <span>{t('stats.queue')}</span><span>{t('stats.causes')}</span><span>{t('stats.cost')}</span></HeadRow>
      {weeks.some(week => week.finished || week.cancelled) ? weeks.map(week => {
        const rate = firstAttemptRate(week);
        return <Row key={week.weekStartUtc} className={`grid-cols-2 ${columns}`}>
          <span className="text-sm font-medium text-primary">{weekLabel(week.weekStartUtc, language)}</span>
          <span className="text-sm tabular-nums text-secondary">{t('stats.integratedOf', { n: week.integrated, total: week.finished })}</span>
          <span className="text-sm tabular-nums text-secondary" title={t('stats.firstAttemptHint', { n: week.firstAttemptsIntegrated, total: week.firstAttempts })}>
            {rate === undefined ? '—' : `${rate} %`}</span>
          <span className="text-sm tabular-nums text-secondary" title={week.queueP90Seconds == null ? undefined : t('stats.queueP90', { time: duration(week.queueP90Seconds * 1000) })}>
            {week.queueMedianSeconds == null ? '—' : duration(week.queueMedianSeconds * 1000)}</span>
          <span className="min-w-0 truncate text-sm text-secondary">{week.failureCauses.length
            ? week.failureCauses.map(cause => `${failureCauseText(cause.cause)} (${cause.count})`).join(' · ') : '—'}</span>
          <span className="text-sm tabular-nums text-secondary">{week.costUsd == null ? '—' : usd(week.costUsd)}</span>
        </Row>;
      }) : <Empty>{t('stats.none')}</Empty>}
    </Rows></ReadState>
  </Card>;
}
