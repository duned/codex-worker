import { useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useArchive } from '../shared/actions';
import { archivedExecutionsPath, recentExecutionsPath, useExecutions, useNames } from '../shared/data';
import { t, useLanguage, type TranslationKey } from '../shared/i18n';
import { canArchive, executionStatus } from '../shared/model';
import { Card, Empty, Filters, matches, PageHeader, ReadState, Rows, Select } from '../shared/ui';
import { ExecutionHead, ExecutionRow, executionLabel } from './ExecutionRow';

const states: { value: string; label: TranslationKey }[] = [
  { value: 'Queued', label: 'status.queued' }, { value: 'Running', label: 'status.running' }, { value: 'Completed', label: 'status.integrated' },
  { value: 'Failed', label: 'status.failed' }, { value: 'Cancelled', label: 'status.cancelled' }];

export function ExecutionsPage() {
  useLanguage();
  const [view, setView] = useState<'current' | 'archived'>('current');
  const archivedView = view === 'archived';
  const executions = useExecutions(archivedView ? archivedExecutionsPath : recentExecutionsPath), names = useNames(), archive = useArchive();
  const [query, setQuery] = useState(''), [state, setState] = useState(''), [selected, setSelected] = useState<Set<string>>(new Set());
  const rows = (executions.data ?? [])
    .filter(item => !state || (state === 'Running' ? ['Assigned', 'Running'].includes(item.state) : item.state === state))
    .filter(item => matches(`${executionLabel(item)} ${names.project(item.projectId)} ${names.worker(item.assignedWorkerId)} ${t(executionStatus(item).label)}`, query));
  // Archived rows can always be restored; current rows only when finished.
  const selectable = rows.filter(item => archivedView || canArchive(item));
  const chosen = selectable.filter(item => selected.has(item.id)).map(item => item.id);
  const toggle = (id: string, value: boolean) => setSelected(current => {
    const next = new Set(current);
    if (value) next.add(id); else next.delete(id);
    return next;
  });
  const switchView = (value: string) => { setView(value === 'archived' ? 'archived' : 'current'); setSelected(new Set()); };
  const apply = async () => { if (await archive.run(chosen, !archivedView)) setSelected(new Set()); };

  return <>
    <PageHeader title={t('executions.title')} description={t(archivedView ? 'executions.archivedDescription' : 'executions.description')}
      actions={<Button color="secondary" isDisabled={!chosen.length || archive.busy} onClick={() => void apply()}>
        {t(archivedView ? 'actions.restoreSelected' : 'actions.archiveSelected', { n: chosen.length })}</Button>} />
    <Card flush>
      <Filters query={query} onQuery={setQuery} status={state} onStatus={setState} placeholder={t('executions.search')}
        options={states.map(item => ({ value: item.value, label: t(item.label) }))}>
        <Select label={t('executions.view')} value={view} onChange={switchView} hideLabel
          options={[{ value: 'current', label: t('executions.viewCurrent') }, { value: 'archived', label: t('executions.viewArchived') }]} />
      </Filters>
      <ReadState read={executions}><Rows>
        <ExecutionHead selection={{ checked: !!selectable.length && chosen.length === selectable.length, disabled: !selectable.length,
          onChange: value => setSelected(value ? new Set(selectable.map(item => item.id)) : new Set()) }} />
        {rows.length ? rows.map(execution => <ExecutionRow key={execution.id} execution={execution}
          selection={{ checked: selected.has(execution.id), disabled: !selectable.includes(execution), onChange: value => toggle(execution.id, value) }} />)
          : <Empty>{t(archivedView ? 'executions.noArchived' : 'common.noMatches')}</Empty>}
      </Rows></ReadState>
    </Card>
  </>;
}
