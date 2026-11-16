import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead } from '../shared/api/session';
import { issues as validateIssues, project as validateProject } from '../shared/api/validation';
import { projectExecutionsPath, useExecutions, useWorkers } from '../shared/data';
import { t, useLanguage } from '../shared/i18n';
import { issueStatus, latestByIssue, type Tone } from '../shared/model';
import { Card, Empty, Filters, KeyValues, matches, PageHeader, Pill, ReadState, RowLink, Rows, StatusPill } from '../shared/ui';
import { EnqueuePanel } from './EnqueuePanel';
import { ProjectPanel } from './ProjectPanel';

const issueFilters: { value: Tone; label: Parameters<typeof t>[0] }[] = [
  { value: 'idle', label: 'issues.filterWaiting' }, { value: 'run', label: 'issues.filterRunning' },
  { value: 'ok', label: 'issues.filterDone' }, { value: 'bad', label: 'common.needsAttention' }, { value: 'warn', label: 'issues.filterBlocked' }];

export function ProjectPage() {
  useLanguage();
  const { projectId = '' } = useParams(), base = `/api/v1/projects/${encodeURIComponent(projectId)}`;
  const project = useApiRead(base, validateProject), workers = useWorkers(), action = useAction();
  const [scope, setScope] = useState<'open' | 'all'>('open');
  const issues = useApiRead(`${base}/github/issues?state=${scope}&limit=100`, validateIssues);
  const executions = useExecutions(projectExecutionsPath(projectId));
  const [query, setQuery] = useState(''), [status, setStatus] = useState(''), [editing, setEditing] = useState(false), [enqueue, setEnqueue] = useState(false);
  const item = project.data;
  if (!item) return <><PageHeader title={t('projects.title')} crumbs={[{ label: t('nav.projects'), to: '/projects' }]} /><Card flush><ReadState read={project}>{null}</ReadState></Card></>;
  const latest = latestByIssue(executions.data ?? []);
  const known = new Map((issues.data ?? []).map(issue => [issue.number, issue]));
  // Issues the Server has worked on stay visible even when GitHub no longer lists them as open.
  const numbers = [...new Set([...(issues.data ?? []).map(issue => issue.number), ...(scope === 'all' ? latest.keys() : [])])].sort((a, b) => b - a);
  const rows = numbers.map(number => {
    const issue = known.get(number), execution = latest.get(number);
    return { number, issue, execution, status: issueStatus(issue, execution, item.issueReadyLabel ?? 'codex-ready'), title: issue?.title ?? execution?.workReference?.title ?? '' };
  }).filter(row => (!status || row.status.tone === status) && matches(`#${row.number} ${row.title}`, query));
  const managed = (workers.data ?? []).filter(worker => worker.activeProjects?.includes(item.name) || worker.activeProjects?.includes(item.id));

  return <>
    <PageHeader crumbs={[{ label: t('nav.projects'), to: '/projects' }, { label: item.name }]} title={item.name}
      badge={item.enabled === false ? <Pill tone="warn">{t('projects.paused')}</Pill> : undefined}
      description={<>{item.repository} · {t('projects.integratesInto')} <span className="font-mono text-sm">{item.defaultBranch ?? 'main'}</span></>}
      actions={<>
        <Button color="secondary" isDisabled={!!action.busy} onClick={() => void action.run(`project-${item.id}`, `${base}/lifecycle`, 'PUT',
          { enabled: item.enabled === false, expectedRevision: item.revision }, item.enabled === false ? 'projects.resumed' : 'projects.pausedDone')}>
          {t(item.enabled === false ? 'projects.resume' : 'projects.pause')}</Button>
        <Button color="secondary" onClick={() => setEnqueue(true)}>{t('issues.enqueue')}</Button>
        <Button color="primary" onClick={() => setEditing(true)}>{t('projects.edit')}</Button>
      </>} />
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)] lg:items-start">
      <Card title={t('issues.title')} actions={<div className="flex rounded-lg ring-1 ring-primary" role="group" aria-label={t('issues.scope')}>
        {(['open', 'all'] as const).map(value => <button key={value} type="button" aria-pressed={scope === value} onClick={() => setScope(value)}
          className={`px-3 py-1 text-sm font-semibold first:rounded-l-lg last:rounded-r-lg ${scope === value ? 'bg-secondary text-primary' : 'text-tertiary'}`}>{t(value === 'open' ? 'issues.open' : 'issues.all')}</button>)}
      </div>} flush>
        <Filters query={query} onQuery={setQuery} status={status} onStatus={setStatus} placeholder={t('issues.search')} options={issueFilters.map(option => ({ value: option.value, label: t(option.label) }))} />
        <ReadState read={issues}><Rows>
          {rows.length ? rows.map(row => <RowLink key={row.number} to={`/projects/${encodeURIComponent(item.id)}/issues/${row.number}`} className="grid-cols-[minmax(0,1fr)_auto]">
            <span className="min-w-0"><span className="font-medium break-words text-primary">#{row.number} · {row.title}</span>
              {row.issue?.blockedBy.some(blocker => blocker.state?.toLowerCase() !== 'closed') && <span className="block text-sm text-tertiary">
                {t('issues.waitsFor', { list: row.issue.blockedBy.filter(blocker => blocker.state?.toLowerCase() !== 'closed').map(blocker => `#${blocker.number}`).join(', ') })}</span>}</span>
            <StatusPill status={row.status} />
          </RowLink>) : <Empty>{issues.data?.length ? t('common.noMatches') : t('issues.none')}</Empty>}
        </Rows></ReadState>
      </Card>
      <Card title={t('projects.configuration')}><KeyValues items={[
        [t('projects.baseBranch'), <span className="font-mono text-sm">{item.defaultBranch ?? 'main'}</span>],
        [t('projects.parallel'), t('projects.upTo', { n: item.maxParallelTasks ?? 1 })],
        [t('projects.validation'), item.validationCommands?.length ? <span className="font-mono text-xs">{item.validationCommands.join(' && ')}</span> : t('projects.workerValidation')],
        [t('projects.pickup'), item.automaticDiscovery?.enabled ? t('projects.pickupAutomatic', { label: item.issueReadyLabel ?? 'codex-ready' }) : t('projects.pickupManual')],
        [t('nav.workers'), managed.length ? managed.map(worker => worker.displayName || worker.workerId.slice(0, 8)).join(', ') : t('projects.anyWorker')],
        ...(item.description ? [[t('projects.descriptionField'), item.description] as [string, string]] : [])
      ]} /></Card>
    </div>
    <ProjectPanel open={editing} onClose={() => setEditing(false)} project={item} />
    <EnqueuePanel open={enqueue} onClose={() => setEnqueue(false)} project={item} />
  </>;
}
