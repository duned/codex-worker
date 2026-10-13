import { t, useLanguage, statusLabel, localizeText, type TranslationKey } from '../../shared/i18n';
import { GuidDisplay } from '../../shared/GuidDisplay';
import { Link } from 'react-router-dom';
import { Dialog, DialogTrigger, Heading, Popover } from 'react-aria-components';
import { InfoCircle } from '@untitledui/icons';
import { StatusBadge } from '../../shared/Presentation';
import type { ExecutionSummary, ProjectSummary, WorkerObservation } from '../../shared/api/contracts';
import { Button } from '../../untitled/components/base/buttons/button';
import { timestamp, statusColor } from '../../model';
import { presentation } from './model';
import { IssueTitle } from '../projects/IssueTitle';

type ExecutionTableItem = ExecutionSummary & { failureClassification?: string };

function StateCell({ item }: { item: ExecutionTableItem }) {
  useLanguage();
  const state = presentation(item);
  const notes = [
    item.currentStage ? { label: t('executions.reportedStage'), value: localizeText(item.currentStage) } : undefined,
    item.pendingReason ? { label: t('executions.pendingReason'), value: localizeText(item.pendingReason) } : undefined,
    item.completionSummary ? { label: t('executions.completionSummary'), value: localizeText(item.completionSummary) } : undefined,
    item.recoveryReason ? { label: t('executions.recoveryReason'), value: localizeText(item.recoveryReason) } : undefined,
    item.recoveryState ? { label: t('executions.recoveryStatus'), value: statusLabel(item.recoveryState) } : undefined,
    item.failureClassification ? { label: t('executions.failureClassification'), value: statusLabel(item.failureClassification) } : undefined
  ].filter((note): note is { label: string; value: string } => !!note);
  return <StatusBadge tone={state.tone} active={state.active}>
    {statusLabel(item.state)}
    {notes.length > 0 && <DialogTrigger>
      <Button color="tertiary" size="xs" className="!p-1 text-current" iconLeading={InfoCircle} aria-label={t('executions.viewStateDetailsFor', { id: item.id })} />
      <Popover placement="bottom end" className="z-50 max-w-sm rounded-xl border border-secondary bg-primary p-4 shadow-lg">
        <Dialog className="max-h-[min(70vh,28rem)] overflow-y-auto outline-none">
          <Heading slot="title" className="mb-3 text-sm font-semibold text-primary">{t('executions.stateDetails')}</Heading>
          <dl className="space-y-3">
            {notes.map(note => <div key={note.label}>
              <dt className="text-xs font-semibold text-tertiary">{note.label}</dt>
              <dd className="mt-1 whitespace-pre-wrap break-words text-sm text-secondary">{note.value}</dd>
            </div>)}
          </dl>
        </Dialog>
      </Popover>
    </DialogTrigger>}
  </StatusBadge>;
}

function ExecutionListTimestamp({ value, accessibleLabel }: { value: string; accessibleLabel: TranslationKey }) {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) return <time dateTime={value}>{timestamp(value)}</time>;

  const date = new Date(parsed), pad = (number: number) => String(number).padStart(2, '0');
  const dateText = `${pad(date.getDate())}/${pad(date.getMonth() + 1)}/${date.getFullYear()}`;
  const timeText = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return <time dateTime={value} aria-label={t(accessibleLabel, { date: dateText, time: timeText })}>
    <span className="block">{dateText}</span>
    <span className="block">{timeText}</span>
  </time>;
}

export function stateTone(state: string): 'gray' | 'success' | 'warning' | 'error' | 'info' {
  const tone = statusColor(state);
  return tone === 'success' || tone === 'warning' || tone === 'error' || tone === 'info' ? tone : 'gray';
}

export function ExecutionTable({ items, search = '', projects, workers }: {
  items: ExecutionTableItem[]; search?: string; projects: ProjectSummary[]; workers: WorkerObservation[];
}) {
  useLanguage();
  return <div className="overflow-x-auto focus-visible:outline-2 focus-visible:outline-offset-2" role="region" aria-label={t('executions.executionTable')} tabIndex={0}>
    <table className="w-full min-w-[1200px] table-fixed text-left">
      <colgroup>
        <col className="w-[9%]" /><col className="w-[17%]" /><col className="w-[24%]" />
        <col className="w-[14%]" /><col className="w-[10%]" /><col className="w-[14%]" /><col className="w-[12%]" />
      </colgroup>
      <thead className="bg-secondary text-xs uppercase tracking-wide text-tertiary">
        <tr>
          <th className="px-4 py-4 font-semibold">{t('executions.executionIdShort')}</th>
          <th className="px-4 py-4 font-semibold">{t('executions.project')}</th>
          <th className="px-4 py-4 font-semibold">{t('executions.githubIssueColumn')}</th>
          <th className="px-4 py-4 font-semibold">{t('executions.created')}</th>
          <th className="px-4 py-4 font-semibold">{t('executions.state')}</th>
          <th className="px-4 py-4 font-semibold">{t('executions.finished')}</th>
          <th className="px-4 py-4 font-semibold">{t('workers.worker')}</th>
        </tr>
      </thead>
      <tbody className="divide-y divide-secondary">
        {items.map(item => {
          const project = projects.find(value => value.id === item.projectId);
          const worker = workers.find(value => value.workerId === item.assignedWorkerId);
          return <tr key={item.id} className="align-middle">
            <td className="px-4 py-4"><Link className="font-medium text-primary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}${search}`}><GuidDisplay value={item.id} /></Link></td>
            <td className="px-4 py-4"><Link className="block break-words text-sm text-primary hover:text-brand-secondary" to={`/projects/${encodeURIComponent(item.projectId)}`}>{project?.name ?? item.projectId}</Link></td>
            <td className="px-4 py-4"><IssueTitle projectId={item.projectId} repository={project?.repository} workReference={item.workReference} className="break-words text-sm text-primary hover:text-brand-secondary" /></td>
            <td className="px-4 py-4 text-xs text-secondary"><ExecutionListTimestamp value={item.createdAtUtc} accessibleLabel="executions.createdAtAccessible" /></td>
            <td className="px-4 py-4"><StateCell item={item} /></td>
            <td className="px-4 py-4 text-xs text-secondary">{item.completedAtUtc && <ExecutionListTimestamp value={item.completedAtUtc} accessibleLabel="executions.finishedAtAccessible" />}</td>
            <td className="px-4 py-4 text-sm text-secondary">{item.assignedWorkerId ? <Link className="block truncate hover:text-brand-secondary" to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{worker?.displayName ?? <GuidDisplay value={item.assignedWorkerId} />}</Link> : t('executions.workerUnassigned')}</td>
          </tr>;
        })}
      </tbody>
    </table>
  </div>;
}
