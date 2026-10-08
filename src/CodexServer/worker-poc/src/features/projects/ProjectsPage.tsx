import { t, useLanguage, localizeText } from '../../shared/i18n';
import { DeleteAction } from '../../shared/Actions';
import { useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { PageHeading, Notice, StatusBadge, ResourceIdentity, ViewState, AdvancedDisclosure } from '../../shared/Presentation';
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
import { ProjectWorkers, ProjectBlockers } from './Readiness';
export function ProjectsPage() {
  useLanguage();
  const { resourceId } = useParams(), location = useLocation(), w = useProjects(), read = useApiRead('/api/v1/projects', projectList);
  const [action, setAction] = useState<{ project: Project; enabled?: boolean }>();
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
        <TableCard.Root><div className="space-y-4 p-5"><h2 className="text-lg font-semibold text-primary">{t("projects.projectDefinition")}</h2><p className="break-words text-secondary">{selected.repository} · {selected.defaultBranch}</p><p className="text-sm text-tertiary">{selected.description || t("projects.noDescription")}{' '}{t("projects.revision2")}{' '}{selected.revision}</p><StatusBadge tone={selected.enabled ? 'success' : 'gray'}>{selected.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge>{controls(selected)}</div></TableCard.Root>
        <DiscoveryPolicy project={selected} />
        <section className="space-y-3"><h2 className="text-lg font-semibold text-primary">{t("projects.executionPolicy")}</h2><p className="text-sm text-secondary">{t("projects.executionRequiresAnEligibleAuthorizedWorkerCurrentManagedConfigurationAndAvailableCapacity")}</p><a className="text-sm text-brand-secondary" href={`/executions?project=${encodeURIComponent(selected.id)}`}>{t("projects.projectFilteredExecutions")}</a><AdvancedDisclosure title={t("projects.advancedProjectRequirements")}>{selected.requirements.length ? selected.requirements.map((r, i) => <p key={i}>{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? t('projects.scope', { scope: r.scope }) : ''}</p>) : <p>{t("projects.noAdditionalRequirements")}</p>}</AdvancedDisclosure></section>
        <ProjectWorkers project={selected} /><Issues key={selected.id} project={selected} />
      </> : <ViewState title={t("projects.projectUnavailableOrDeletedReturnToProjectsToInspectCurrentDefinitions")} error /> : read.data.length ? <TableCard.Root><ul className="divide-y divide-secondary">{read.data.map(p => <li key={p.id} className="flex flex-wrap items-start justify-between gap-4 p-5"><div className="min-w-0 flex-1 space-y-2"><Link to={`/projects/${encodeURIComponent(p.id)}${location.search}`}><ResourceIdentity name={p.name} id={p.id} /></Link><p className="break-words text-sm text-secondary">{p.repository} · {p.defaultBranch}</p><StatusBadge tone={p.enabled ? 'success' : 'gray'}>{p.enabled ? localizeText('Enabled') : localizeText('Disabled')}</StatusBadge><p className="text-sm text-secondary">{t("projects.automaticDiscovery2")}{' '}{p.automaticDiscovery?.enabled ? p.enabled ? localizeText('enabled') : t("projects.pausedByDisabledLifecycle") : localizeText('disabled')}{' '}{t("projects.executionRequiresCurrentWorkerReadiness")}</p>{!p.enabled && <p className="text-sm text-warning-primary">{t("projects.enableThisProjectToAdmitNewWork")}</p>}<p className="text-sm text-tertiary">{t("projects.readyLabel")}{' '}{p.issueReadyLabel ?? t("projects.noneConfigured")}{' '}{t("projects.blockedLabel")}{' '}{p.issueBlockedLabel ?? t("projects.noneConfigured")}{t("projects.inspectIssueEligibilityAndWorkerPreparationForActionableBlockers")}</p><ProjectBlockers project={p} /></div>{controls(p)}</li>)}</ul></TableCard.Root> : <ViewState title={t("projects.noCentralProjectsRegistered")}>{t("projects.createAProjectThenAssociateAndPrepareAWorker")}</ViewState>}
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
