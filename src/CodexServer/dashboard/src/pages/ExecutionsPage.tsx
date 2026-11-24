import { useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useArchive } from '../shared/actions';
import { archivedExecutionsPath, recentExecutionsPath, useExecutions, useNames } from '../shared/data';
import { t, useLanguage, type TranslationKey } from '../shared/i18n';
import { canArchive, duration, engineStats, executionStatus } from '../shared/model';
import { Card, Empty, Filters, HeadRow, matches, PageHeader, ReadState, Row, Rows, Select } from '../shared/ui';
import { ExecutionHead, ExecutionRow, executionLabel } from './ExecutionRow';

const states: { value: string; label: TranslationKey }[] = [
  { value: 'Queued', label: 'status.queued' }, { value: 'Running', label: 'status.running' }, { value: 'Completed', label: 'status.integrated' },
  { value: 'Failed', label: 'status.failed' }, { value: 'Cancelled', label: 'status.cancelled' }];

const engineColumns = 'md:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_100px_110px_110px]';

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
  const engines = archivedView ? [] : engineStats(executions.data ?? []);

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
    {engines.length > 0 && <Card title={t('executions.byEngine')} actions={<span className="text-sm text-tertiary">{t('executions.byEngineScope')}</span>} flush><Rows>
      <HeadRow className={engineColumns}><span>{t('executions.engine')}</span><span>{t('executions.finished')}</span><span>{t('executions.successRate')}</span>
        <span>{t('executions.medianTime')}</span><span>{t('executions.totalTime')}</span></HeadRow>
      {engines.map(item => <Row key={item.engine} className={`grid-cols-2 ${engineColumns}`}>
        <span className="font-medium text-primary">{item.engine === 'ccc' ? 'Claude Code' : 'Codex'}</span>
        <span className="text-sm text-secondary tabular-nums">{t('executions.finishedCount', { n: item.finished, ok: item.integrated, failed: item.failed })}</span>
        <span className="text-sm text-secondary tabular-nums">{Math.round(item.integrated * 100 / item.finished)} %</span>
        <span className="text-sm text-secondary tabular-nums">{duration(item.medianMs)}</span>
        <span className="text-sm text-secondary tabular-nums">{duration(item.totalMs)}</span>
      </Row>)}
    </Rows></Card>}
  </>;
}
