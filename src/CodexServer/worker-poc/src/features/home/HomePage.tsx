import { t, useLanguage, statusLabel, localizeText } from '../../shared/i18n';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useApiRead } from '../../shared/api/session';
import { workers, projects, executions } from '../../shared/api/validation';
import { useServerReadiness } from '../server/useServerReadiness';
import { completedExecutions, completedStatuses, recentProjects, repositoryLink, sectionMessage } from './model';
import { Notice, StatusBadge } from '../../shared/Presentation';
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
  const today = new Date().toISOString().slice(0, 10);
  const completedToday = activity.data?.filter(item => item.completedAtUtc?.slice(0, 10) === today) ?? [];
  const running = activity.data?.filter(item => item.state === 'Running') ?? [];
  const metrics = [
    { label: t('home.activeWorkers'), value: registry.data?.filter(worker => worker.availability === 'online').length, detail: registry.data ? `${registry.data.length} ${t('home.totalWorkers')}` : undefined },
    { label: t('home.projects'), value: catalog.data?.length, detail: undefined },
    { label: t('home.runningExecutions'), value: activity.data ? running.length : undefined, detail: t('home.latest50Requests') },
    { label: t('home.completedToday'), value: activity.data ? completedToday.length : undefined, detail: activity.data ? t('home.latest50Requests') : undefined }
  ];
  const linkClass = 'text-brand-secondary hover:underline';
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
      <div className="flex flex-wrap items-center gap-2"><StatusBadge compact tone={state.tone}>{state.text}</StatusBadge><Link className="text-sm text-tertiary hover:text-primary" to={`/executions/${encodeURIComponent(item.id)}`} aria-label={t("home.executionDetails")}>{active ? duration(item, Date.now()) : timestamp(item.completedAtUtc)}</Link></div>
    </li>;
  }
  function readState(read: { data?: unknown[]; loading: boolean; error?: string; stale: boolean; updatedAt: number }, empty: string) {
    const message = sectionMessage(read.data, read.loading, read.error, empty);
    return <>{message && <Notice error={!!read.error}>{message}</Notice>}{read.data && read.error && <p className="mb-3 text-sm text-warning-primary">{t("home.retainedObservationsAreStaleObserved")}{' '}{timestamp(new Date(read.updatedAt).toISOString())}</p>}</>;
  }
  return <div className="space-y-5 text-secondary">
    <header className="mb-1"><h1 className="text-display-sm font-semibold text-primary">{t('home.greeting')}</h1><p className="mt-2 text-sm text-tertiary">{t('home.greetingSupport')}</p></header>
    <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">{metrics.map(metric => <section key={metric.label} className="rounded-xl border border-secondary bg-primary p-4" aria-label={metric.label}><p className="text-sm text-tertiary">{metric.label}</p><p className="mt-2 text-3xl font-semibold text-primary">{metric.value ?? t('home.unavailable')}</p>{metric.detail && <p className="mt-1 text-xs text-tertiary">{metric.detail}</p>}</section>)}</div>
    <div className="grid items-start gap-5 xl:grid-cols-[260px_minmax(0,1fr)]">
      <section aria-labelledby="home-workers"><div className="mb-3 flex items-center justify-between gap-2"><h2 id="home-workers" className="text-lg font-semibold text-primary">{t("home.workers")}</h2><Button href="/workers" color="link-color">{t("home.allWorkers")}</Button></div>{registry.data && <p className="mb-2 text-xs text-tertiary">{registry.data.length} {t('home.totalWorkers')}</p>}{readState(registry, t("home.noWorkersRegisteredAddAWorkerToGetStarted"))}
        <div className="grid gap-3">{registry.data?.map(worker => {
          const capacity = worker.maximumCapacity ?? worker.capacity;
          const occupied = worker.activeExecutions;
          const usage = capacity != null && capacity > 0 && occupied != null ? Math.min(100, Math.max(0, occupied / capacity * 100)) : undefined;
          const availabilityTone = worker.availability === 'online' ? 'success' : worker.availability === 'busy' ? 'warning' : worker.availability === 'idle' ? 'info' : statusColor(worker.availability);
          return <Link key={worker.workerId} className="block min-w-0 rounded-xl border border-secondary bg-primary p-3 hover:bg-secondary" to={`/workers/${encodeURIComponent(worker.workerId)}`}>
          <span className="flex min-w-0 items-center justify-between gap-2"><span className="truncate font-medium text-primary">{worker.displayName ?? t("home.workerNameUnavailable")}</span><StatusBadge compact tone={availabilityTone}>{localizeText(worker.availability)}</StatusBadge></span><span className="mt-3 block text-xs text-tertiary">{t("home.slotsOccupied")}: {occupied ?? t("home.unknown")} / {capacity ?? t("home.unknown")}</span><span className="mt-2 block h-1 overflow-hidden rounded-full bg-secondary" aria-hidden="true"><span className="block h-full rounded-full bg-brand-solid" style={{ width: `${usage ?? 0}%` }} /></span>
        </Link>;
        })}</div>{readiness.inventory.error && <Notice error>{t("home.readinessUnavailableRefreshSystemStateToRetry")}</Notice>}</section>
      <section aria-labelledby="home-projects"><div className="mb-3 flex items-center justify-between"><h2 id="home-projects" className="text-lg font-semibold text-primary">{t("home.recentlyActiveProjects")}</h2><Button href="/projects" color="link-color">{t("home.allProjects")}</Button></div><div className="overflow-x-auto rounded-xl border border-secondary bg-primary">{readState(catalog, t("home.noProjectsRegisteredCreateAProjectToGetStarted"))}
        <ul className="min-w-[720px] divide-y divide-secondary"><li className="grid grid-cols-[minmax(15rem,1fr)_7.5rem_minmax(18rem,1.25fr)] gap-4 px-5 py-3 text-[11px] font-bold uppercase tracking-wider text-quaternary"><span>{t('home.projectRepository')}</span><span>{t('home.status')}</span><span>{t('home.latestExecution')}</span></li>{recentProjects(catalog.data ?? [], activity.data ?? []).map(({ project, latest }) => {
          const repoHref = repositoryLink(project.repository);
          return <li key={project.id} className="grid grid-cols-[minmax(15rem,1fr)_7.5rem_minmax(18rem,1.25fr)] items-center gap-4 px-5 py-3">
          <div className="min-w-0"><Link className={`${linkClass} font-medium`} to={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</Link><span className="mt-1 block truncate text-xs text-tertiary">{repoHref ? <ExternalLink href={repoHref}>{project.repository}</ExternalLink> : t("home.repositoryLinkUnavailable")}</span></div>
          <StatusBadge compact tone={project.enabled === true ? 'success' : 'gray'}>{project.enabled == null ? t("home.statusUnavailable") : project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>
          {!activity.data ? <span className="text-sm text-tertiary">{t("home.executionActivityUnavailable")}</span> : latest ? <Link className="min-w-0 text-sm text-secondary hover:text-primary" to={`/executions/${encodeURIComponent(latest.id)}`}>{latest.workReference ? `${latest.workReference.type === 'github-issue' ? '#' : ''}${latest.workReference.id}` : t("home.issueUnavailable")} · {timestamp(latest.completedAtUtc ?? latest.startedAtUtc ?? latest.assignedAtUtc ?? latest.createdAtUtc)} · {localizeText(latest.state)}</Link> : <span className="text-sm text-tertiary">{t("home.noRecordedExecutionActivityInThisSnapshot")}</span>}
        </li>;
        })}</ul></div></section>
    </div>
    <section aria-labelledby="home-current"><h2 id="home-current" className="mb-3 text-lg font-semibold text-primary">{`${t('home.currentExecutions')}${activity.data ? t('home.activeCount', { count: current.length }) : ''}`}</h2><div className="overflow-x-auto rounded-xl border border-secondary bg-primary">{readState(activity, t("home.noExecutionRequestsYet"))}{activity.data && !current.length && <p className="px-5 py-4 text-sm text-tertiary">{t("home.noActiveExecutionsInThisSnapshot")}</p>}<ul className="min-w-[720px] divide-y divide-secondary">{current.map(item => executionRow(item, true))}</ul></div><Button className="mt-2" href="/executions" color="link-color">{t("home.allExecutions")}</Button></section>
    <section aria-labelledby="home-completed"><div className="mb-3 flex flex-wrap items-center justify-between gap-3"><h2 id="home-completed" className="text-lg font-semibold text-primary">{t("home.recentCompletedExecutions")}</h2><label className="text-sm">{t("home.status")}<select className="ml-3 rounded-lg border border-secondary bg-primary p-2" value={filter} onChange={event => setFilter(event.target.value)}>{['All', ...completedStatuses].map(state => <option key={state} value={state}>{statusLabel(state)}</option>)}</select></label></div><div className="overflow-x-auto rounded-xl border border-secondary bg-primary">
      {activity.data && !completed.length && <p className="px-5 py-4 text-sm text-tertiary">{completedExecutions(activity.data).length ? t("home.noCompletedExecutionsMatchThisStatus") : t("home.noCompletedExecutionsInThisSnapshot")}</p>}<ul className="min-w-[720px] divide-y divide-secondary">{completed.map(item => executionRow(item))}</ul>
    </div></section>
  </div>;
}
