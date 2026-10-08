import { timestamp } from '../../model';
import { ExternalLink } from '../../shared/Actions';
import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams, useNavigate } from 'react-router-dom';
import { FormDialog, ConfirmationDialog } from '../../shared/Dialogs';
import { Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import { Input } from '../../untitled/components/base/input/input';
import { TextArea } from '../../shared/TextArea';
import { NumberInput } from '../../shared/NumberInput';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead, useRuntime } from '../../shared/api/session';
import { access, issue, issueList, issueResult, issueUrl, type Project } from './contracts';
import { issuePath, projectPath, changeRequest, type IssueChange } from './model';
import { useProjects } from './Workspace';
export function Issues({ project }: { project: Project }) {
  const [params, setParams] = useSearchParams(), w = useProjects();
  const [state, setState] = useState(params.get('issueState') ?? 'open'), [label, setLabel] = useState(params.get('label') ?? ''), [checkAccess, setCheckAccess] = useState(false);
  // Back/reload restore filters from the URL; unsent filter input is a transient form.
  useEffect(() => { setState(params.get('issueState') ?? 'open'); setLabel(params.get('label') ?? ''); }, [params]);
  const number = Number(params.get('issue'));
  const context = { state: ['open', 'closed', 'all'].includes(params.get('issueState') ?? '') ? params.get('issueState') ?? 'open' : 'open', limit: '50' };
  const query = new URLSearchParams(context); if (params.get('label')) query.set('label', params.get('label') ?? '');
  const open = (change: IssueChange, before?: import('./contracts').Issue) => {
    if (w.issueDraft) { w.setIssueDraft({ ...w.issueDraft, open: true }); w.setMessage('Resume or discard the retained Issue draft before starting another action.'); return; }
    w.setIssueDraft({ project, before, change, open: true });
  };
  return <section className="space-y-4">
    <h2 className="text-lg font-semibold text-primary">GitHub Issues</h2>
    <div className="flex flex-wrap gap-2"><Button color="secondary" onPress={() => setCheckAccess(true)}>Check repository access</Button><Button isDisabled={w.locked(project.id)} onPress={() => open({ kind: 'create', title: '', body: '' })}>Create Issue</Button></div>
    {checkAccess && <RepositoryAccess id={project.id} />}
    <form className="flex flex-wrap items-end gap-3" onSubmit={event => { event.preventDefault(); const next = new URLSearchParams(params); next.set('issueState', state); if (label.trim()) next.set('label', label.trim()); else next.delete('label'); next.set('issues', '1'); next.delete('issue'); setParams(next); }}>
      <label className="flex flex-col gap-2 text-sm text-secondary">Issue state<select value={state} onChange={event => setState(event.target.value)} className="rounded-lg border border-secondary bg-primary p-2"><option value="open">Open</option><option value="closed">Closed</option><option value="all">All</option></select></label>
      <Input label="Filter by label" value={label} maxLength={100} onChange={setLabel} />
      <Button type="submit" color="secondary">Load Issues</Button>
    </form>
    <p className="text-sm text-tertiary">At most 50 matching Issues. Listing does not enqueue work. No total or discovery-cycle history is exposed.</p>
    {params.get('issues') === '1' ? <IssueList project={project} query={query.toString()} open={open} /> : <ViewState title="Load Issues to query GitHub." />}
    {params.has('issue') && (Number.isSafeInteger(number) && number > 0 && number <= 2147483647 ? <IssueDetail project={project} number={number} open={open} /> : <Notice error>Invalid Issue number in URL.</Notice>)}
  </section>;
}
function RepositoryAccess({ id }: { id: string }) {
  const read = useApiRead(projectPath(id) + '/github/access', access);
  return read.data ? <Notice><StatusBadge tone={read.data.cliAuthenticated ? 'success' : 'error'}>Server GitHub authentication: {read.data.cliAuthenticated ? 'available' : 'unavailable'}</StatusBadge> <StatusBadge tone={read.data.repositoryReadable ? 'success' : 'error'}>Repository read: {read.data.repositoryReadable ? 'available' : 'unavailable'}</StatusBadge>. Checked {timestamp(read.data.checkedAtUtc)}. Worker checkout and push permission are separate.</Notice> : <ViewState title={read.error ? 'Access unavailable. Check Server GitHub connection and repository permission in Settings.' : 'Checking repository access…'} error={!!read.error} />;
}
function IssueList({ project, query, open }: { project: Project; query: string; open(change: IssueChange, before: import('./contracts').Issue): void }) {
  const read = useApiRead(issuePath(project.id) + '?' + query, issueList), [params] = useSearchParams(), w = useProjects();
  if (!read.data) return <ViewState title={read.error ? 'Issues unavailable. Check repository access and retry Load Issues.' : 'Loading Issues…'} error={!!read.error} />;
  if (!read.data.length) return <ViewState title="No Issues matched these filters." />;
  return <TableCard.Root><ul className="divide-y divide-secondary">{read.data.map(i => { const next = new URLSearchParams(params); next.set('issue', String(i.number)); return <li key={i.number} className="flex flex-wrap items-start justify-between gap-3 p-4"><div className="min-w-0 flex-1 basis-full sm:basis-auto"><Link to={`?${next}`} className="font-medium text-primary">#{i.number} · {i.title}</Link><p className="text-sm text-tertiary">{i.state} · Labels: {i.labels.join(', ') || 'none'}</p>{i.blockedBy.length > 0 && <p className="text-sm text-secondary">Blocked by {i.blockedBy.map(b => `#${b.number} (${b.state})`).join(', ')}</p>}{i.eligibilityReasons.map(r => <p key={r} className="text-sm text-warning-primary">{r}</p>)}</div><div className="flex flex-wrap gap-2"><StatusBadge tone={i.isEligible ? 'success' : 'warning'}>{i.isEligible ? 'Eligible' : 'Ineligible'}</StatusBadge>{i.isEligible && <Button size="sm" color="secondary" isDisabled={w.locked(project.id) || !project.enabled} onPress={() => open({ kind: 'enqueue' }, i)}>Enqueue #{i.number}</Button>}</div></li>; })}</ul></TableCard.Root>;
}
function IssueDetail({ project, number, open }: { project: Project; number: number; open(change: IssueChange, before: import('./contracts').Issue): void }) {
  const read = useApiRead(issuePath(project.id, number), value => { const observed = issue(value); if (observed.number !== number) throw Error('Unexpected Issue identity.'); return observed; }), w = useProjects(), [params, setParams] = useSearchParams();
  if (!read.data) return <ViewState title={read.error ? 'Issue unavailable or deleted. Check access and reload.' : 'Loading Issue…'} error={!!read.error} />;
  const i = read.data, disabled = w.locked(project.id);
  return <TableCard.Root><div className="space-y-4 p-5">
    <div className="flex flex-wrap items-start justify-between gap-2"><h3 className="break-words text-lg font-semibold text-primary"><ExternalLink href={issueUrl(project.repository, i)}>#{i.number} · {i.title}</ExternalLink></h3><Button color="secondary" onPress={() => { const next = new URLSearchParams(params); next.delete('issue'); setParams(next); }}>Close Issue detail</Button></div>
    <StatusBadge tone={i.isEligible ? 'success' : 'warning'}>{i.state} · {i.isEligible ? 'Eligible' : 'Ineligible'}</StatusBadge>
    {i.eligibilityReasons.map(r => <p key={r} className="text-sm text-secondary">{r}</p>)}
    <p className="text-sm text-tertiary">Labels: {i.labels.join(', ') || 'none'}</p>
    <h4 className="font-semibold text-primary">Configured eligibility labels</h4>
    <div className="flex flex-wrap gap-2">{[project.issueReadyLabel, project.issueBlockedLabel].filter((l): l is string => !!l).map(label => { const applied = i.labels.some(l => l.toLowerCase() === label.toLowerCase()); return <Button key={label} color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'label', label, applied: !applied }, i)}>{applied ? 'Remove' : 'Add'} {label}</Button>; })}</div>
    {!project.issueReadyLabel && !project.issueBlockedLabel && <p className="text-sm text-tertiary">No eligibility labels configured. Edit project policy to configure them.</p>}
    <h4 className="font-semibold text-primary">Native blocked-by relationships</h4>
    {i.blockedBy.length ? i.blockedBy.map(b => <div key={b.number} className="flex flex-wrap items-center gap-2"><ExternalLink href={issueUrl(project.repository, b)} className="text-sm text-secondary">#{b.number} · {b.title} · {b.state}</ExternalLink><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'dependency', blockerIssueNumber: b.number, applied: false }, i)}>Remove relationship #{b.number}</Button></div>) : <p className="text-sm text-tertiary">No blocking Issues.</p>}
    <Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'dependency', blockerIssueNumber: 0, applied: true }, i)}>Add blocked-by Issue</Button>
    <h4 className="font-semibold text-primary">Issue description</h4><pre className="whitespace-pre-wrap break-words text-sm text-secondary">{i.body || 'No description.'}</pre>
    <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'edit', title: i.title, body: i.body }, i)}>Edit title/body</Button><Button isDisabled={disabled || !i.isEligible || !project.enabled} onPress={() => open({ kind: 'enqueue' }, i)}>Explicitly enqueue</Button><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'refresh' }, i)}>Refresh queued eligibility</Button></div>
    <p className="text-sm text-tertiary">Enqueue and eligibility refresh retain Server admission checks; they never bypass policy, relationships, authorization or capacity.</p>
  </div></TableCard.Root>;
}
export function IssueEditor() {
  const w = useProjects(), runtime = useRuntime(), d = w.issueDraft, navigate = useNavigate(), [params] = useSearchParams();
  const [busy, setBusy] = useState(false), guard = useRef(false), controller = useRef<AbortController | undefined>(undefined);
  const [error, setError] = useState(''), [discard, setDiscard] = useState(false);
  useEffect(() => () => controller.current?.abort(), []);
  if (!d) return null;
  const change = d.change, needsPreview = change.kind !== 'enqueue' && change.kind !== 'refresh', locked = w.locked(d.project.id);
  const update = (change: IssueChange) => { w.setIssueDraft({ ...d, change, preview: undefined }); setError(''); };
  async function submit() {
    if (!d || guard.current || locked) return; guard.current = true; setBusy(true); setError('');
    controller.current?.abort(); controller.current = new AbortController();
    try {
      if (needsPreview && !d.preview) {
        const request = changeRequest(d.project.id, d.before?.number, d.change);
        const preview = await runtime.preview(request.path, request.method, request.body ?? {}, controller.current.signal, issueResult);
        if (!preview.previewOnly || preview.repository.toLowerCase() !== d.project.repository.toLowerCase()) throw Error('Invalid preview.');
        if (!preview.changed) { setError('No changes to apply. Close or edit this draft.'); return; }
        w.setIssueDraft({ ...d, preview });
      } else {
        const result = await w.submitIssue(d);
        if (d.change.kind === 'create' && !Array.isArray(result) && 'issueNumber' in result && result.issueNumber) { const next = new URLSearchParams(params); next.set('issue', String(result.issueNumber)); next.set('issues', '1'); navigate(`/projects/${encodeURIComponent(d.project.id)}?${next}`); }
      }
    } catch { setError('Result unavailable. Draft retained. Check current Issue/repository state and reconcile any uncertain action before retrying.'); }
    finally { guard.current = false; setBusy(false); }
  }
  return <>
    <FormDialog isOpen={d.open} title={`${change.kind === 'create' ? 'Create' : change.kind === 'edit' ? 'Edit' : 'Administer'} Issue${d.before ? ` #${d.before.number}` : ''}`} description={`${d.project.name} · ${d.project.repository}. ${needsPreview ? 'Preview Server changes before applying.' : change.kind === 'enqueue' ? 'Explicitly request work. The Server checks current admission; this does not grant execution authority.' : 'Re-evaluate queued eligibility using current GitHub evidence.'}`} pending={busy} onClose={() => w.setIssueDraft({ ...d, open: false })}>
      <form className="flex flex-col gap-4" onSubmit={event => { event.preventDefault(); void submit(); }}>
        <fieldset disabled={busy || locked} className="flex min-w-0 flex-col gap-3">
          {(change.kind === 'create' || change.kind === 'edit') && <><Input label="Issue title" value={change.title} isRequired maxLength={256} onChange={title => update({ ...change, title })} /><TextArea label="Issue body" value={change.body} maxLength={65536} onChange={body => update({ ...change, body })} /></>}
          {change.kind === 'dependency' && <NumberInput label="Blocking Issue number" min={1} max={2147483647} isRequired value={change.blockerIssueNumber ? String(change.blockerIssueNumber) : ''} onChange={value => update({ ...change, blockerIssueNumber: Number(value) })} />}
          {change.kind === 'label' && <p className="text-sm text-secondary">{change.applied ? 'Add' : 'Remove'} configured label “{change.label}”.</p>}
          {d.preview && <><h3 className="font-semibold text-primary">Server preview</h3><p className="text-sm text-secondary">{d.preview.title}</p>{d.preview.body != null && <pre className="whitespace-pre-wrap break-words text-sm text-secondary">{d.preview.body}</pre>}{change.kind === 'dependency' && <p className="text-sm text-secondary">{change.applied ? 'Add' : 'Remove'} blocked-by relationship with Issue #{change.blockerIssueNumber}.</p>}{change.kind === 'label' && <p className="text-sm text-secondary">{change.applied ? 'Add' : 'Remove'} label “{change.label}”.</p>}</>}
        </fieldset>
        {error && <Notice error>{error}</Notice>}{locked && <Notice error>Result uncertain. Close and reconcile authoritative state.</Notice>}
        <div className="flex flex-wrap justify-end gap-2"><Button autoFocus color="secondary" isDisabled={busy} onPress={() => w.setIssueDraft({ ...d, open: false })}>Close</Button><Button color="secondary" isDisabled={busy || locked} onPress={() => setDiscard(true)}>Discard draft</Button><Button type="submit" isDisabled={busy || locked}>{busy ? 'Checking…' : needsPreview ? d.preview ? 'Apply previewed changes' : 'Preview changes' : change.kind === 'enqueue' ? 'Enqueue Issue' : 'Refresh eligibility'}</Button></div>
      </form>
    </FormDialog>
    {discard && <ConfirmationDialog isOpen title="Discard Issue draft?" description="Unsaved Issue changes will be lost. No GitHub state is changed." destructive actionLabel="Discard draft" onClose={() => setDiscard(false)} onSubmit={async () => { w.setIssueDraft(undefined); }} />}
  </>;
}
