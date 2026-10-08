import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useApiRead } from '../../shared/api/session';
import { workers, projects, executions } from '../../shared/api/validation';
import { useServerReadiness } from '../server/useServerReadiness';
import { completedExecutions, completedStatuses, recentProjects, repositoryLink, sectionMessage } from './model';
import { PageHeading, Notice, StatusBadge } from '../../shared/Presentation';
import { ExternalLink } from '../../shared/Actions';
import { Button } from '../../untitled/components/base/buttons/button';
import { issueLink, timestamp, duration, statusColor } from '../../model';
import { presentation } from '../executions/model';
import type { ExecutionSummary } from '../../shared/api/contracts';

export function HomePage() {
  const registry = useApiRead('/api/v1/workers', workers);
  const catalog = useApiRead('/api/v1/projects', projects);
  const activity = useApiRead('/api/v1/executions?limit=50&offset=0', executions);
  const readiness = useServerReadiness();
  const [filter, setFilter] = useState('All');
  const current = activity.data?.filter(item => ['Assigned', 'Running'].includes(item.state)) ?? [];
  const completed = completedExecutions(activity.data ?? [], filter);
  const linkClass = 'text-brand-secondary underline break-words';
  function issue(item: ExecutionSummary) {
    const project = catalog.data?.find(project => project.id === item.projectId);
    const href = issueLink(item.workReference, project?.repository);
    const label = item.workReference ? `${item.workReference.type === 'github-issue' ? 'Issue #' : `${item.workReference.type} `}${item.workReference.id}` : 'Issue unavailable';
    return <span>{href ? <ExternalLink href={href}>{label}</ExternalLink> : label}<span className="text-tertiary"> · Title unavailable</span></span>;
  }
  function executionRow(item: ExecutionSummary, active = false) {
    const state = presentation(item);
    return <li key={item.id} className="space-y-2 border-b border-secondary py-4 last:border-0">
      <div className="flex flex-wrap items-center justify-between gap-3"><div className="min-w-0 break-words">{issue(item)}</div><StatusBadge tone={state.tone}>{state.text}</StatusBadge></div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm"><Link className={linkClass} to={`/projects/${encodeURIComponent(item.projectId)}`}>{catalog.data?.find(p => p.id === item.projectId)?.name ?? 'Project name unavailable'}</Link>
        {item.assignedWorkerId ? <Link className={linkClass} to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{registry.data?.find(w => w.workerId === item.assignedWorkerId)?.displayName ?? 'Worker name unavailable'}</Link> : <span>Worker unassigned</span>}
        <Link className={linkClass} to={`/executions/${encodeURIComponent(item.id)}`}>Execution details</Link></div>
      {active ? <p className="text-sm text-tertiary">Stage: {item.currentStage ?? 'Unavailable'} · Elapsed: {duration(item, Date.now())}</p> : <><p className="line-clamp-2 break-words text-sm">{item.completionSummary || 'Summary not reported'}</p><p className="text-sm text-tertiary">Completed: {timestamp(item.completedAtUtc)}</p></>}
    </li>;
  }
  function readState(read: { data?: unknown[]; loading: boolean; error?: string; stale: boolean; updatedAt: number }, empty: string) {
    const message = sectionMessage(read.data, read.loading, read.error, empty);
    return <>{message && <Notice error={!!read.error}>{message}</Notice>}{read.data && read.error && <p className="mb-3 text-sm text-warning-primary">Retained observations are stale · observed {timestamp(new Date(read.updatedAt).toISOString())}</p>}</>;
  }
  async function refresh() {
    await Promise.all([registry.refetch(), catalog.refetch(), activity.refetch(), readiness.inventory.refetch()]);
  }
  return <div className="space-y-6 text-secondary">
    <PageHeading title="Home" actions={<Button color="secondary" onPress={() => { void refresh(); }}>Refresh system state</Button>} />
    <div className="grid items-start gap-6 lg:grid-cols-2">
      <section aria-labelledby="home-workers"><h2 id="home-workers" className="mb-4 text-lg font-semibold text-primary">Workers</h2><div className="space-y-4">{readState(registry, 'No Workers registered. Add a Worker to get started.')}
        <div className="grid gap-3 sm:grid-cols-2">{registry.data?.map(worker => {
          const node = readiness.inventory.data?.find(n => n.kind === 'worker' && n.id === worker.workerId);
          return <div key={worker.workerId} className="min-w-0 space-y-2 rounded-xl border border-secondary bg-primary p-4"><Link className={`${linkClass} font-medium`} to={`/workers/${encodeURIComponent(worker.workerId)}`}>{worker.displayName ?? 'Worker name unavailable'}</Link>
            <p><StatusBadge tone={statusColor(worker.availability)}>{worker.availability}</StatusBadge></p>
            <p className="text-sm">{worker.activeExecutions ?? 'Unknown'} / {worker.maximumCapacity ?? worker.capacity ?? 'Unknown'} slots occupied</p>
            <p className="text-xs text-tertiary">{worker.lifecycleState ?? 'Lifecycle unavailable'} · Scheduling {worker.schedulingPolicy ?? 'unavailable'}</p>
            <p className="text-xs text-tertiary">{!node ? 'Readiness unavailable' : node.observationsStale ? 'Readiness stale' : `Prerequisites: ${node.executionReadiness}`}</p>
          </div>;
        })}</div>{readiness.inventory.error && <Notice error>Readiness unavailable. Refresh system state to retry.</Notice>}<Button href="/workers" color="link-color">All Workers</Button></div></section>
      <section aria-labelledby="home-projects"><h2 id="home-projects" className="text-lg font-semibold text-primary">Projects</h2><p className="mt-1 text-sm text-tertiary">Latest five by recorded activity in the latest 50 execution requests.</p><div>{readState(catalog, 'No projects registered. Create a project to get started.')}
        <ul>{recentProjects(catalog.data ?? [], activity.data ?? []).map(({ project, latest }) => <li key={project.id} className="space-y-2 border-b border-secondary py-4 last:border-0">
          <div className="flex flex-wrap items-center justify-between gap-2"><Link className={`${linkClass} font-medium`} to={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</Link><StatusBadge tone={project.enabled === true ? 'success' : 'gray'}>{project.enabled == null ? 'Status unavailable' : project.enabled ? 'Enabled' : 'Disabled'}</StatusBadge></div>
          <p className="break-words text-sm">{repositoryLink(project.repository) ? <ExternalLink href={repositoryLink(project.repository)}>{project.repository}</ExternalLink> : 'Repository link unavailable'}</p>
          {!activity.data ? <p className="text-sm text-tertiary">Execution activity unavailable</p> : latest ? <div className="space-y-1 text-sm"><p>{issue(latest)}</p><Link className={linkClass} to={`/executions/${encodeURIComponent(latest.id)}`}>{latest.state} · {timestamp(latest.completedAtUtc ?? latest.startedAtUtc ?? latest.assignedAtUtc ?? latest.createdAtUtc)}</Link></div> : <p className="text-sm text-tertiary">No recorded execution activity in this snapshot</p>}
        </li>)}</ul><Button href="/projects" color="link-color">All Projects</Button></div></section>
    </div>
    <section className="border-t border-secondary pt-6" aria-labelledby="home-current"><h2 id="home-current" className="text-lg font-semibold text-primary">{`Current executions${activity.data ? ` · ${current.length} active` : ''}`}</h2><p className="mb-3 text-sm text-tertiary">Assigned and running requests in the latest 50 requests; older active work may be outside this snapshot.</p><div>{readState(activity, 'No execution requests yet.')}{activity.data && !current.length && <p>No active executions in this snapshot.</p>}<ul>{current.map(item => executionRow(item, true))}</ul><Button href="/executions" color="link-color">All executions</Button></div></section>
    <section className="border-t border-secondary pt-6" aria-labelledby="home-completed"><h2 id="home-completed" className="text-lg font-semibold text-primary">Recent completed executions</h2><p className="mb-3 text-sm text-tertiary">Terminal outcomes in the latest 50 requests.</p><div className="space-y-4">
      <label className="block text-sm">Status<select className="ml-3 rounded-lg border border-secondary bg-primary p-2" value={filter} onChange={event => setFilter(event.target.value)}>{['All', ...completedStatuses].map(state => <option key={state}>{state}</option>)}</select></label>
      {activity.data && !completed.length && <p>{completedExecutions(activity.data).length ? 'No completed executions match this status.' : 'No completed executions in this snapshot.'}</p>}<ul>{completed.map(item => executionRow(item))}</ul>
    </div></section>
  </div>;
}
