import { useState, type ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction, useArchive } from '../shared/actions';
import { useApiRead, useRuntime } from '../shared/api/session';
import type { Validator } from '../shared/api/client';
import type { ExecutionMetrics } from '../shared/api/contracts';
import { execution as validateExecution, record } from '../shared/api/validation';
import { projectExecutionsPath, recentExecutionsPath, retryIssuePath, useExecutions, useNames, useProjects } from '../shared/data';
import { t, tryT, useLanguage } from '../shared/i18n';
import { canArchive, compareUrl, dateTime, duration, executionActions, executionStatus, explain, isActive, issueNumber, newerAttempt, stepProgress, type ExecutionAction, usageText } from '../shared/model';
import { Ago, Card, Check, Explanation, Field, inputClass, KeyValues, Notice, PageHeader, Pill, ReadState, StatusPill, Steps, TextLink, CopyValue } from '../shared/ui';
import { executionLabel } from './ExecutionRow';

interface ConflictPreview { executionId: string; workerId?: string; candidate?: Record<string, unknown>; eligible: boolean; reason: string }
const conflictPreview: Validator<ConflictPreview> = value => {
  const item = record(value);
  if (typeof item.executionId !== 'string' || typeof item.eligible !== 'boolean' || typeof item.reason !== 'string') throw new Error('Invalid preview.');
  if (item.eligible && (typeof item.workerId !== 'string' || !item.candidate)) throw new Error('Invalid preview.');
  return item as unknown as ConflictPreview;
};

/** The Server posts guidance as one Issue comment; the Worker reads up to 4096 characters of each. */
const guidanceLimit = 4000;

export function ExecutionPage() {
  const language = useLanguage(), { executionId = '' } = useParams(), runtime = useRuntime(), action = useAction(), archive = useArchive(), names = useNames();
  const path = `/api/v1/executions/${encodeURIComponent(executionId)}`;
  const read = useApiRead(path, validateExecution), projects = useProjects();
  const [conflictReason, setConflictReason] = useState<string>(), [panel, setPanel] = useState<'manualCompletion' | 'resolveUncertain' | 'retryGuidance'>();
  const [disposition, setDisposition] = useState<'NotIntegrated' | 'Integrated'>('NotIntegrated'), [evidence, setEvidence] = useState(''), [commit, setCommit] = useState('');
  const [guidance, setGuidance] = useState('');
  const item = read.data;
  const attempts = useExecutions(item ? projectExecutionsPath(item.projectId, issueNumber(item) ?? undefined) : recentExecutionsPath);
  if (!item) return <><PageHeader title={t('executions.detail')} crumbs={[{ label: t('nav.executions'), to: '/executions' }]} /><Card flush><ReadState read={read}>{null}</ReadState></Card></>;
  const project = projects.data?.find(entry => entry.id === item.projectId), number = issueNumber(item), status = executionStatus(item), reason = explain(item);
  const issuePath = number ? `/projects/${encodeURIComponent(item.projectId)}/issues/${number}` : undefined;
  const enqueue = () => number && action.run(`enqueue-${item.projectId}-${number}`, retryIssuePath(item.projectId, number), 'POST', {}, 'actions.retryQueued');
  const newer = newerAttempt(item, attempts.data ?? []);
  const available = executionActions(item, !!newer);
  const operationId = () => crypto.randomUUID().replaceAll('-', '');
  // One click: ask the Server whether only the integration can be retried and, if so, request it.
  async function retryIntegration() {
    setConflictReason(undefined);
    let preview: ConflictPreview;
    try { preview = await runtime.read(`${path}/conflict-recovery`, new AbortController().signal, conflictPreview); }
    catch (error) { setConflictReason(error instanceof Error ? error.message : t('common.loadFailed')); return; }
    if (!preview.eligible) { setConflictReason(preview.reason); return; }
    await action.run(`${path}/conflict-recovery`, `${path}/conflict-recovery`, 'POST',
      { operationId: operationId(), workerId: preview.workerId, candidate: preview.candidate }, 'actions.integrationRetryQueued');
  }
  function closePanel() { setPanel(undefined); setEvidence(''); setCommit(''); setGuidance(''); setDisposition('NotIntegrated'); }
  // The guidance becomes an Issue comment, which the next attempt reads with the Issue.
  async function retryWithGuidance() {
    if (number && await action.run(`enqueue-${item!.projectId}-${number}`, retryIssuePath(item!.projectId, number), 'POST',
      { guidance: guidance.trim() }, 'actions.retryGuidanceQueued')) closePanel();
  }
  const guidanceValid = guidance.trim().length > 0 && guidance.trim().length <= guidanceLimit && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(guidance);
  async function resolveUncertain() {
    const body = disposition === 'Integrated' ? { disposition, evidence: evidence.trim(), integrationCommit: commit.trim() } : { disposition, evidence: evidence.trim() };
    if (await action.run(`${path}/reconcile`, `${path}/reconcile`, 'POST', body,
      disposition === 'Integrated' ? 'actions.uncertainIntegrated' : 'actions.uncertainRequeued')) closePanel();
  }
  const evidenceValid = evidence.trim().length > 0 && evidence.trim().length <= 1000 && !/[\u0000-\u001f]/.test(evidence)
    && (disposition === 'NotIntegrated' || /^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$/.test(commit.trim()));
  const actionButton = (kind: ExecutionAction, primary: boolean) => {
    const color = primary ? 'primary' : 'secondary', busy = !!action.busy;
    switch (kind) {
      case 'prioritize': return <Button key={kind} color="secondary" isDisabled={busy}
        onClick={() => void action.run(`prioritize-${item.id}`, `${path}/prioritize`, 'POST', {}, 'actions.prioritized')}>{t('actions.prioritize')}</Button>;
      case 'cancel': return <Button key={kind} color="secondary-destructive" isDisabled={busy}
        onClick={() => void action.run(`cancel-${item.id}`, `${path}/cancel`, 'POST', {}, 'actions.cancelled')}>{t('actions.cancelQueued')}</Button>;
      case 'retryIntegration': return <Button key={kind} color={color} isDisabled={busy} onClick={() => void retryIntegration()}>{t('actions.retryIntegration')}</Button>;
      case 'retryFresh': return number ? <span key={kind} className="contents">
        <Button color={color} isDisabled={busy} onClick={() => void enqueue()}>{t('actions.retryFresh')}</Button>
        <Button color="secondary" isDisabled={busy || !!panel} onClick={() => setPanel('retryGuidance')}>{t('actions.retryWithGuidance')}</Button>
      </span> : null;
      case 'resolveUncertain': return <Button key={kind} color={color} isDisabled={busy || !!panel} onClick={() => setPanel('resolveUncertain')}>{t('actions.resolveUncertain')}</Button>;
      case 'manualCompletion': return <Button key={kind} color={color} isDisabled={busy || !!panel} onClick={() => setPanel('manualCompletion')}>{t('actions.reconcileManually')}</Button>;
    }
  };
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
        {available.map((kind, index) => actionButton(kind, index === 0))}
        {(canArchive(item) || item.archivedAtUtc) && <Button color="secondary" isDisabled={archive.busy}
          onClick={() => void archive.run([item.id], !item.archivedAtUtc)}>{t(item.archivedAtUtc ? 'actions.restore' : 'actions.archive')}</Button>}
        {item.workReference?.url && <Button color="secondary" href={item.workReference.url} target="_blank" rel="noreferrer">{t('actions.openGitHub')}</Button>}
      </>} />

    <Card><Steps execution={item} /></Card>

    {newer && item.state === 'Failed' && <Notice>{t('executions.newerAttempt')} <TextLink to={`/executions/${encodeURIComponent(newer.id)}`}>{t('executions.openNewerAttempt', { n: newer.attemptNumber ?? '' })}</TextLink></Notice>}
    {conflictReason && <Notice tone="warn">{t('actions.integrationRetryUnavailable')} {conflictReason}</Notice>}

    {panel === 'manualCompletion' && <Notice>
      <div className="flex flex-col gap-3">
        <span>{t('actions.reconcileHint')}</span>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" color="primary" isDisabled={!!action.busy} onClick={() => void action.run(`${path}/manual-completion`, `${path}/manual-completion`, 'POST',
            { operationId: operationId() }, 'actions.manualCompletionRequested').then(closePanel)}>{t('actions.confirmReconcile')}</Button>
          <Button size="sm" color="secondary" isDisabled={!!action.busy} onClick={closePanel}>{t('actions.cancel')}</Button>
        </div>
      </div>
    </Notice>}

    {panel === 'retryGuidance' && <Card title={t('actions.retryWithGuidance')}>
      <div className="flex flex-col gap-4">
        <p className="text-sm text-secondary">{t('actions.retryGuidanceHint')}</p>
        <Field label={t('actions.retryGuidanceLabel')} hint={t('actions.retryGuidanceLimit', { n: guidanceLimit - guidance.trim().length })}>
          <textarea className={inputClass} rows={5} maxLength={guidanceLimit + 200} value={guidance} autoFocus onChange={event => setGuidance(event.target.value)} />
        </Field>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" color="primary" isDisabled={!!action.busy || !guidanceValid} onClick={() => void retryWithGuidance()}>{t('actions.retryGuidanceConfirm')}</Button>
          <Button size="sm" color="secondary" isDisabled={!!action.busy} onClick={closePanel}>{t('actions.cancel')}</Button>
        </div>
      </div>
    </Card>}

    {panel === 'resolveUncertain' && <Card title={t('actions.resolveUncertain')}>
      <div className="flex flex-col gap-4">
        <p className="text-sm text-secondary">{t('actions.resolveUncertainHint')}</p>
        <div className="flex flex-col gap-2">
          <Check checked={disposition === 'NotIntegrated'} onChange={value => value && setDisposition('NotIntegrated')}>{t('actions.uncertainNotIntegrated')}</Check>
          <Check checked={disposition === 'Integrated'} onChange={value => value && setDisposition('Integrated')}>{t('actions.uncertainIsIntegrated')}</Check>
        </div>
        <Field label={t('actions.uncertainEvidence')} hint={t('actions.uncertainEvidenceHint')}>
          <textarea className={inputClass} rows={2} maxLength={1000} value={evidence} onChange={event => setEvidence(event.target.value)} />
        </Field>
        {disposition === 'Integrated' && <Field label={t('actions.uncertainCommit')} hint={t('actions.uncertainCommitHint')}>
          <input className={`${inputClass} font-mono`} value={commit} onChange={event => setCommit(event.target.value)} />
        </Field>}
        <div className="flex flex-wrap gap-2">
          <Button size="sm" color="primary" isDisabled={!!action.busy || !evidenceValid} onClick={() => void resolveUncertain()}>
            {t(disposition === 'Integrated' ? 'actions.uncertainConfirmIntegrated' : 'actions.uncertainConfirmRequeue')}</Button>
          <Button size="sm" color="secondary" isDisabled={!!action.busy} onClick={closePanel}>{t('actions.cancel')}</Button>
        </div>
      </div>
    </Card>}

    {reason && <Card>
      <Explanation what={t(reason.what)} todo={t(reason.todo)} />
      {item.completionSummary && <details className="mt-4 border-t border-secondary pt-3"><summary className="cursor-pointer text-sm font-medium text-secondary">{t('executions.technicalDetail')}</summary>
        <pre className="mt-2 overflow-x-auto rounded-lg bg-secondary p-3 font-mono text-xs whitespace-pre-wrap text-secondary">{item.completionSummary}</pre></details>}
    </Card>}
    {item.state === 'Queued' && item.pendingReason && <Notice>
      {tryT(`pending.${item.pendingReason}`, item.pendingReason)}
      {item.managedEligibilityState === 'blocked' && (item.managedEligibilityReasons?.length ?? 0) > 0 &&
        <ul className="mt-2 list-disc pl-5">{item.managedEligibilityReasons!.map(text => <li key={text}>{text}</li>)}</ul>}
      {item.managedEligibilityState && item.managedEligibilityState !== 'eligible' && number && <div className="mt-3">
        <Button size="sm" color="secondary" isDisabled={!!action.busy} onClick={() => void action.run(`eligibility-${item.id}`,
          `/api/v1/projects/${encodeURIComponent(item.projectId)}/github/issues/${number}/eligibility/refresh`, 'POST', {}, 'actions.eligibilityRechecked')}>
          {t('actions.recheckEligibility')}</Button></div>}
    </Notice>}

    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)] lg:items-start">
      <div className="flex flex-col gap-6">
        <Card title={t('executions.result')}><KeyValues items={[
          [t('executions.implementation'), stepProgress(item)[1].state === 'done' ? t('executions.done') : isActive(item) ? t('executions.inProgress') : t('executions.pending')],
          [t('executions.validation'), item.validationResult ? <span className="font-mono text-xs">{item.validationResult}</span> : t('executions.pending')],
          [t('executions.integration'), integrationText],
          [t('executions.duration'), duration(item.durationMilliseconds ?? (item.startedAtUtc && item.completedAtUtc ? Date.parse(item.completedAtUtc) - Date.parse(item.startedAtUtc) : undefined))],
          ...(item.metrics?.usage ? [[t('usage.title'), usageText(item.metrics.usage, t)] as [string, string]] : []),
          ...(item.state === 'Completed' && item.completionSummary ? [[t('executions.summary'), item.completionSummary] as [string, string]] : [])
        ]} /></Card>
        {item.metrics?.changes && <ChangesCard changes={item.metrics.changes} compare={compareUrl(item)} />}
      </div>
      <Card title={t('common.details')}><KeyValues items={[
        [t('executions.project'), <TextLink to={`/projects/${encodeURIComponent(item.projectId)}`}>{names.project(item.projectId)}</TextLink>],
        [t('executions.worker'), item.assignedWorkerId ? <TextLink to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{names.worker(item.assignedWorkerId)}</TextLink> : names.worker(item.assignedWorkerId)],
        [t('executions.engine'), item.workReference?.selection?.engine === 'ccc' ? 'Claude Code' : 'Codex'],
        [t('executions.created'), dateTime(item.createdAtUtc, language)],
        [t('executions.finished'), dateTime(item.completedAtUtc, language)],
        [t('executions.id'), <CopyValue value={item.id} />],
        ...(item.archivedAtUtc ? [[t('executions.archivedAt'), dateTime(item.archivedAtUtc, language)] as [string, ReactNode]] : []),
        ...(item.retryOfExecutionId ? [[t('executions.retryOf'), <TextLink mono to={`/executions/${encodeURIComponent(item.retryOfExecutionId)}`}>{item.retryOfExecutionId.slice(0, 8)}</TextLink>] as [string, ReactNode]] : [])
      ]} /></Card>
    </div>
  </>;
}

/** Size of the integrated change and the files it touched, with the comparison on GitHub. */
function ChangesCard({ changes, compare }: { changes: NonNullable<ExecutionMetrics['changes']>; compare?: string }) {
  const hidden = changes.filesChanged - changes.files.length;
  return <Card title={t('changes.title')} actions={compare && <Button size="sm" color="secondary" href={compare} target="_blank" rel="noreferrer">{t('changes.compare')}</Button>}>
    <p className="text-sm text-secondary"><span className="font-medium text-primary">{t('changes.files', { n: changes.filesChanged })}</span>
      {' · '}<span className="text-utility-green-500 tabular-nums">+{changes.insertions}</span>{' '}<span className="text-utility-red-500 tabular-nums">−{changes.deletions}</span></p>
    {changes.files.length > 0 && <ul className="mt-3 flex flex-col gap-1 font-mono text-xs text-secondary">
      {changes.files.map(file => <li key={file} className="truncate" title={file}>{file}</li>)}
      {hidden > 0 && <li className="font-sans text-tertiary">{t('changes.more', { n: hidden })}</li>}
    </ul>}
  </Card>;
}
