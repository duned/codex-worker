import { t, useLanguage, localizeText } from '../../shared/i18n';
import { DeleteAction, ExternalLink } from '../../shared/Actions';
import { useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { PageHeading, Notice, StatusBadge, ViewState, AdvancedDisclosure } from '../../shared/Presentation';
import { ConfirmationDialog } from '../../shared/Dialogs';
import { Button } from '../../untitled/components/base/buttons/button';
import { NumberInput } from '../../shared/NumberInput';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead } from '../../shared/api/session';
import { executions } from '../../shared/api/validation';
import { projectList, type Project } from './contracts';
import { definitionOf, newDefinition } from './model';
import { useProjects } from './Workspace';
import { ProjectEditor } from './ProjectEditor';
import { Issues, IssueEditor } from './Issues';
import { ProjectWorkers } from './Readiness';
import { timestamp } from '../../model';
export function ProjectsPage() {
  useLanguage();
  const { resourceId } = useParams(), location = useLocation(), w = useProjects(), read = useApiRead('/api/v1/projects', projectList);
  const [action, setAction] = useState<{ project: Project; enabled?: boolean }>();
  const [filter, setFilter] = useState('all'), [search, setSearch] = useState('');
  const activity = useApiRead('/api/v1/executions?limit=50&offset=0', executions);
  const selected = read.data?.find(p => p.id === resourceId);
  const open = (p?: Project) => {
    if (w.draft) { w.setDraft({ ...w.draft, open: true }); w.setMessage(t("projects.resumeOrDiscardTheRetainedDraftBeforeStartingAnotherDefinition")); return; }
    w.setConflict(undefined); w.setDraft({ before: p, definition: p ? definitionOf(p) : newDefinition(), step: 1, open: true });
  };
  const controls = (p: Project) => <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={w.locked(p.id)} onPress={() => open(p)}>{t("projects.editProject")}</Button><Button color="secondary" isDisabled={w.locked(p.id)} onPress={() => setAction({ project: p, enabled: !p.enabled })}>{p.enabled ? t('projects.disable') : t('projects.enable')}</Button><DeleteAction isDisabled={w.locked(p.id)} onPress={() => setAction({ project: p })}>{t("projects.delete")}</DeleteAction></div>;
  return <>
    <PageHeading title={resourceId ? selected?.name ?? t("projects.project") : t('home.projects')} resourceId={resourceId} breadcrumbs={resourceId ? [{ label: t("projects.projects"), href: `/projects${location.search}` }, { label: selected?.name ?? t("projects.project") }] : []} actions={!resourceId && <Button isDisabled={w.locked()} onPress={() => open()}>{t("projects.createProject")}</Button>} />
    <div className="space-y-6">
      {w.message && <Notice>{w.message}</Notice>}
      {w.draft && !w.draft.open && <Button color="secondary" onPress={() => w.setDraft(d => d ? { ...d, open: true } : d)}>{t("projects.resumeProjectDraft")}</Button>}
      {w.issueDraft && !w.issueDraft.open && <Button color="secondary" onPress={() => w.setIssueDraft(d => d ? { ...d, open: true } : d)}>{t("projects.resumeIssueDraft")}</Button>}
      {Object.keys(w.attempts).map(key => <Reconciliation key={key} resource={key} />)}
      {!read.data ? <ViewState title={read.error ? t("projects.projectConfigurationUnavailableRefreshProjects") : t("projects.loadingProjects")} error={!!read.error}><Button color="secondary" onPress={() => { void read.refetch(); }}>{t("projects.refreshProjects")}</Button></ViewState> : resourceId ? selected ? <>
        <Link to={`/projects${location.search}`} className="text-sm text-brand-secondary">{t("projects.backToProjects")}</Link>
        <div className="grid gap-5 xl:grid-cols-[minmax(0,1.55fr)_minmax(20rem,1fr)]"><div className="space-y-5"><TableCard.Root><div className="space-y-4 p-5"><h2 className="text-lg font-semibold text-primary">{t("projects.projectDefinition")}</h2><p className="break-words text-secondary">{selected.repository} · {selected.defaultBranch}</p><p className="text-sm text-tertiary">{selected.description || t("projects.noDescription")}{' '}{t("projects.revision2")}{' '}{selected.revision}</p><StatusBadge tone={selected.enabled ? 'success' : 'gray'}>{selected.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>{controls(selected)}</div></TableCard.Root><ProjectRecentActivity project={selected} /></div>
          <div className="space-y-5"><TableCard.Root><div className="space-y-3 p-5"><h2 className="text-lg font-semibold text-primary">{t("projects.executionPolicy")}</h2><p className="text-sm text-secondary">{t("projects.executionRequiresAnEligibleAuthorizedWorkerCurrentManagedConfigurationAndAvailableCapacity")}</p><a className="text-sm text-brand-secondary" href={`/executions?project=${encodeURIComponent(selected.id)}`}>{t("projects.projectFilteredExecutions")}</a><AdvancedDisclosure title={t("projects.advancedProjectRequirements")}>{selected.requirements.length ? selected.requirements.map((r, i) => <p key={i}>{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? t('projects.scope', { scope: r.scope }) : ''}</p>) : <p>{t("projects.noAdditionalRequirements")}</p>}</AdvancedDisclosure></div></TableCard.Root><DiscoveryPolicy project={selected} /></div></div>
        <ProjectWorkers project={selected} /><Issues key={selected.id} project={selected} />
      </> : <ViewState title={t("projects.projectUnavailableOrDeletedReturnToProjectsToInspectCurrentDefinitions")} error /> : read.data.length ? <>
        <TableCard.Root><div className="flex flex-wrap items-end gap-3 p-4" aria-label={t("projects.projects")}>
          <label className="flex min-w-56 flex-1 flex-col gap-1 text-sm text-secondary">{t("projects.searchProjects")}<input className="rounded-lg border border-secondary bg-primary px-3 py-2 text-primary" value={search} onChange={event => setSearch(event.target.value)} /></label>
          <label className="flex flex-col gap-1 text-sm text-secondary">{t("projects.filterStatus")}<select className="rounded-lg border border-secondary bg-primary px-3 py-2 text-primary" value={filter} onChange={event => setFilter(event.target.value)}><option value="all">{t("projects.allProjects")}</option><option value="enabled">{localizeText('Enabled')}</option><option value="disabled">{localizeText('Disabled')}</option></select></label>
        </div></TableCard.Root>
        {(() => { const visible = read.data.filter(p => (filter === 'all' || (filter === 'enabled') === p.enabled) && `${p.name} ${p.repository}`.toLowerCase().includes(search.trim().toLowerCase())); return visible.length ? <TableCard.Root><div className="overflow-x-auto"><table className="w-full min-w-[1050px] table-fixed text-left"><colgroup><col className="w-[24%]"/><col className="w-[20%]"/><col className="w-[16%]"/><col className="w-[18%]"/><col className="w-[22%]"/></colgroup><thead className="bg-secondary text-xs uppercase tracking-wide text-tertiary"><tr>{(['projects.project', 'projects.repository', 'projects.filterStatus', 'projects.automaticDiscovery2', 'projects.recentActivity'] as const).map(label => <th key={label} className="px-5 py-4 font-semibold">{t(label)}</th>)}</tr></thead><tbody className="divide-y divide-secondary">{visible.map(p => {
          const repoHref = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(p.repository) ? `https://github.com/${p.repository}` : undefined;
          const discovery = p.automaticDiscovery?.enabled ? p.enabled ? t("projects.discoveryEnabled") : t("projects.discoveryPaused") : t("projects.discoveryDisabled");
          const latest = activity.data?.find(item => item.projectId === p.id);
          return <tr key={p.id} className="align-top"><td className="px-5 py-5"><Link to={`/projects/${encodeURIComponent(p.id)}${location.search}`} className="font-medium text-primary hover:text-brand-secondary">{p.name}</Link><p className="mt-1 line-clamp-2 text-xs text-tertiary">{p.description || t("projects.noDescription")}</p></td><td className="px-5 py-5 text-sm text-secondary">{repoHref ? <ExternalLink href={repoHref}>{p.repository}</ExternalLink> : p.repository}<p className="mt-1 text-xs text-tertiary">{p.defaultBranch}</p></td><td className="px-5 py-5"><div className="flex flex-wrap gap-2"><StatusBadge tone={p.enabled ? 'success' : 'gray'} compact>{p.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge><StatusBadge tone={p.automaticDiscovery?.enabled ? 'success' : 'gray'} compact>{discovery}</StatusBadge></div></td><td className="px-5 py-5 text-sm text-secondary">{p.automaticDiscovery?.enabled ? t('projects.automaticDiscovery2') : t('projects.discoveryDisabled')}<p className="mt-1 text-xs text-tertiary">{p.automaticDiscovery?.enabled ? `${p.automaticDiscovery.intervalSeconds} ${t('projects.seconds')}` : t('projects.onDemand')}</p></td><td className="px-5 py-5 text-sm text-secondary">{activity.error ? t("projects.recentActivityUnavailable") : latest ? <><Link className="font-medium text-primary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(latest.id)}`}>{latest.workReference ? `#${latest.workReference.id}` : latest.id}</Link><p className="mt-1 text-xs text-tertiary">{timestamp(latest.createdAtUtc)}</p><div className="mt-2"><StatusBadge compact tone={latest.state === 'Completed' ? 'success' : latest.state === 'Failed' ? 'error' : 'info'}>{latest.state}</StatusBadge></div></> : t("projects.noRecentActivity")}</td></tr>;
        })}</tbody></table></div></TableCard.Root> : <ViewState title={t("projects.noProjectsMatchFilters")} />; })()}
      </> : <ViewState title={t("projects.noCentralProjectsRegistered")}>{t("projects.createAProjectThenAssociateAndPrepareAWorker")}</ViewState>}
    </div>
    {w.draft && <ProjectEditor key={w.draft.before?.id ?? 'new'} />}{w.issueDraft && <IssueEditor key={`${w.issueDraft.project.id}-${w.issueDraft.before?.number ?? 'new'}-${w.issueDraft.change.kind}`} />}
    {action && <ConfirmationDialog key={`${action.project.id}-${String(action.enabled)}`} isOpen title={action.enabled === undefined ? t("projects.deleteCentralProject") : action.enabled ? t("projects.enableProject") : t("projects.disableProject")} description={action.enabled === undefined ? t('projects.deleteDescription', { name: action.project.name }) : action.enabled ? t('projects.enableDescription', { name: action.project.name }) : t('projects.disableDescription', { name: action.project.name })} actionLabel={action.enabled === undefined ? t("projects.deleteProject") : action.enabled ? t("projects.enableProject2") : t("projects.disableProject2")} destructive={action.enabled !== true} onClose={() => setAction(undefined)} onSubmit={() => w.lifecycle(action.project, action.enabled)} />}
  </>;
}
function Reconciliation({ resource }: { resource: string }) {
  useLanguage();
  const w = useProjects(), attempt = w.attempts[resource], [number, setNumber] = useState(''), [busy, setBusy] = useState(false);
  if (!attempt) return null;
  const createIssue = attempt.kind === 'issue' && attempt.change.kind === 'create';
  return <Notice error><p>{t("projects.unconfirmed")}{' '}{localizeText(attempt.kind)} · {resource}{t("projects.pollingAndNavigationCannotReleaseThisLock")}</p>{createIssue && <NumberInput label={t("projects.createdIssueNumberInspectGitHub")} min={1} max={2147483647} value={number} onChange={setNumber} />}<Button color="secondary" isDisabled={busy} onPress={() => { setBusy(true); void w.reconcile(resource, Number(number)).finally(() => setBusy(false)); }}>{busy ? t("projects.checking") : attempt.kind === 'save' ? t("projects.checkSavedDefinition") : t("projects.reconcileAuthoritativeState")}</Button></Notice>;
}
function ProjectRecentActivity({ project }: { project: Project }) {
  useLanguage();
  const read = useApiRead(`/api/v1/executions?projectId=${encodeURIComponent(project.id)}&limit=50&offset=0`, executions);
  return <TableCard.Root><section aria-labelledby="project-recent-activity"><header className="flex flex-wrap items-center justify-between gap-3 border-b border-secondary px-5 py-4"><div><h2 id="project-recent-activity" className="text-lg font-semibold text-primary">{t("shared.recentExecutions")}</h2><p className="mt-1 text-sm text-tertiary">{t("projects.latestRecordedProjectActivity")}</p></div><Link className="text-sm text-brand-secondary" to={`/executions?project=${encodeURIComponent(project.id)}`}>{t("projects.projectFilteredExecutions")}</Link></header>{!read.data ? <div className="p-5"><ViewState title={read.error ? t("projects.recentActivityUnavailable") : t("projects.loadingCurrentQueueObservation")} error={!!read.error} /></div> : read.data.length === 0 ? <p className="p-5 text-sm text-tertiary">{t("projects.noRecentActivity")}</p> : <ul className="divide-y divide-secondary">{read.data.slice(0, 5).map(item => <li key={item.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-4"><div className="min-w-0"><Link className="font-medium text-primary hover:text-brand-secondary" to={`/executions/${encodeURIComponent(item.id)}`}>{item.workReference ? `${item.workReference.type === 'github-issue' ? '#' : `${item.workReference.type} `}${item.workReference.id}` : item.id}</Link><p className="mt-1 break-all text-xs text-tertiary">{item.id}</p></div><div className="flex flex-wrap items-center gap-3"><StatusBadge compact tone={item.state === 'Completed' ? 'success' : item.state === 'Failed' ? 'error' : item.state === 'Running' ? 'info' : 'gray'}>{item.state}</StatusBadge><time className="text-xs text-tertiary">{timestamp(item.createdAtUtc)}</time></div></li>)}</ul>}</section></TableCard.Root>;
}
function DiscoveryPolicy({ project }: { project: Project }) {
  useLanguage();
  const read = useApiRead(`/api/v1/executions?projectId=${encodeURIComponent(project.id)}&limit=50&offset=0`, executions), d = project.automaticDiscovery;
  return <section className="space-y-3"><h2 className="text-lg font-semibold text-primary">{t("projects.automaticIssueDiscovery")}</h2><StatusBadge tone={!project.enabled ? 'gray' : d?.enabled ? 'success' : 'gray'}>{!project.enabled ? t("projects.pausedProjectDisabled") : d?.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>
    <p className="text-sm text-secondary">{t("projects.interval2")}{' '}{d?.intervalSeconds ?? 300}{' '}{t("projects.secondsMaximumPageSize")}{' '}{d?.pageSize ?? 25}{' '}{t("projects.cycleDeadline")}{' '}{d?.deadlineSeconds ?? 120}{' '}{t("projects.seconds")}</p>
    <p className="text-sm text-secondary">{t("projects.readyLabel")}{' '}{project.issueReadyLabel ?? t("projects.notConfigured")}{' '}{t("projects.blockedLabel")}{' '}{project.issueBlockedLabel ?? t("projects.notConfigured")}{t("projects.openBlockedByDependenciesPreventEligibility")}</p>
    <p className="text-sm text-tertiary">{t("projects.discoveryCanQueueEligibleIssuesReadingOrListingAloneDoesNotEnqueue")}</p><AdvancedDisclosure title={t("projects.advancedDiscoveryObservations")}><p>{t("projects.oneBoundedPageIsReadPerDueIntervalNoLastCycleTimestamp")}</p>
    <Notice>{read.data ? t('projects.queueObservation', { total: read.data.length, queued: read.data.filter(e => e.state.toLowerCase() === 'queued').length }) : read.error ? t("projects.currentQueueObservationUnavailableInspectProjectExecutions") : t("projects.loadingCurrentQueueObservation")}</Notice></AdvancedDisclosure>
    <p className="text-sm text-tertiary">{t("projects.enableDiscoveryInEditProjectEnableLifecycleSeparatelyForPermissionFailuresCheck")}</p>
  </section>;
}
