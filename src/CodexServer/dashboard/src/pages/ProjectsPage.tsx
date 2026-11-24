import { useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useExecutions, useProjects, useSchedulingPauses } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { attention, isActive } from '../shared/model';
import { Card, Empty, Filters, HeadRow, matches, PageHeader, Pill, ReadState, RowLink, Rows } from '../shared/ui';
import { ProjectPanel } from './ProjectPanel';

const columns = 'md:grid-cols-[minmax(0,2fr)_minmax(0,1.6fr)_90px_90px_160px]';

export function ProjectsPage() {
  useLanguage();
  const projects = useProjects(), executions = useExecutions(), pauses = useSchedulingPauses();
  const paused = new Set((pauses.data ?? []).map(pause => pause.projectId));
  const [query, setQuery] = useState(''), [status, setStatus] = useState(''), [creating, setCreating] = useState(false);
  const all = executions.data ?? [], failing = new Set(attention(all).map(item => item.projectId));
  const rows = (projects.data ?? []).map(project => {
    const own = all.filter(item => item.projectId === project.id);
    const state = project.enabled === false || paused.has(project.id) ? 'paused' : failing.has(project.id) ? 'attention' : 'active';
    return { project, state, running: own.filter(isActive).length, queued: own.filter(item => item.state === 'Queued').length };
  }).filter(row => (!status || row.state === status) && matches(`${row.project.name} ${row.project.repository}`, query));
  return <>
    <PageHeader title={t('projects.title')} description={t('projects.description')}
      actions={<Button color="primary" onClick={() => setCreating(true)}>{t('projects.add')}</Button>} />
    <Card flush>
      <Filters query={query} onQuery={setQuery} status={status} onStatus={setStatus} placeholder={t('projects.search')}
        options={[{ value: 'active', label: t('projects.active') }, { value: 'attention', label: t('common.needsAttention') }, { value: 'paused', label: t('projects.paused') }]} />
      <ReadState read={projects}><Rows>
        <HeadRow className={columns}><span>{t('projects.project')}</span><span>{t('projects.repository')}</span><span>{t('projects.running')}</span><span>{t('projects.queued')}</span><span>{t('common.status')}</span></HeadRow>
        {rows.length ? rows.map(({ project, state, running, queued }) => <RowLink key={project.id} to={`/projects/${encodeURIComponent(project.id)}`} className={`grid-cols-1 ${columns}`}>
          <span className="font-medium text-primary">{project.name}</span>
          <span className="text-sm text-tertiary">{project.repository} · {project.defaultBranch ?? 'main'}</span>
          <span className="text-sm text-tertiary tabular-nums">{running}</span>
          <span className="text-sm text-tertiary tabular-nums">{queued}</span>
          <span>{state === 'attention' ? <Pill tone="bad">{t('common.needsAttention')}</Pill> : state === 'paused' ? <Pill tone="warn">{t('projects.paused')}</Pill> : <Pill tone={running ? 'run' : 'ok'}>{t(running ? 'projects.working' : 'projects.active')}</Pill>}</span>
        </RowLink>) : <Empty>{projects.data?.length ? t('common.noMatches') : t('projects.none')}</Empty>}
      </Rows></ReadState>
    </Card>
    <ProjectPanel open={creating} onClose={() => setCreating(false)} />
  </>;
}
