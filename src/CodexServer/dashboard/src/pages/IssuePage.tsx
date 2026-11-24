import { useParams } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead } from '../shared/api/session';
import { issue as validateIssue, project as validateProject } from '../shared/api/validation';
import { projectExecutionsPath, retryIssuePath, useExecutions, useNames } from '../shared/data';
import { Markdown } from '../shared/Markdown';
import { t, useLanguage } from '../shared/i18n';
import { duration, executionActions, executionStatus, explain, isActive, issueStatus, issueUsage, tokens, totalDuration, usd } from '../shared/model';
import { Ago, Card, Empty, Explanation, KeyValues, PageHeader, ReadState, RowLink, Rows, StatusPill, Steps, TextLink } from '../shared/ui';

export function IssuePage() {
  useLanguage();
  const { projectId = '', issueNumber = '' } = useParams(), number = Number(issueNumber);
  const base = `/api/v1/projects/${encodeURIComponent(projectId)}`;
  const project = useApiRead(base, validateProject), issue = useApiRead(`${base}/github/issues/${number}`, validateIssue, Number.isSafeInteger(number) && number > 0);
  const executions = useExecutions(projectExecutionsPath(projectId, number)), names = useNames(), action = useAction();
  const attempts = [...(executions.data ?? [])].sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc));
  const usage = issueUsage(attempts);
  const latest = attempts[0], status = issueStatus(issue.data, latest, project.data?.issueReadyLabel ?? 'codex-ready');
  const reason = latest ? explain(latest) : undefined, title = issue.data?.title ?? latest?.workReference?.title ?? '';
  const enqueue = (done: 'actions.queued') => action.run(`enqueue-${projectId}-${number}`, `${base}/github/issues/${number}/enqueue`, 'POST', {}, done);
  const cancel = () => latest && action.run(`cancel-${latest.id}`, `/api/v1/executions/${encodeURIComponent(latest.id)}/cancel`, 'POST', {}, 'actions.cancelled');
  const actions: { label: Parameters<typeof t>[0]; run?: () => unknown; href?: string; tone?: 'primary' | 'secondary' | 'secondary-destructive' }[] =
    latest?.state === 'Queued' ? [{ label: 'actions.removeFromQueue', run: cancel, tone: 'secondary-destructive' }]
    : latest && isActive(latest) ? [{ label: 'actions.viewExecution', href: `/executions/${encodeURIComponent(latest.id)}`, tone: 'primary' }]
    : latest?.state === 'Failed' ? executionActions(latest, false)[0] === 'retryFresh'
      ? [{ label: 'actions.retryFresh', run: () => action.run(`enqueue-${projectId}-${number}`, retryIssuePath(projectId, number), 'POST', {}, 'actions.retryQueued'), tone: 'primary' }, { label: 'actions.viewExecution', href: `/executions/${encodeURIComponent(latest.id)}` }]
      // Its execution page offers the action that resolves it (retry only the integration, resolve an uncertain result...).
      : [{ label: executionActions(latest, false).length ? 'actions.resolve' : 'actions.viewExecution', href: `/executions/${encodeURIComponent(latest.id)}`, tone: 'primary' }]
    : [{ label: latest ? 'actions.runAgain' : 'issues.enqueue', run: () => enqueue('actions.queued'), tone: 'primary' }];
  const labels = issue.data?.labels ?? [], selection = issue.data?.selection;

  return <>
    <PageHeader crumbs={[{ label: t('nav.projects'), to: '/projects' }, { label: project.data?.name ?? projectId, to: `/projects/${encodeURIComponent(projectId)}` }, { label: `#${number}` }]}
      title={`#${number}${title ? ` · ${title}` : ''}`}
      description={<span className="flex flex-wrap items-center gap-2"><StatusPill status={status} />{project.data?.repository}
        {issue.data?.author && <> · {t('issues.openedBy', { author: issue.data.author })}</>}{issue.data && <> · {t('issues.updated')} <Ago value={issue.data.updatedAtUtc} /></>}</span>}
      actions={<>{issue.data?.url && <Button color="secondary" href={issue.data.url} target="_blank" rel="noreferrer">{t('actions.openGitHub')}</Button>}
        {actions[0].run ? <Button color={actions[0].tone ?? 'primary'} isDisabled={!!action.busy} onClick={() => void actions[0].run?.()}>{t(actions[0].label)}</Button>
          : <Button color="primary" href={actions[0].href ?? "#"}>{t(actions[0].label)}</Button>}</>} />

    {reason && latest?.state === 'Failed' && <Card><Explanation what={t(reason.what)} todo={t(reason.todo)} /></Card>}

    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)] lg:items-start">
      <div className="flex min-w-0 flex-col gap-6">
        <Card title={t('issues.description')} actions={labels.length ? <span className="flex flex-wrap gap-1.5">{labels.map(label =>
          <span key={label} className="rounded-md px-1.5 py-0.5 text-xs text-secondary ring-1 ring-primary">{label}</span>)}</span> : undefined}>
          <ReadState read={issue}>{issue.data?.body ? <Markdown source={issue.data.body} /> : <p className="text-sm text-tertiary">{t('issues.noDescription')}</p>}</ReadState>
        </Card>
        <Card title={t('issues.attempts')} actions={<span className="text-sm text-tertiary">{attempts.length || ''}</span>} flush>
          <ReadState read={executions}><Rows>
            {attempts.length ? attempts.map(attempt => <RowLink key={attempt.id} to={`/executions/${encodeURIComponent(attempt.id)}`} className="grid-cols-1 gap-y-3">
              <span className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium text-primary">{t('issues.attemptOn', { n: attempt.attemptNumber ?? 1, worker: names.worker(attempt.assignedWorkerId) })}</span>
                <span className="flex items-center gap-3 text-sm text-tertiary"><Ago value={attempt.startedAtUtc ?? attempt.createdAtUtc} /><StatusPill status={executionStatus(attempt)} /></span>
              </span>
              <Steps execution={attempt} />
            </RowLink>) : <Empty>{issue.data?.blockedBy.length ? t('issues.waitsFor', { list: issue.data.blockedBy.map(blocker => `#${blocker.number}`).join(', ') }) : t('issues.noAttempts')}</Empty>}
          </Rows></ReadState>
        </Card>
      </div>
      <div className="flex min-w-0 flex-col gap-6">
        <Card title={t('issues.status')} actions={<StatusPill status={status} />}><KeyValues items={[
          [t('executions.project'), project.data?.name ?? projectId],
          [t('executions.worker'), latest?.assignedWorkerId ? <TextLink to={`/workers/${encodeURIComponent(latest.assignedWorkerId)}`}>{names.worker(latest.assignedWorkerId)}</TextLink> : latest ? names.worker(undefined) : t('issues.unassigned')],
          [t('issues.dependsOn'), issue.data?.blockedBy.length ? issue.data.blockedBy.map(blocker => `#${blocker.number}`).join(', ') : t('issues.nothing')],
          [t('executions.engine'), `${selection?.engine === 'ccc' ? 'Claude Code' : 'Codex'}${selection?.effort ? ` · ${selection.effort}` : ''}${selection?.model ? ` · ${selection.model}` : ''}`],
          [t('issues.timeSpent'), attempts.length ? t('issues.timeSpentValue', { time: duration(totalDuration(attempts)), n: attempts.length }) : '—'],
          ...(usage.withUsage ? [[t('usage.title'), t('usage.engineTotals', { input: tokens(usage.inputTokens), output: tokens(usage.outputTokens) }) +
            (usage.costUsd !== undefined ? ` · ${usd(usage.costUsd)}` : '')] as [string, string]] : []),
          [t('issues.eligible'), issue.data ? (issue.data.isEligible ? t('issues.yes') : issue.data.eligibilityReasons.join(' · ') || t('issues.no')) : '—']
        ]} /></Card>
        <Card title={t('issues.actions')}><div className="flex flex-col gap-2">
          {actions.map(item => item.run
            ? <Button key={item.label} color={item.tone ?? 'secondary'} isDisabled={!!action.busy} onClick={() => void item.run?.()}>{t(item.label)}</Button>
            : <Button key={item.label} color={item.tone ?? 'secondary'} href={item.href ?? "#"}>{t(item.label)}</Button>)}
          {issue.data?.url && <Button color="secondary" href={issue.data.url} target="_blank" rel="noreferrer">{t('actions.editInGitHub')}</Button>}
        </div></Card>
      </div>
    </div>
  </>;
}
