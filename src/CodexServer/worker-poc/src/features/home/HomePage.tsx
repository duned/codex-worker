import { t, useLanguage, statusLabel, localizeText } from '../../shared/i18n';
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
  useLanguage();
  const registry = useApiRead('/api/v1/workers', workers);
  const catalog = useApiRead('/api/v1/projects', projects);
  const activity = useApiRead('/api/v1/executions?limit=50&offset=0', executions);
  const readiness = useServerReadiness();
  const [filter, setFilter] = useState('All');
  const current = activity.data?.filter(item => ['Assigned', 'Running'].includes(item.state)) ?? [];
  const completed = completedExecutions(activity.data ?? [], filter);
  const linkClass = 'text-brand-secondary hover:underline break-words';
  function issue(item: ExecutionSummary) {
    const project = catalog.data?.find(project => project.id === item.projectId);
    const href = issueLink(item.workReference, project?.repository);
    const label = item.workReference ? `${item.workReference.type === 'github-issue' ? t("home.issue") : `${item.workReference.type} `}${item.workReference.id}` : t("home.issueUnavailable");
    return <span>{href ? <ExternalLink href={href}>{label}</ExternalLink> : label}<span className="text-tertiary">{' '}{t("home.titleUnavailable")}</span></span>;
  }
  function executionRow(item: ExecutionSummary, active = false) {
    const state = presentation(item);
    return <li key={item.id} className="grid gap-2 border-b border-secondary px-3 py-3 last:border-0 sm:grid-cols-[minmax(9rem,1.2fr)_minmax(7rem,1fr)_minmax(7rem,1fr)_auto] sm:items-center">
      <div className="min-w-0">{issue(item)}{active && <span className="block text-xs text-tertiary">{t("home.stage")}{' '}{localizeText(item.currentStage ?? t("home.unavailable"))}</span>}</div>
      <Link className={linkClass} to={`/projects/${encodeURIComponent(item.projectId)}`}>{catalog.data?.find(p => p.id === item.projectId)?.name ?? t("home.projectNameUnavailable")}</Link>
      <div className="flex items-center gap-2">{item.assignedWorkerId ? <Link className={linkClass} to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{registry.data?.find(w => w.workerId === item.assignedWorkerId)?.displayName ?? t("home.workerNameUnavailable")}</Link> : <span>{t("home.workerUnassigned")}</span>}</div>
      <div className="flex flex-wrap items-center gap-2"><StatusBadge tone={state.tone}>{state.text}</StatusBadge><Link className="text-sm text-tertiary hover:text-primary" to={`/executions/${encodeURIComponent(item.id)}`} aria-label={t("home.executionDetails")}>{active ? duration(item, Date.now()) : timestamp(item.completedAtUtc)}</Link></div>
    </li>;
  }
  function readState(read: { data?: unknown[]; loading: boolean; error?: string; stale: boolean; updatedAt: number }, empty: string) {
    const message = sectionMessage(read.data, read.loading, read.error, empty);
    return <>{message && <Notice error={!!read.error}>{message}</Notice>}{read.data && read.error && <p className="mb-3 text-sm text-warning-primary">{t("home.retainedObservationsAreStaleObserved")}{' '}{timestamp(new Date(read.updatedAt).toISOString())}</p>}</>;
  }
  async function refresh() {
    await Promise.all([registry.refetch(), catalog.refetch(), activity.refetch(), readiness.inventory.refetch()]);
  }
  return <div className="space-y-5 text-secondary">
    <PageHeading title={t("home.home")} actions={<Button color="secondary" onPress={() => { void refresh(); }}>{t("home.refreshSystemState")}</Button>} />
    <div className="grid items-start gap-5 xl:grid-cols-[0.9fr_1.1fr]">
      <section className="rounded-xl border border-secondary bg-primary p-4" aria-labelledby="home-workers"><div className="mb-3 flex items-center justify-between"><h2 id="home-workers" className="text-lg font-semibold text-primary">{t("home.workers")}</h2><Button href="/workers" color="link-color">{t("home.allWorkers")}</Button></div><div className="space-y-3">{readState(registry, t("home.noWorkersRegisteredAddAWorkerToGetStarted"))}
        <div className="grid gap-2 sm:grid-cols-2">{registry.data?.map(worker => <Link key={worker.workerId} className="flex min-w-0 items-center justify-between gap-2 rounded-lg border border-secondary px-3 py-2 hover:bg-secondary" to={`/workers/${encodeURIComponent(worker.workerId)}`}>
          <span className="min-w-0"><span className="block truncate font-medium text-primary">{worker.displayName ?? t("home.workerNameUnavailable")}</span><span className="text-xs text-tertiary">{t("home.slotsOccupied")}: {worker.activeExecutions ?? t("home.unknown")} / {worker.maximumCapacity ?? worker.capacity ?? t("home.unknown")}</span></span><StatusBadge tone={statusColor(worker.availability)}>{localizeText(worker.availability)}</StatusBadge>
        </Link>)}</div>{readiness.inventory.error && <Notice error>{t("home.readinessUnavailableRefreshSystemStateToRetry")}</Notice>}</div></section>
      <section className="rounded-xl border border-secondary bg-primary p-4" aria-labelledby="home-projects"><div className="mb-3 flex items-center justify-between"><h2 id="home-projects" className="text-lg font-semibold text-primary">{t("home.projects")}</h2><Button href="/projects" color="link-color">{t("home.allProjects")}</Button></div><p className="mb-2 text-xs text-tertiary">{t("home.latestFiveByRecordedActivityInTheLatest50ExecutionRequests")}</p><div>{readState(catalog, t("home.noProjectsRegisteredCreateAProjectToGetStarted"))}
        <ul className="divide-y divide-secondary">{recentProjects(catalog.data ?? [], activity.data ?? []).map(({ project, latest }) => {
          const repoHref = repositoryLink(project.repository);
          return <li key={project.id} className="grid gap-1 py-2 sm:grid-cols-[minmax(5rem,1fr)_minmax(7rem,1.2fr)_auto_minmax(7rem,1.2fr)] sm:items-center">
          <Link className={`${linkClass} font-medium`} to={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</Link>
          {repoHref ? <ExternalLink href={repoHref}>{project.repository}</ExternalLink> : <span className="text-sm">{t("home.repositoryLinkUnavailable")}</span>}
          <StatusBadge tone={project.enabled === true ? 'success' : 'gray'}>{project.enabled == null ? t("home.statusUnavailable") : project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>
          {!activity.data ? <span className="text-sm text-tertiary">{t("home.executionActivityUnavailable")}</span> : latest ? <Link className="text-sm text-secondary hover:text-primary" to={`/executions/${encodeURIComponent(latest.id)}`}>{latest.workReference ? `${latest.workReference.type === 'github-issue' ? '#' : ''}${latest.workReference.id}` : t("home.issueUnavailable")} · {timestamp(latest.completedAtUtc ?? latest.startedAtUtc ?? latest.assignedAtUtc ?? latest.createdAtUtc)} · {localizeText(latest.state)}</Link> : <span className="text-sm text-tertiary">{t("home.noRecordedExecutionActivityInThisSnapshot")}</span>}
        </li>;
        })}</ul></div></section>
    </div>
    <section className="rounded-xl border border-secondary bg-primary p-4" aria-labelledby="home-current"><h2 id="home-current" className="text-lg font-semibold text-primary">{`${t('home.currentExecutions')}${activity.data ? t('home.activeCount', { count: current.length }) : ''}`}</h2><p className="mb-3 text-sm text-tertiary">{t("home.assignedAndRunningRequestsInTheLatest50RequestsOlderActiveWork")}</p><div>{readState(activity, t("home.noExecutionRequestsYet"))}{activity.data && !current.length && <p>{t("home.noActiveExecutionsInThisSnapshot")}</p>}<ul className="divide-y divide-secondary">{current.map(item => executionRow(item, true))}</ul><Button href="/executions" color="link-color">{t("home.allExecutions")}</Button></div></section>
    <section className="rounded-xl border border-secondary bg-primary p-4" aria-labelledby="home-completed"><div className="flex flex-wrap items-start justify-between gap-3"><div><h2 id="home-completed" className="text-lg font-semibold text-primary">{t("home.recentCompletedExecutions")}</h2><p className="mb-3 text-sm text-tertiary">{t("home.terminalOutcomesInTheLatest50Requests")}</p></div><label className="text-sm">{t("home.status")}<select className="ml-3 rounded-lg border border-secondary bg-primary p-2" value={filter} onChange={event => setFilter(event.target.value)}>{['All', ...completedStatuses].map(state => <option key={state} value={state}>{statusLabel(state)}</option>)}</select></label></div><div>
      {activity.data && !completed.length && <p>{completedExecutions(activity.data).length ? t("home.noCompletedExecutionsMatchThisStatus") : t("home.noCompletedExecutionsInThisSnapshot")}</p>}<ul className="divide-y divide-secondary">{completed.map(item => executionRow(item))}</ul>
    </div></section>
  </div>;
}
