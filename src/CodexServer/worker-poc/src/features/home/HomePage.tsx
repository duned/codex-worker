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
  const linkClass = 'text-brand-secondary underline break-words';
  function issue(item: ExecutionSummary) {
    const project = catalog.data?.find(project => project.id === item.projectId);
    const href = issueLink(item.workReference, project?.repository);
    const label = item.workReference ? `${item.workReference.type === 'github-issue' ? t("home.issue") : `${item.workReference.type} `}${item.workReference.id}` : t("home.issueUnavailable");
    return <span>{href ? <ExternalLink href={href}>{label}</ExternalLink> : label}<span className="text-tertiary">{' '}{t("home.titleUnavailable")}</span></span>;
  }
  function executionRow(item: ExecutionSummary, active = false) {
    const state = presentation(item);
    return <li key={item.id} className="space-y-2 border-b border-secondary py-4 last:border-0">
      <div className="flex flex-wrap items-center justify-between gap-3"><div className="min-w-0 break-words">{issue(item)}</div><StatusBadge tone={state.tone}>{state.text}</StatusBadge></div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm"><Link className={linkClass} to={`/projects/${encodeURIComponent(item.projectId)}`}>{catalog.data?.find(p => p.id === item.projectId)?.name ?? t("home.projectNameUnavailable")}</Link>
        {item.assignedWorkerId ? <Link className={linkClass} to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{registry.data?.find(w => w.workerId === item.assignedWorkerId)?.displayName ?? t("home.workerNameUnavailable")}</Link> : <span>{t("home.workerUnassigned")}</span>}
        <Link className={linkClass} to={`/executions/${encodeURIComponent(item.id)}`}>{t("home.executionDetails")}</Link></div>
      {active ? <p className="text-sm text-tertiary">{t("home.stage")}{' '}{localizeText(item.currentStage ?? t("home.unavailable"))}{' '}{t("home.elapsed")}{' '}{duration(item, Date.now())}</p> : <><p className="line-clamp-2 break-words text-sm">{item.completionSummary || t("home.summaryNotReported")}</p><p className="text-sm text-tertiary">{t("home.completed")}{' '}{timestamp(item.completedAtUtc)}</p></>}
    </li>;
  }
  function readState(read: { data?: unknown[]; loading: boolean; error?: string; stale: boolean; updatedAt: number }, empty: string) {
    const message = sectionMessage(read.data, read.loading, read.error, empty);
    return <>{message && <Notice error={!!read.error}>{message}</Notice>}{read.data && read.error && <p className="mb-3 text-sm text-warning-primary">{t("home.retainedObservationsAreStaleObserved")}{' '}{timestamp(new Date(read.updatedAt).toISOString())}</p>}</>;
  }
  async function refresh() {
    await Promise.all([registry.refetch(), catalog.refetch(), activity.refetch(), readiness.inventory.refetch()]);
  }
  return <div className="space-y-6 text-secondary">
    <PageHeading title={t("home.home")} actions={<Button color="secondary" onPress={() => { void refresh(); }}>{t("home.refreshSystemState")}</Button>} />
    <div className="grid items-start gap-6 lg:grid-cols-2">
      <section aria-labelledby="home-workers"><h2 id="home-workers" className="mb-4 text-lg font-semibold text-primary">{t("home.workers")}</h2><div className="space-y-4">{readState(registry, t("home.noWorkersRegisteredAddAWorkerToGetStarted"))}
        <div className="grid gap-3 sm:grid-cols-2">{registry.data?.map(worker => {
          const node = readiness.inventory.data?.find(n => n.kind === 'worker' && n.id === worker.workerId);
          return <div key={worker.workerId} className="min-w-0 space-y-2 rounded-xl border border-secondary bg-primary p-4"><Link className={`${linkClass} font-medium`} to={`/workers/${encodeURIComponent(worker.workerId)}`}>{worker.displayName ?? t("home.workerNameUnavailable")}</Link>
            <p><StatusBadge tone={statusColor(worker.availability)}>{localizeText(worker.availability)}</StatusBadge></p>
            <p className="text-sm">{worker.activeExecutions ?? t("home.unknown")} / {worker.maximumCapacity ?? worker.capacity ?? t("home.unknown")}{' '}{t("home.slotsOccupied")}</p>
            <p className="text-xs text-tertiary">{localizeText(worker.lifecycleState ?? t("home.lifecycleUnavailable"))}{' '}{t("home.scheduling")}{' '}{localizeText(worker.schedulingPolicy ?? 'unavailable')}</p>
            <p className="text-xs text-tertiary">{!node ? t("home.readinessUnavailable") : node.observationsStale ? t("home.readinessStale") : `Prerequisites: ${localizeText(node.executionReadiness)}`}</p>
          </div>;
        })}</div>{readiness.inventory.error && <Notice error>{t("home.readinessUnavailableRefreshSystemStateToRetry")}</Notice>}<Button href="/workers" color="link-color">{t("home.allWorkers")}</Button></div></section>
      <section aria-labelledby="home-projects"><h2 id="home-projects" className="text-lg font-semibold text-primary">{t("home.projects")}</h2><p className="mt-1 text-sm text-tertiary">{t("home.latestFiveByRecordedActivityInTheLatest50ExecutionRequests")}</p><div>{readState(catalog, t("home.noProjectsRegisteredCreateAProjectToGetStarted"))}
        <ul>{recentProjects(catalog.data ?? [], activity.data ?? []).map(({ project, latest }) => <li key={project.id} className="space-y-2 border-b border-secondary py-4 last:border-0">
          <div className="flex flex-wrap items-center justify-between gap-2"><Link className={`${linkClass} font-medium`} to={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</Link><StatusBadge tone={project.enabled === true ? 'success' : 'gray'}>{project.enabled == null ? t("home.statusUnavailable") : project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge></div>
          <p className="break-words text-sm">{repositoryLink(project.repository) ? <ExternalLink href={repositoryLink(project.repository)}>{project.repository}</ExternalLink> : t("home.repositoryLinkUnavailable")}</p>
          {!activity.data ? <p className="text-sm text-tertiary">{t("home.executionActivityUnavailable")}</p> : latest ? <div className="space-y-1 text-sm"><p>{issue(latest)}</p><Link className={linkClass} to={`/executions/${encodeURIComponent(latest.id)}`}>{localizeText(latest.state)} · {timestamp(latest.completedAtUtc ?? latest.startedAtUtc ?? latest.assignedAtUtc ?? latest.createdAtUtc)}</Link></div> : <p className="text-sm text-tertiary">{t("home.noRecordedExecutionActivityInThisSnapshot")}</p>}
        </li>)}</ul><Button href="/projects" color="link-color">{t("home.allProjects")}</Button></div></section>
    </div>
    <section className="border-t border-secondary pt-6" aria-labelledby="home-current"><h2 id="home-current" className="text-lg font-semibold text-primary">{`${t('home.currentExecutions')}${activity.data ? t('home.activeCount', { count: current.length }) : ''}`}</h2><p className="mb-3 text-sm text-tertiary">{t("home.assignedAndRunningRequestsInTheLatest50RequestsOlderActiveWork")}</p><div>{readState(activity, t("home.noExecutionRequestsYet"))}{activity.data && !current.length && <p>{t("home.noActiveExecutionsInThisSnapshot")}</p>}<ul>{current.map(item => executionRow(item, true))}</ul><Button href="/executions" color="link-color">{t("home.allExecutions")}</Button></div></section>
    <section className="border-t border-secondary pt-6" aria-labelledby="home-completed"><h2 id="home-completed" className="text-lg font-semibold text-primary">{t("home.recentCompletedExecutions")}</h2><p className="mb-3 text-sm text-tertiary">{t("home.terminalOutcomesInTheLatest50Requests")}</p><div className="space-y-4">
      <label className="block text-sm">{t("home.status")}<select className="ml-3 rounded-lg border border-secondary bg-primary p-2" value={filter} onChange={event => setFilter(event.target.value)}>{['All', ...completedStatuses].map(state => <option key={state} value={state}>{statusLabel(state)}</option>)}</select></label>
      {activity.data && !completed.length && <p>{completedExecutions(activity.data).length ? t("home.noCompletedExecutionsMatchThisStatus") : t("home.noCompletedExecutionsInThisSnapshot")}</p>}<ul>{completed.map(item => executionRow(item))}</ul>
    </div></section>
  </div>;
}
