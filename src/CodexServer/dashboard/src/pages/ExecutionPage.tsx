import { useState, type ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction, useArchive } from '../shared/actions';
import { useApiRead, useRuntime } from '../shared/api/session';
import type { Validator } from '../shared/api/client';
import { execution as validateExecution, record } from '../shared/api/validation';
import { useNames, useProjects } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { canArchive, dateTime, duration, executionStatus, explain, isActive, issueNumber, stepProgress } from '../shared/model';
import { Ago, Card, Explanation, KeyValues, Notice, PageHeader, Pill, ReadState, StatusPill, Steps } from '../shared/ui';
import { executionLabel } from './ExecutionRow';

interface ConflictPreview { executionId: string; workerId?: string; candidate?: Record<string, unknown>; eligible: boolean; reason: string }
const conflictPreview: Validator<ConflictPreview> = value => {
  const item = record(value);
  if (typeof item.executionId !== 'string' || typeof item.eligible !== 'boolean' || typeof item.reason !== 'string') throw new Error('Invalid preview.');
  if (item.eligible && (typeof item.workerId !== 'string' || !item.candidate)) throw new Error('Invalid preview.');
  return item as unknown as ConflictPreview;
};

export function ExecutionPage() {
  const language = useLanguage(), { executionId = '' } = useParams(), runtime = useRuntime(), action = useAction(), archive = useArchive(), names = useNames();
  const path = `/api/v1/executions/${encodeURIComponent(executionId)}`;
  const read = useApiRead(path, validateExecution), projects = useProjects();
  const [preview, setPreview] = useState<ConflictPreview>(), [checking, setChecking] = useState(false);
  const item = read.data;
  if (!item) return <><PageHeader title={t('executions.detail')} crumbs={[{ label: t('nav.executions'), to: '/executions' }]} /><Card flush><ReadState read={read}>{null}</ReadState></Card></>;
  const project = projects.data?.find(entry => entry.id === item.projectId), number = issueNumber(item), status = executionStatus(item), reason = explain(item);
  const issuePath = number ? `/projects/${encodeURIComponent(item.projectId)}/issues/${number}` : undefined;
  const enqueue = () => number && action.run(`enqueue-${item.projectId}-${number}`, `/api/v1/projects/${encodeURIComponent(item.projectId)}/github/issues/${number}/enqueue`, 'POST', {}, 'actions.queued');
  async function inspectConflict() {
    setChecking(true);
    try { setPreview(await runtime.read(`${path}/conflict-recovery`, new AbortController().signal, conflictPreview)); }
    catch (error) { setPreview({ executionId, eligible: false, reason: error instanceof Error ? error.message : t('common.loadFailed') }); }
    finally { setChecking(false); }
  }
  const integrationText = item.state === 'Completed' ? t('executions.integratedInto', { branch: project?.defaultBranch ?? 'main' })
    : item.integrationResult === 'rebase-conflict' || item.failureClassification === 'IntegrationConflict' ? t('executions.notIntegratedConflict')
    : item.currentStage === 'Integration' && isActive(item) ? t('executions.integrating') : t('executions.pending');

  return <>
    <PageHeader crumbs={[{ label: names.project(item.projectId), to: `/projects/${encodeURIComponent(item.projectId)}` },
      ...(issuePath ? [{ label: `#${number}`, to: issuePath }] : []), { label: t('executions.attempt', { n: item.attemptNumber ?? 1 }) }]}
      title={executionLabel(item)}
      description={<span className="flex flex-wrap items-center gap-2"><StatusPill status={status} />
        {item.archivedAtUtc && <Pill tone="idle">{t('executions.archived')}</Pill>}
        {t('executions.attemptOn', { n: item.attemptNumber ?? 1, worker: names.worker(item.assignedWorkerId) })} · {t('executions.startedAgo')} <Ago value={item.startedAtUtc ?? item.createdAtUtc} /></span>}
      actions={<>
        {item.state === 'Queued' && <Button color="secondary-destructive" isDisabled={!!action.busy}
          onClick={() => void action.run(`cancel-${item.id}`, `${path}/cancel`, 'POST', {}, 'actions.cancelled')}>{t('actions.cancelQueued')}</Button>}
        {item.state === 'Failed' && number && <Button color="primary" isDisabled={!!action.busy} onClick={() => void enqueue()}>{t('actions.retryFresh')}</Button>}
        {(canArchive(item) || item.archivedAtUtc) && <Button color="secondary" isDisabled={archive.busy}
          onClick={() => void archive.run([item.id], !item.archivedAtUtc)}>{t(item.archivedAtUtc ? 'actions.restore' : 'actions.archive')}</Button>}
        {item.workReference?.url && <Button color="secondary" href={item.workReference.url} target="_blank" rel="noreferrer">{t('actions.openGitHub')}</Button>}
      </>} />

    <Card><Steps execution={item} /></Card>

    {reason && <Card>
      <Explanation what={t(reason.what)} todo={t(reason.todo)}>
        {item.failureClassification === 'IntegrationConflict' && item.recoverable && <div className="flex flex-wrap items-center gap-2">
          {!preview?.eligible && <Button size="sm" color="secondary" isDisabled={checking} onClick={() => void inspectConflict()}>{t('actions.checkIntegrationRetry')}</Button>}
          {preview?.eligible && <Button size="sm" color="secondary" isDisabled={!!action.busy} onClick={() => void action.run(`${path}/conflict-recovery`, `${path}/conflict-recovery`, 'POST',
            { operationId: crypto.randomUUID().replaceAll('-', ''), workerId: preview.workerId, candidate: preview.candidate }, 'actions.integrationRetryQueued').then(done => { if (done) setPreview(undefined); })}>
            {t('actions.retryIntegration')}</Button>}
          {preview && !preview.eligible && <span className="text-sm text-tertiary">{preview.reason}</span>}
        </div>}
      </Explanation>
      {item.completionSummary && <details className="mt-4 border-t border-secondary pt-3"><summary className="cursor-pointer text-sm font-medium text-secondary">{t('executions.technicalDetail')}</summary>
        <pre className="mt-2 overflow-x-auto rounded-lg bg-secondary p-3 font-mono text-xs whitespace-pre-wrap text-secondary">{item.completionSummary}</pre></details>}
    </Card>}
    {item.state === 'Queued' && item.pendingReason && <Notice>{item.pendingReason}</Notice>}

    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
      <Card title={t('executions.result')}><KeyValues items={[
        [t('executions.implementation'), stepProgress(item)[1].state === 'done' ? t('executions.done') : isActive(item) ? t('executions.inProgress') : t('executions.pending')],
        [t('executions.validation'), item.validationResult ? <span className="font-mono text-xs">{item.validationResult}</span> : t('executions.pending')],
        [t('executions.integration'), integrationText],
        [t('executions.duration'), duration(item.durationMilliseconds ?? (item.startedAtUtc && item.completedAtUtc ? Date.parse(item.completedAtUtc) - Date.parse(item.startedAtUtc) : undefined))],
        ...(item.state === 'Completed' && item.completionSummary ? [[t('executions.summary'), item.completionSummary] as [string, string]] : [])
      ]} /></Card>
      <Card title={t('common.details')}><KeyValues items={[
        [t('executions.project'), names.project(item.projectId)],
        [t('executions.worker'), names.worker(item.assignedWorkerId)],
        [t('executions.engine'), item.workReference?.selection?.engine === 'ccc' ? 'Claude Code' : 'Codex'],
        [t('executions.created'), dateTime(item.createdAtUtc, language)],
        [t('executions.finished'), dateTime(item.completedAtUtc, language)],
        [t('executions.id'), <span className="font-mono text-xs">{item.id}</span>],
        ...(item.archivedAtUtc ? [[t('executions.archivedAt'), dateTime(item.archivedAtUtc, language)] as [string, ReactNode]] : []),
        ...(item.retryOfExecutionId ? [[t('executions.retryOf'), <a className="font-mono text-xs underline" href={`/executions/${item.retryOfExecutionId}`}>{item.retryOfExecutionId.slice(0, 8)}</a>] as [string, ReactNode]] : [])
      ]} /></Card>
    </div>
  </>;
}
