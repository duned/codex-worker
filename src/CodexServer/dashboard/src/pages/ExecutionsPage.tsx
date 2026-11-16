import { useState } from 'react';
import { useExecutions, useNames } from '../shared/data';
import { t, useLanguage, type TranslationKey } from '../shared/i18n';
import { executionStatus } from '../shared/model';
import { Card, Empty, Filters, matches, PageHeader, ReadState, Rows } from '../shared/ui';
import { ExecutionHead, ExecutionRow, executionLabel } from './ExecutionRow';

const states: { value: string; label: TranslationKey }[] = [
  { value: 'Queued', label: 'status.queued' }, { value: 'Running', label: 'status.running' }, { value: 'Completed', label: 'status.integrated' },
  { value: 'Failed', label: 'status.failed' }, { value: 'Cancelled', label: 'status.cancelled' }];

export function ExecutionsPage() {
  useLanguage();
  const executions = useExecutions(), names = useNames();
  const [query, setQuery] = useState(''), [state, setState] = useState('');
  const rows = (executions.data ?? [])
    .filter(item => !state || (state === 'Running' ? ['Assigned', 'Running'].includes(item.state) : item.state === state))
    .filter(item => matches(`${executionLabel(item)} ${names.project(item.projectId)} ${names.worker(item.assignedWorkerId)} ${t(executionStatus(item).label)}`, query));
  return <>
    <PageHeader title={t('executions.title')} description={t('executions.description')} />
    <Card flush>
      <Filters query={query} onQuery={setQuery} status={state} onStatus={setState} placeholder={t('executions.search')}
        options={states.map(item => ({ value: item.value, label: t(item.label) }))} />
      <ReadState read={executions}><Rows>
        <ExecutionHead />
        {rows.length ? rows.map(execution => <ExecutionRow key={execution.id} execution={execution} />) : <Empty>{t('common.noMatches')}</Empty>}
      </Rows></ReadState>
    </Card>
  </>;
}
