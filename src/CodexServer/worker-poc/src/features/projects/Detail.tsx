import { ChevronDown } from '@untitledui/icons';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { localizeText, statusLabel, t, useLanguage } from '../../shared/i18n';
import { GuidDisplay } from '../../shared/GuidDisplay';
import { ExternalLink } from '../../shared/Actions';
import { useApiRead } from '../../shared/api/session';
import { executions as executionList } from '../../shared/api/validation';
import type { ExecutionSummary, NodeSummary, WorkerObservation } from '../../shared/api/contracts';
import { timestamp, workerListSignals, statusColor, isActiveExecutionState } from '../../model.js';
import { Button } from '../../untitled/components/base/buttons/button';
import { Dropdown } from '../../untitled/components/base/dropdown/dropdown';
import { TableCard } from '../../untitled/components/application/table/table';
import { Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import { nodes, workers } from '../../shared/api/validation';
import type { Project } from './contracts';
import { IssueTitle, projectRepositoryUrl } from './IssueTitle';

type ProjectAction = (project: Project, enabled?: boolean) => void;

export function ProjectDetail({ project, onEdit, onAction, locked, listHref, message }: {
  project: Project;
  onEdit: () => void;
  onAction: ProjectAction;
  locked: boolean;
  listHref: string;
  message?: string;
}) {
  useLanguage();
  const inventory = useApiRead('/api/v1/workers', workers);
  const nodeRead = useApiRead('/api/v1/nodes', nodes);
  const activity = useApiRead(`/api/v1/executions?projectId=${encodeURIComponent(project.id)}&limit=50&offset=0`, executionList);
  const recent = activity.data ? [...activity.data].sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc)) : undefined;
  const readiness = projectReadiness(inventory.data, nodeRead.data, inventory.error, nodeRead.error);
  const repoUrl = projectRepositoryUrl(project);
  const discovery = project.automaticDiscovery?.enabled
    ? project.enabled ? t('projects.discoveryAutomaticEveryInterval', { interval: discoveryInterval(project.automaticDiscovery.intervalSeconds) }) : t('projects.discoveryPaused')
    : t('projects.discoveryDisabled');

  return <div className="min-w-0">
    <header className="mb-5 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
      <div className="min-w-0 flex-1">
        <nav aria-label={t('shared.breadcrumb')} className="mb-2 text-sm text-tertiary">
          <ol className="flex flex-wrap gap-2">
            <li><Link to={listHref}>{t('projects.projects')}</Link></li>
            <li aria-hidden="true">/</li>
            <li aria-current="page">{project.name}</li>
          </ol>
        </nav>
        <h1 tabIndex={-1} className="text-display-sm font-semibold text-primary">{project.name}</h1>
        <p className="mt-2 flex flex-wrap items-center gap-x-2 text-sm text-tertiary">
          {repoUrl ? <ExternalLink href={repoUrl}>{project.repository}</ExternalLink> : <span>{project.repository}</span>}
          <span aria-hidden="true">·</span><span>{project.description || t('projects.noDescription')}</span>
        </p>
      </div>
      <div className="flex flex-wrap items-center justify-end gap-2">
        <StatusBadge tone={project.enabled ? 'success' : 'gray'}>{project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>
        <StatusBadge tone={readiness.tone}>{readiness.label}</StatusBadge>
        <Button color="secondary" onPress={onEdit} isDisabled={locked}>{t('projects.editProject')}</Button>
        <Dropdown.Root>
          <Button color="secondary" iconTrailing={ChevronDown}>{t('projects.moreActions')}</Button>
          <Dropdown.Popover>
            <Dropdown.Menu onAction={key => {
              if (key === 'toggle') onAction(project, !project.enabled);
              if (key === 'delete') onAction(project);
            }}>
              <Dropdown.Item id="toggle" label={project.enabled ? t('projects.disable') : t('projects.enable')} selectionIndicator="none" isDisabled={locked} />
              <Dropdown.Item id="delete" label={t('projects.delete')} selectionIndicator="none" isDisabled={locked} />
            </Dropdown.Menu>
          </Dropdown.Popover>
        </Dropdown.Root>
      </div>
    </header>
    {message && <Notice>{message}</Notice>}

    <section aria-label={t('projects.projectSummary')} className="mb-5 rounded-xl bg-primary px-5 py-4 shadow-xs ring-1 ring-secondary">
      <div className="grid gap-y-4 xl:grid-cols-[0.8fr_1fr_1.15fr_1.55fr_auto] xl:items-center">
        <SummaryField label={t('projects.projectRevision')} value={`rev. ${project.revision}`} />
        <SummaryField label={t('projects.defaultBranch')} value={project.defaultBranch} separated />
        <SummaryField label={t('projects.issueLabels')} value={`${project.issueReadyLabel || t('projects.notConfigured')} · ${project.issueBlockedLabel || t('projects.notConfigured')}`} separated />
        <SummaryField label={t('projects.discovery')} value={discovery} separated />
        <Button className="justify-self-start xl:justify-self-end" color="link-color" size="sm" onPress={onEdit} isDisabled={locked}>{t('projects.editConfiguration')}</Button>
      </div>
    </section>

    <div className="grid gap-5 xl:grid-cols-[minmax(0,1.72fr)_minmax(22rem,1fr)]">
      <TableCard.Root>
        <section aria-labelledby="project-configuration" className="p-5 sm:p-6">
          <h2 id="project-configuration" className="mb-2 text-lg font-semibold text-primary">{t('projects.projectConfiguration')}</h2>
          <div className="divide-y divide-secondary">
            <DetailRow label={t('projects.repository')}><span className="break-all">{repoUrl ? <ExternalLink href={repoUrl}>{project.repository}</ExternalLink> : project.repository}</span></DetailRow>
            <DetailRow label={t('projects.defaultBranch')}>{project.defaultBranch}</DetailRow>
            <DetailRow label={t('projects.projectStatus')}><div className="flex flex-wrap gap-2"><StatusBadge tone={project.enabled ? 'success' : 'gray'}>{project.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge><StatusBadge tone={readiness.tone}>{readiness.label}</StatusBadge></div></DetailRow>
            <DetailRow label={`${t('projects.projectConcurrency')}:`}><span className="font-medium text-primary">{concurrencyLabel(project.maxParallelTasks ?? null)}</span><span className="mt-1 block text-xs text-tertiary">{t(project.maxParallelTasks == null ? 'projects.projectConcurrencyAutomaticDetail' : 'projects.projectConcurrencyExplicitDetail')}</span><span className="mt-1 block text-xs text-tertiary">{t('projects.projectConcurrencyFreshness')}</span></DetailRow>
            <DetailRow label={t('projects.automaticDiscovery2')}>{discovery}{project.automaticDiscovery?.enabled && <span className="block text-xs text-tertiary">{t('projects.discoveryPageAndDeadline', { pageSize: project.automaticDiscovery.pageSize, deadline: project.automaticDiscovery.deadlineSeconds })}</span>}</DetailRow>
            <DetailRow label={t('projects.requirements')}><div className="space-y-1">{project.requirements.length ? project.requirements.map((requirement, index) => <p key={`${requirement.type}-${requirement.name}-${index}`}>{requirement.type} · {requirement.name}{requirement.version ? ` ${requirement.version}` : ''}{requirement.scope ? ` · ${requirement.scope}` : ''}</p>) : <p>{t('projects.noAdditionalRequirements')}</p>}</div></DetailRow>
          </div>
        </section>
      </TableCard.Root>

      <TableCard.Root>
        <section aria-labelledby="worker-availability" className="flex h-full flex-col p-5 sm:p-6">
          <h2 id="worker-availability" className="mb-4 text-lg font-semibold text-primary">{t('projects.workerAvailability')}</h2>
          {!inventory.data ? <ViewState title={inventory.error ? t('projects.workerInventoryUnavailableRefreshWorkersToInspectPreparation') : t('projects.loadingWorkers')} error={!!inventory.error} />
            : !inventory.data.length ? <p className="text-sm text-tertiary">{t('projects.noWorkerExistsYet')}</p>
              : <div className="space-y-4">{inventory.data.map(worker => <WorkerAvailability key={worker.workerId} worker={worker} project={project} node={nodeRead.data?.find(node => node.id === worker.workerId && node.kind === 'worker')} />)}</div>}
          {nodeRead.error && <Notice error>{t('projects.readinessUnavailableInspectWorkerDiagnostics')}</Notice>}
          <div className="mt-auto pt-5">
            <div className="mb-4 border-t border-secondary" />
            <h3 className="mb-3 text-sm font-semibold text-primary">{t('projects.latestExecution')}</h3>
            {!activity.data ? <p className="text-sm text-tertiary">{activity.error ? t('projects.recentActivityUnavailable') : t('projects.loadingCurrentQueueObservation')}</p>
              : recent?.length ? <LatestExecution item={recent[0]} project={project} />
                : <p className="text-sm text-tertiary">{t('projects.noRecentActivity')}</p>}
          </div>
        </section>
      </TableCard.Root>
    </div>

    <ProjectRecentActivity project={project} read={activity} workers={inventory.data ?? []} recent={recent} />
  </div>;
}

function SummaryField({ label, value, separated = false }: { label: string; value: string; separated?: boolean }) {
  return <div className={`min-w-0 ${separated ? 'xl:border-l xl:border-secondary xl:pl-5' : ''}`}>
    <p className="text-xs text-tertiary">{label}</p>
    <p className="mt-1 break-words text-sm font-semibold text-primary">{value}</p>
  </div>;
}

function DetailRow({ label, children }: { label: string; children: ReactNode }) {
  return <div className="grid gap-1 py-3 sm:grid-cols-[12.5rem_minmax(0,1fr)] sm:gap-3">
    <span className="text-sm text-tertiary">{label}</span>
    <span className="min-w-0 break-words text-sm text-secondary">{children}</span>
  </div>;
}

function projectReadiness(workersData: WorkerObservation[] | undefined, nodesData: NodeSummary[] | undefined, workerError?: string, nodeError?: string) {
  if (workerError || nodeError || !workersData || !nodesData) return { label: t('shared.notReported'), tone: 'gray' as const };
  if (!workersData.length) return { label: t('status.not-ready'), tone: 'warning' as const };
  const observed = workersData.map(worker => ({ worker, node: nodesData.find(node => node.id === worker.workerId && node.kind === 'worker') }));
  if (observed.some(({ worker, node }) => worker.availability === 'online' && node?.observationsStale === false && node.executionReadiness.toLowerCase() === 'ready')) {
    return { label: statusLabel('Ready'), tone: 'success' as const };
  }
  if (observed.some(({ node }) => node?.observationsStale)) return { label: statusLabel('Stale'), tone: 'warning' as const };
  if (observed.every(({ node }) => node)) return { label: statusLabel('not-ready'), tone: 'warning' as const };
  return { label: t('shared.notReported'), tone: 'gray' as const };
}

function WorkerAvailability({ worker, project, node }: { worker: WorkerObservation; project: Project; node?: NodeSummary }) {
  useLanguage();
  const signals = workerListSignals(worker, node);
  const total = signals.totalSlots, occupied = signals.occupiedSlots;
  const capacityKnown = Number.isFinite(total) && Number(total) > 0 && Number.isFinite(occupied) && Number(occupied) >= 0;
  const percentage = capacityKnown ? Math.min(100, Math.round((Number(occupied) / Number(total)) * 100)) : undefined;
  const readiness = node ? node.observationsStale ? statusLabel('Stale') : localizeText(node.executionReadiness) : t('shared.notReported');
  const readinessTone = node?.observationsStale ? 'warning' : node ? toneOf(node.executionReadiness) : 'gray';
  return <article className="space-y-3 border-b border-secondary pb-4 last:border-0 last:pb-0">
    <Link to={`/workers/${encodeURIComponent(worker.workerId)}?step=preparation&project=${encodeURIComponent(project.id)}`} className="font-semibold text-primary hover:text-brand-secondary">{worker.displayName ?? worker.workerId}</Link>
    <div className="flex flex-wrap items-center gap-2 text-sm text-secondary">
      <span aria-hidden="true" className={`size-2 rounded-full ${toneDot(worker.availability)}`} />
      <span>{t('projects.connection')} · {localizeText(worker.availability)}</span>
      <StatusBadge compact tone={readinessTone}>{t('projects.executionReadiness')} · {readiness}</StatusBadge>
    </div>
    <div>
      <div className="mb-2 flex items-center justify-between gap-3 text-xs">
        <span className="text-tertiary">{t('workers.slots')}</span>
        <span className="font-semibold text-primary">{capacityKnown ? `${occupied} / ${total} ${t('projects.slots')}` : t('shared.notReported')}</span>
      </div>
      {percentage !== undefined && <div role="progressbar" aria-label={t('projects.workerCapacity')} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentage} className="h-1.5 overflow-hidden rounded-full bg-secondary"><div className="h-full rounded-full bg-brand-solid" style={{ width: `${percentage}%` }} /></div>}
    </div>
  </article>;
}

function LatestExecution({ item, project }: { item: ExecutionSummary; project: Project }) {
  return <div>
    <div className="flex flex-wrap items-start justify-between gap-2">
      <div className="min-w-0 font-semibold text-primary"><IssueTitle projectId={project.id} repository={project.repository} workReference={item.workReference} className="hover:text-brand-secondary" /></div>
      <StatusBadge tone={toneOf(item.state)} active={isActiveExecutionState(item.state)}>{statusLabel(item.state)}</StatusBadge>
    </div>
    <time className="mt-2 block text-xs text-tertiary" dateTime={item.createdAtUtc}>{timestamp(item.createdAtUtc)}</time>
    <Link className="mt-3 inline-block text-sm text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}`}>{t('projects.viewExecution')}</Link>
  </div>;
}

function ProjectRecentActivity({ project, read, workers: workerList, recent }: {
  project: Project;
  read: Pick<ReturnType<typeof useApiRead<ExecutionSummary[]>>, 'data' | 'error' | 'refetch'>;
  workers: WorkerObservation[];
  recent?: ExecutionSummary[];
}) {
  useLanguage();
  const rows = recent?.slice(0, 5) ?? [];
  return <section aria-labelledby="project-recent-activity" className="mt-8">
    <header className="mb-4 flex flex-wrap items-baseline gap-x-3 gap-y-2">
      <h2 id="project-recent-activity" className="text-lg font-semibold text-primary">{t('shared.recentExecutions')}</h2>
      <p className="text-sm text-tertiary">{t('projects.latestRecordedProjectActivity')}</p>
      <Link className="ml-auto text-sm text-brand-secondary" to={`/executions?project=${encodeURIComponent(project.id)}`}>{t('projects.viewAll')}</Link>
    </header>
    <TableCard.Root>
      {!read.data ? <div className="p-5"><ViewState title={read.error ? t('projects.recentActivityUnavailable') : t('projects.loadingCurrentQueueObservation')} error={!!read.error}><Button color="secondary" onPress={() => { void read.refetch(); }}>{t('projects.refreshRecentExecutions')}</Button></ViewState></div>
        : !rows.length ? <p className="p-5 text-sm text-tertiary">{t('projects.noRecentActivity')}</p>
          : <div className="overflow-x-auto"><table className="w-full min-w-[760px] table-fixed text-left">
            <colgroup><col className="w-[47%]"/><col className="w-[14%]"/><col className="w-[19%]"/><col className="w-[20%]"/></colgroup>
            <thead className="bg-secondary text-[11px] font-semibold uppercase tracking-wide text-tertiary"><tr>
              <th scope="col" className="px-5 py-4">{t('projects.issueExecution')}</th><th scope="col" className="px-5 py-4">{t('executions.state')}</th><th scope="col" className="px-5 py-4">{t('workers.worker')}</th><th scope="col" className="px-5 py-4">{t('executions.created')}</th>
            </tr></thead>
            <tbody className="divide-y divide-secondary">{rows.map(item => {
              const worker = workerList.find(value => value.workerId === item.assignedWorkerId);
              return <tr key={item.id} className="align-middle">
                <td className="px-5 py-4"><p className="font-semibold text-primary"><IssueTitle projectId={project.id} repository={project.repository} workReference={item.workReference} className="hover:text-brand-secondary" /></p><Link className="mt-1 block break-all text-xs text-tertiary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}`}><GuidDisplay value={item.id} /></Link></td>
                <td className="px-5 py-4"><StatusBadge tone={toneOf(item.state)} active={isActiveExecutionState(item.state)}>{statusLabel(item.state)}</StatusBadge></td>
                <td className="px-5 py-4 text-sm text-secondary">{item.assignedWorkerId ? <Link to={`/workers/${encodeURIComponent(item.assignedWorkerId)}`} className="hover:text-brand-secondary">{worker?.displayName ?? item.assignedWorkerId}</Link> : t('executions.workerUnassigned')}</td>
                <td className="px-5 py-4 text-sm text-secondary"><time dateTime={item.createdAtUtc}>{timestamp(item.createdAtUtc)}</time></td>
              </tr>;
            })}</tbody>
            <tfoot><tr><td colSpan={4} className="border-t border-secondary px-5 py-3"><div className="flex flex-wrap items-center justify-between gap-3 text-xs text-tertiary"><span>{t('projects.showingRecentExecutions', { count: rows.length })}</span><Link className="text-sm text-brand-secondary" to={`/executions?project=${encodeURIComponent(project.id)}`}>{t('projects.openExecutions')}</Link></div></td></tr></tfoot>
          </table></div>}
    </TableCard.Root>
  </section>;
}

function discoveryInterval(seconds: number) {
  return seconds % 60 === 0 ? t('projects.intervalMinutes', { count: seconds / 60 }) : t('projects.intervalShortSeconds', { count: seconds });
}

function concurrencyLabel(limit: number | null) {
  return limit === null ? t('projects.projectConcurrencyAutomatic') : t(limit === 1 ? 'projects.projectConcurrencyOneExecution' : 'projects.projectConcurrencyExecutions', { count: limit });
}

function toneOf(value: string) {
  const tone = statusColor(value);
  return tone === 'success' || tone === 'warning' || tone === 'error' || tone === 'info' ? tone : 'gray';
}

function toneDot(value: string) {
  const tone = toneOf(value);
  return tone === 'success' ? 'bg-success-solid' : tone === 'warning' ? 'bg-warning-solid' : tone === 'error' ? 'bg-error-solid' : tone === 'info' ? 'bg-utility-blue-500' : 'bg-secondary-solid';
}
