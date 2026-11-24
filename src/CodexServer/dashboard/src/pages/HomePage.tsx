import { useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { retryIssuePath, useExecutions, useNames, useWorkers } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { attentionCount, attentionInbox, executionActions, explain, isActive, isOnline, issueNumber, schedulingBlockerText, workerHealth } from '../shared/model';
import type { TranslationKey } from '../shared/i18n';
import { Card, Empty, Filters, matches, Notice, PageHeader, ReadState, Row, Rows } from '../shared/ui';
import { ActiveExecution, ExecutionHead, ExecutionRow, executionLabel } from './ExecutionRow';

export function HomePage() {
  useLanguage();
  const executions = useExecutions(), workers = useWorkers(), names = useNames(), action = useAction();
  const [query, setQuery] = useState(''), [status, setStatus] = useState('');
  const all = executions.data ?? [], workerList = workers.data ?? [];
  const inbox = attentionInbox(all, workerList);
  const active = all.filter(isActive), queued = all.filter(item => item.state === 'Queued');
  const finished = all.filter(item => ['Completed', 'Failed'].includes(item.state))
    .filter(item => matches(`${executionLabel(item)} ${names.project(item.projectId)} ${names.worker(item.assignedWorkerId)}`, query))
    .filter(item => !status || (status === 'ok' ? item.state === 'Completed' : item.state === 'Failed')).slice(0, 15);
  const problems = attentionCount(inbox);

  return <>
    <PageHeader title={t('home.title')} description={t('home.summary', {
      online: workerList.filter(isOnline).length, workers: workerList.length, active: active.length, queued: queued.length })} />
    {!executions.loading && !workers.loading && <Notice tone={problems ? 'bad' : 'ok'}>
      <strong>{problems ? t('home.attentionCount', { n: problems }) : t('home.allGood')}</strong> {problems ? t('home.restOk') : ''}
    </Notice>}

    {problems > 0 && <Card title={t('home.attention')} flush>
      <Group title="home.group.paused" count={inbox.pausedProjects.length}>
        {inbox.pausedProjects.map(({ worker, projectId, detail }) => <Item key={`${worker.workerId}/${projectId}`}
          title={t('home.projectPaused', { project: names.project(projectId), worker: names.worker(worker.workerId) })} detail={detail ?? t('workers.pausedNoReason')}>
          <Button size="sm" color="primary" isDisabled={!!action.busy} onClick={() => void action.run(`project-resume-${worker.workerId}`,
            `/api/v1/workers/${encodeURIComponent(worker.workerId)}/projects/${encodeURIComponent(projectId)}/resume`, 'POST', {}, 'workers.resumeRequested')}>{t('workers.resumeProject')}</Button>
          <Button size="sm" color="secondary" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{t('actions.viewWorker')}</Button>
        </Item>)}
      </Group>
      <Group title="home.group.maintenance" count={inbox.maintenance.length}>
        {inbox.maintenance.map(worker => <Item key={worker.workerId} title={t('home.maintenanceUnresolved', { worker: names.worker(worker.workerId) })}
          detail={schedulingBlockerText('worker-operation-unresolved')}>
          <Button size="sm" color="primary" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{t('actions.resolve')}</Button>
        </Item>)}
      </Group>
      <Group title="home.group.clock" count={inbox.clock.length}>
        {inbox.clock.map(worker => {
          const health = workerHealth(worker);
          return <Item key={worker.workerId} title={`${names.worker(worker.workerId)} · ${t(health.label)}`} detail={t(health.cause.key, health.cause.values)}>
            <Button size="sm" color="secondary" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{t('actions.viewWorker')}</Button>
          </Item>;
        })}
      </Group>
      <Group title="home.group.uncertain" count={inbox.uncertain.length}>
        {inbox.uncertain.map(execution => <Item key={execution.id}
          title={t('home.executionUncertain', { issue: executionLabel(execution), project: names.project(execution.projectId) })} detail={t('home.executionUncertainDetail')}>
          <Button size="sm" color="primary" href={`/executions/${encodeURIComponent(execution.id)}`}>{t('actions.resolve')}</Button>
        </Item>)}
      </Group>
      <Group title="home.group.ineligible" count={inbox.ineligible.length}>
        {inbox.ineligible.map(execution => {
          const number = issueNumber(execution);
          return <Item key={execution.id} title={t('home.queuedBlocked', { issue: executionLabel(execution), project: names.project(execution.projectId) })}
            detail={execution.managedEligibilityReasons?.join(' · ') || t('home.queuedBlockedDetail')}>
            {number && <Button size="sm" color="primary" isDisabled={!!action.busy} onClick={() => void action.run(`eligibility-${execution.id}`,
              `/api/v1/projects/${encodeURIComponent(execution.projectId)}/github/issues/${number}/eligibility/refresh`, 'POST', {}, 'actions.eligibilityRechecked')}>
              {t('actions.recheckEligibility')}</Button>}
            <Button size="sm" color="secondary" href={`/executions/${encodeURIComponent(execution.id)}`}>{t('actions.viewDetail')}</Button>
          </Item>;
        })}
      </Group>
      <Group title="home.group.failed" count={inbox.failed.length}>
        {inbox.failed.map(execution => {
          const reason = explain(execution), number = issueNumber(execution);
          return <Item key={execution.id} title={t('home.issueFailed', { issue: executionLabel(execution), project: names.project(execution.projectId) })}
            detail={reason ? t(reason.what) : undefined}>
            {number && executionActions(execution, false)[0] === 'retryFresh' ? <>
              <Button size="sm" color="primary" isDisabled={!!action.busy} onClick={() => void action.run(`enqueue-${execution.projectId}-${number}`,
                retryIssuePath(execution.projectId, number), 'POST', {}, 'actions.retryQueued')}>{t('actions.retryFresh')}</Button>
              <Button size="sm" color="secondary" href={`/executions/${encodeURIComponent(execution.id)}`}>{t('actions.viewDetail')}</Button></>
              : <Button size="sm" color="primary" href={`/executions/${encodeURIComponent(execution.id)}`}>{t('actions.resolve')}</Button>}
          </Item>;
        })}
      </Group>
      <Group title="home.group.workers" count={inbox.workers.length}>
        {inbox.workers.map(worker => <Item key={worker.workerId} title={t('home.workerCannotWork', { worker: worker.displayName || worker.workerId })}
          detail={(worker.schedulingBlocker && schedulingBlockerText(worker.schedulingBlocker)) || t(isOnline(worker) ? 'workers.blockedReason' : 'workers.offlineReason')}>
          <Button size="sm" color="secondary" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{t('actions.viewWorker')}</Button>
        </Item>)}
      </Group>
    </Card>}

    <Card title={t('home.running')} actions={<Link className="text-sm font-semibold text-brand-secondary" to="/executions">{t('home.allExecutions')}</Link>} flush>
      <ReadState read={executions}><Rows>
        {active.length ? active.map(execution => <ActiveExecution key={execution.id} execution={execution} />) : <Empty>{t('home.nothingRunning')}</Empty>}
      </Rows></ReadState>
    </Card>

    <Card title={t('home.finished')} flush>
      <Filters query={query} onQuery={setQuery} status={status} onStatus={setStatus} placeholder={t('executions.search')}
        options={[{ value: 'ok', label: t('status.integrated') }, { value: 'bad', label: t('home.withProblems') }]} />
      <ReadState read={executions}><Rows>
        <ExecutionHead />
        {finished.length ? finished.map(execution => <ExecutionRow key={execution.id} execution={execution} />) : <Empty>{t('common.noMatches')}</Empty>}
      </Rows></ReadState>
    </Card>
  </>;
}

/** One cause in the attention inbox; hidden when nothing has that cause. */
function Group({ title, count, children }: { title: TranslationKey; count: number; children: ReactNode }) {
  if (!count) return null;
  return <section aria-label={t(title)} className="border-t border-secondary first:border-t-0">
    <h3 className="bg-secondary px-5 py-2 text-xs font-semibold tracking-wide text-tertiary uppercase">{t(title)} · {count}</h3>
    <Rows>{children}</Rows>
  </section>;
}

function Item({ title, detail, children }: { title: string; detail?: string; children: ReactNode }) {
  return <Row className="grid-cols-1 md:grid-cols-[minmax(0,1fr)_auto]">
    <div className="min-w-0">
      <div className="font-medium text-primary">{title}</div>
      {detail && <div className="text-sm break-words text-secondary">{detail}</div>}
    </div>
    <div className="flex flex-wrap gap-2">{children}</div>
  </Row>;
}
