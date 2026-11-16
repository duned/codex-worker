import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useExecutions, useNames, useWorkers } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { attention, explain, isActive, isOnline, issueNumber, scheduling } from '../shared/model';
import { Card, Empty, Filters, matches, Notice, PageHeader, ReadState, Row, Rows } from '../shared/ui';
import { ActiveExecution, ExecutionHead, ExecutionRow, executionLabel } from './ExecutionRow';

export function HomePage() {
  useLanguage();
  const executions = useExecutions(), workers = useWorkers(), names = useNames(), action = useAction();
  const [query, setQuery] = useState(''), [status, setStatus] = useState('');
  const all = executions.data ?? [], workerList = workers.data ?? [];
  const failed = attention(all);
  const troubled = workerList.filter(worker => scheduling(worker) === 'blocked');
  const active = all.filter(isActive), queued = all.filter(item => item.state === 'Queued');
  const finished = all.filter(item => ['Completed', 'Failed'].includes(item.state))
    .filter(item => matches(`${executionLabel(item)} ${names.project(item.projectId)} ${names.worker(item.assignedWorkerId)}`, query))
    .filter(item => !status || (status === 'ok' ? item.state === 'Completed' : item.state === 'Failed')).slice(0, 15);
  const problems = failed.length + troubled.length;

  return <>
    <PageHeader title={t('home.title')} description={t('home.summary', {
      online: workerList.filter(isOnline).length, workers: workerList.length, active: active.length, queued: queued.length })} />
    {!executions.loading && !workers.loading && <Notice tone={problems ? 'bad' : 'ok'}>
      <strong>{problems ? t('home.attentionCount', { n: problems }) : t('home.allGood')}</strong> {problems ? t('home.restOk') : ''}
    </Notice>}

    {problems > 0 && <Card title={t('home.attention')} flush><Rows>
      {failed.map(execution => {
        const reason = explain(execution), number = issueNumber(execution);
        return <Row key={execution.id} className="grid-cols-1 md:grid-cols-[minmax(0,1fr)_auto]">
          <div className="min-w-0">
            <div className="font-medium text-primary">{t('home.issueFailed', { issue: executionLabel(execution), project: names.project(execution.projectId) })}</div>
            {reason && <div className="text-sm text-secondary">{t(reason.what)}</div>}
          </div>
          <div className="flex flex-wrap gap-2">
            {number && <Button size="sm" color="primary" isDisabled={!!action.busy} onClick={() => void action.run(`enqueue-${execution.projectId}-${number}`,
              `/api/v1/projects/${encodeURIComponent(execution.projectId)}/github/issues/${number}/enqueue`, 'POST', {}, 'actions.queued')}>{t('actions.retryFresh')}</Button>}
            <Button size="sm" color="secondary" href={`/executions/${encodeURIComponent(execution.id)}`}>{t('actions.viewDetail')}</Button>
          </div>
        </Row>;
      })}
      {troubled.map(worker => <Row key={worker.workerId} className="grid-cols-1 md:grid-cols-[minmax(0,1fr)_auto]">
        <div><div className="font-medium text-primary">{t('home.workerCannotWork', { worker: worker.displayName || worker.workerId })}</div>
          <div className="text-sm text-secondary">{worker.schedulingBlocker ?? t(isOnline(worker) ? 'workers.blockedReason' : 'workers.offlineReason')}</div></div>
        <Button size="sm" color="secondary" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{t('actions.viewWorker')}</Button>
      </Row>)}
    </Rows></Card>}

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
