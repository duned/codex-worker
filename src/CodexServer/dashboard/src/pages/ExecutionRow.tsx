import type { ExecutionSummary } from '../shared/api/contracts';
import { useNames } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { dateTime, executionStatus, issueNumber } from '../shared/model';
import { HeadRow, RowLink, StatusPill, Steps } from '../shared/ui';

const columns = 'md:grid-cols-[minmax(0,2.2fr)_minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_180px]';
export const executionLabel = (execution: ExecutionSummary) => {
  const number = issueNumber(execution), title = execution.workReference?.title;
  return number ? `#${number}${title ? ` · ${title}` : ''}` : execution.workReference?.id ?? execution.id;
};

export function ExecutionHead() {
  return <HeadRow className={columns}><span>{t('executions.issue')}</span><span>{t('executions.project')}</span><span>{t('executions.worker')}</span><span>{t('executions.started')}</span><span>{t('common.status')}</span></HeadRow>;
}

/** One execution in a list: Issue, project, Worker, start and status. */
export function ExecutionRow({ execution }: { execution: ExecutionSummary }) {
  const language = useLanguage(), names = useNames();
  return <RowLink to={`/executions/${encodeURIComponent(execution.id)}`} className={`grid-cols-1 ${columns}`}>
    <span className="min-w-0 font-medium break-words text-primary">{executionLabel(execution)}</span>
    <span className="text-sm text-tertiary">{names.project(execution.projectId)}</span>
    <span className="text-sm text-tertiary">{names.worker(execution.assignedWorkerId)}</span>
    <span className="text-sm text-tertiary tabular-nums">{dateTime(execution.startedAtUtc ?? execution.createdAtUtc, language)}</span>
    <span><StatusPill status={executionStatus(execution)} /></span>
  </RowLink>;
}

/** An active execution with its lifecycle steps. */
export function ActiveExecution({ execution, showWorker = true }: { execution: ExecutionSummary; showWorker?: boolean }) {
  const names = useNames();
  return <RowLink to={`/executions/${encodeURIComponent(execution.id)}`} className="grid-cols-1 gap-y-3">
    <span className="flex flex-wrap items-center justify-between gap-2">
      <span className="min-w-0 font-medium break-words text-primary">{executionLabel(execution)}</span>
      <span className="flex items-center gap-3 text-sm text-tertiary">
        {names.project(execution.projectId)}{showWorker && <> · {names.worker(execution.assignedWorkerId)}</>}
        <StatusPill status={executionStatus(execution)} />
      </span>
    </span>
    <Steps execution={execution} />
  </RowLink>;
}
