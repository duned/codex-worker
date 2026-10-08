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
  const { resourceId } = useParams(), location = useLocation(), w = useProjects(), read = useApiRead('/api/v1/projects', projectList);
  const [action, setAction] = useState<{ project: Project; enabled?: boolean }>();
  const selected = read.data?.find(p => p.id === resourceId);
  const open = (p?: Project) => {
    if (w.draft) { w.setDraft({ ...w.draft, open: true }); w.setMessage('Resume or discard the retained draft before starting another definition.'); return; }
    w.setConflict(undefined); w.setDraft({ before: p, definition: p ? definitionOf(p) : newDefinition(), step: 1, open: true });
  };
  const controls = (p: Project) => <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={w.locked(p.id)} onPress={() => open(p)}>Edit project</Button><Button color="secondary" isDisabled={w.locked(p.id)} onPress={() => setAction({ project: p, enabled: !p.enabled })}>{p.enabled ? 'Disable' : 'Enable'}</Button><DeleteAction isDisabled={w.locked(p.id)} onPress={() => setAction({ project: p })}>Delete</DeleteAction></div>;
  return <>
    <PageHeading title={resourceId ? selected?.name ?? 'Project' : 'Projects'} resourceId={resourceId} breadcrumbs={resourceId ? [{ label: 'Projects', href: `/projects${location.search}` }, { label: selected?.name ?? 'Project' }] : []} actions={!resourceId && <Button isDisabled={w.locked()} onPress={() => open()}>Create project</Button>} />
    <div className="space-y-6">
      {w.message && <Notice>{w.message}</Notice>}
      {w.draft && !w.draft.open && <Button color="secondary" onPress={() => w.setDraft(d => d ? { ...d, open: true } : d)}>Resume project draft</Button>}
      {w.issueDraft && !w.issueDraft.open && <Button color="secondary" onPress={() => w.setIssueDraft(d => d ? { ...d, open: true } : d)}>Resume Issue draft</Button>}
      {Object.keys(w.attempts).map(key => <Reconciliation key={key} resource={key} />)}
      {!read.data ? <ViewState title={read.error ? 'Project configuration unavailable. Refresh Projects.' : 'Loading Projects…'} error={!!read.error}><Button color="secondary" onPress={() => { void read.refetch(); }}>Refresh Projects</Button></ViewState> : resourceId ? selected ? <>
        <Link to={`/projects${location.search}`} className="text-sm text-brand-secondary">Back to Projects</Link>
        <TableCard.Root><div className="space-y-4 p-5"><h2 className="text-lg font-semibold text-primary">Project definition</h2><p className="break-words text-secondary">{selected.repository} · {selected.defaultBranch}</p><p className="text-sm text-tertiary">{selected.description || 'No description.'} · Revision {selected.revision}</p><StatusBadge tone={selected.enabled ? 'success' : 'gray'}>{selected.enabled ? 'Enabled' : 'Disabled'}</StatusBadge>{controls(selected)}</div></TableCard.Root>
        <DiscoveryPolicy project={selected} />
        <section className="space-y-3"><h2 className="text-lg font-semibold text-primary">Execution policy</h2><p className="text-sm text-secondary">Execution requires an eligible authorized Worker, current managed configuration and available capacity. Project enablement and automatic Issue discovery are independent choices.</p><a className="text-sm text-brand-secondary" href={`/executions?project=${encodeURIComponent(selected.id)}`}>Project-filtered executions</a><AdvancedDisclosure title="Advanced · project requirements">{selected.requirements.length ? selected.requirements.map((r, i) => <p key={i}>{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? ` for ${r.scope}` : ''}</p>) : <p>No additional requirements.</p>}</AdvancedDisclosure></section>
        <ProjectWorkers project={selected} /><Issues key={selected.id} project={selected} />
      </> : <ViewState title="Project unavailable or deleted. Return to Projects to inspect current definitions." error /> : read.data.length ? <TableCard.Root><ul className="divide-y divide-secondary">{read.data.map(p => <li key={p.id} className="flex flex-wrap items-start justify-between gap-4 p-5"><div className="min-w-0 flex-1 space-y-2"><Link to={`/projects/${encodeURIComponent(p.id)}${location.search}`}><ResourceIdentity name={p.name} id={p.id} /></Link><p className="break-words text-sm text-secondary">{p.repository} · {p.defaultBranch}</p><StatusBadge tone={p.enabled ? 'success' : 'gray'}>{p.enabled ? 'Enabled' : 'Disabled'}</StatusBadge><p className="text-sm text-secondary">Automatic discovery {p.automaticDiscovery?.enabled ? p.enabled ? 'enabled' : 'paused by disabled lifecycle' : 'disabled'} · Execution requires current Worker readiness.</p>{!p.enabled && <p className="text-sm text-warning-primary">Enable this project to admit new work.</p>}<p className="text-sm text-tertiary">Ready label: {p.issueReadyLabel ?? 'none configured'} · Blocked label: {p.issueBlockedLabel ?? 'none configured'}. Inspect Issue eligibility and Worker preparation for actionable blockers.</p><ProjectBlockers project={p} /></div>{controls(p)}</li>)}</ul></TableCard.Root> : <ViewState title="No central projects registered.">Create a project, then associate and prepare a Worker.</ViewState>}
    </div>
    {w.draft && <ProjectEditor key={w.draft.before?.id ?? 'new'} />}{w.issueDraft && <IssueEditor key={`${w.issueDraft.project.id}-${w.issueDraft.before?.number ?? 'new'}-${w.issueDraft.change.kind}`} />}
    {action && <ConfirmationDialog key={`${action.project.id}-${String(action.enabled)}`} isOpen title={action.enabled === undefined ? 'Delete central project?' : action.enabled ? 'Enable project?' : 'Disable project?'} description={action.enabled === undefined ? `Delete “${action.project.name}”. Server in-use and revision checks apply. Execution history remains retained.` : action.enabled ? `Enable “${action.project.name}” for Server admission. Discovery follows its separate configured policy; execution requires current Worker readiness.` : `Disable “${action.project.name}” for new admission and pause discovery. Existing queued work, executions and leases are not cancelled.`} actionLabel={action.enabled === undefined ? 'Delete project' : action.enabled ? 'Enable project' : 'Disable project'} destructive={action.enabled !== true} onClose={() => setAction(undefined)} onSubmit={() => w.lifecycle(action.project, action.enabled)} />}
  </>;
}
function Reconciliation({ resource }: { resource: string }) {
  const w = useProjects(), attempt = w.attempts[resource], [number, setNumber] = useState(''), [busy, setBusy] = useState(false);
  if (!attempt) return null;
  const createIssue = attempt.kind === 'issue' && attempt.change.kind === 'create';
  return <Notice error><p>Unconfirmed {attempt.kind} · {resource}. Polling and navigation cannot release this lock.</p>{createIssue && <NumberInput label="Created Issue number (inspect GitHub)" min={1} max={2147483647} value={number} onChange={setNumber} />}<Button color="secondary" isDisabled={busy} onPress={() => { setBusy(true); void w.reconcile(resource, Number(number)).finally(() => setBusy(false)); }}>{busy ? 'Checking…' : attempt.kind === 'save' ? 'Check saved definition' : 'Reconcile authoritative state'}</Button></Notice>;
}
function DiscoveryPolicy({ project }: { project: Project }) {
  const read = useApiRead(`/api/v1/executions?projectId=${encodeURIComponent(project.id)}&limit=50&offset=0`, executions), d = project.automaticDiscovery;
  return <section className="space-y-3"><h2 className="text-lg font-semibold text-primary">Automatic Issue discovery</h2><StatusBadge tone={!project.enabled ? 'gray' : d?.enabled ? 'success' : 'gray'}>{!project.enabled ? 'Paused · project disabled' : d?.enabled ? 'Enabled' : 'Disabled'}</StatusBadge>
    <p className="text-sm text-secondary">Interval: {d?.intervalSeconds ?? 300} seconds · Maximum page size: {d?.pageSize ?? 25} · Cycle deadline: {d?.deadlineSeconds ?? 120} seconds.</p>
    <p className="text-sm text-secondary">Ready label: {project.issueReadyLabel ?? 'not configured'} · Blocked label: {project.issueBlockedLabel ?? 'not configured'}. Open blocked-by dependencies prevent eligibility.</p>
    <p className="text-sm text-tertiary">Discovery can queue eligible Issues; reading or listing alone does not enqueue work.</p><AdvancedDisclosure title="Advanced · discovery observations"><p>One bounded page is read per due interval. No last-cycle timestamp or candidate total is exposed. Eligible Issues may not yet have been discovered. Previously requested Issues are not automatically retried after completion, failure or cancellation. Turning discovery off does not cancel queued work or active leases.</p>
    <Notice>{read.data ? `Latest bounded observation: ${read.data.length} project requests; ${read.data.filter(e => e.state.toLowerCase() === 'queued').length} queued. This is not a discovery-cycle count.` : read.error ? 'Current queue observation unavailable. Inspect project executions.' : 'Loading current queue observation…'}</Notice></AdvancedDisclosure>
    <p className="text-sm text-tertiary">Enable discovery in Edit project; enable lifecycle separately. For permission failures, check Server GitHub connection in Settings and repository access below. Inspect configured labels and blocked-by relationships for ineligible Issues.</p>
  </section>;
}
