import { t, useLanguage, statusLabel, localizeText } from '../../shared/i18n';
import { timestamp } from '../../model';
import { ExternalLink } from '../../shared/Actions';
import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams, useNavigate } from 'react-router-dom';
import { FormDialog, ConfirmationDialog } from '../../shared/Dialogs';
import { AdvancedDisclosure, Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import { Input } from '../../shared/Input';
import { TextArea } from '../../shared/TextArea';
import { NumberInput } from '../../shared/NumberInput';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead, useRuntime } from '../../shared/api/session';
import { executions } from '../../shared/api/validation';
import { access, issue, issueList, issueResult, issueUrl, type Project } from './contracts';
import { issuePath, projectPath, changeRequest, type IssueChange } from './model';
import { useProjects } from './Workspace';
export function Issues({ project }: { project: Project }) {
  useLanguage();
  const [params, setParams] = useSearchParams(), w = useProjects();
  const [state, setState] = useState(params.get('issueState') ?? 'open'), [label, setLabel] = useState(params.get('label') ?? ''), [checkAccess, setCheckAccess] = useState(false);
  // Back/reload restore filters from the URL; unsent filter input is a transient form.
  useEffect(() => { setState(params.get('issueState') ?? 'open'); setLabel(params.get('label') ?? ''); }, [params]);
  const number = Number(params.get('issue'));
  const context = { state: ['open', 'closed', 'all'].includes(params.get('issueState') ?? '') ? params.get('issueState') ?? 'open' : 'open', limit: '50' };
  const query = new URLSearchParams(context); if (params.get('label')) query.set('label', params.get('label') ?? '');
  const open = (change: IssueChange, before?: import('./contracts').Issue) => {
    if (w.issueDraft) { w.setIssueDraft({ ...w.issueDraft, open: true }); w.setMessage(t("projects.resumeOrDiscardTheRetainedIssueDraftBeforeStartingAnotherAction")); return; }
    w.setIssueDraft({ project, before, change, open: true });
  };
  return <section className="space-y-4">
    <h2 className="text-lg font-semibold text-primary">{t("projects.gitHubIssues")}</h2>
    <div className="flex flex-wrap gap-2"><Button color="secondary" onPress={() => setCheckAccess(true)}>{t("projects.checkRepositoryAccess")}</Button><Button isDisabled={w.locked(project.id)} onPress={() => open({ kind: 'create', title: '', body: '' })}>{t("projects.createIssue")}</Button></div>
    {checkAccess && <RepositoryAccess id={project.id} />}
    <form className="flex flex-wrap items-end gap-3" onSubmit={event => { event.preventDefault(); const next = new URLSearchParams(params); next.set('issueState', state); if (label.trim()) next.set('label', label.trim()); else next.delete('label'); next.set('issues', '1'); next.delete('issue'); setParams(next); }}>
      <label className="flex flex-col gap-2 text-sm text-secondary">{t("projects.issueState")}<select value={state} onChange={event => setState(event.target.value)} className="rounded-lg border border-secondary bg-primary p-2"><option value="open">{t("projects.open")}</option><option value="closed">{t("projects.closed")}</option><option value="all">{t("projects.all")}</option></select></label>
      <Input label={t("projects.filterByLabel")} value={label} maxLength={100} onChange={setLabel} />
      <Button type="submit" color="secondary">{t("projects.loadIssues")}</Button>
    </form>
    <p className="text-sm text-tertiary">{t("projects.atMost50MatchingIssuesListingDoesNotEnqueueWorkNoTotal")}</p>
    {params.get('issues') === '1' ? <IssueList project={project} query={query.toString()} open={open} /> : <ViewState title={t("projects.loadIssuesToQueryGitHub")} />}
    {params.has('issue') && (Number.isSafeInteger(number) && number > 0 && number <= 2147483647 ? <IssueDetail project={project} number={number} open={open} /> : <Notice error>{t("projects.invalidIssueNumberInURL")}</Notice>)}
  </section>;
}
function RepositoryAccess({ id }: { id: string }) {
  useLanguage();
  const read = useApiRead(projectPath(id) + '/github/access', access);
  return read.data ? <Notice><StatusBadge tone={read.data.cliAuthenticated ? 'success' : 'error'}>{t("projects.serverGitHubAuthentication")}{' '}{read.data.cliAuthenticated ? statusLabel('available') : statusLabel('unavailable')}</StatusBadge> <StatusBadge tone={read.data.repositoryReadable ? 'success' : 'error'}>{t("projects.repositoryRead")}{' '}{read.data.repositoryReadable ? statusLabel('available') : statusLabel('unavailable')}</StatusBadge>{t("projects.checked")}{' '}{timestamp(read.data.checkedAtUtc)}{t("projects.workerCheckoutAndPushPermissionAreSeparate")}</Notice> : <ViewState title={read.error ? t("projects.accessUnavailableCheckServerGitHubConnectionAndRepositoryPermissionInSettings") : t("projects.checkingRepositoryAccess")} error={!!read.error} />;
}
function IssueList({ project, query, open }: { project: Project; query: string; open(change: IssueChange, before: import('./contracts').Issue): void }) {
  useLanguage();
  const read = useApiRead(issuePath(project.id) + '?' + query, issueList), [params] = useSearchParams(), w = useProjects();
  const queue = useApiRead(`/api/v1/executions?projectId=${encodeURIComponent(project.id)}&workType=github-issue&limit=100&offset=0`, executions);
  if (!read.data) return <ViewState title={read.error ? t("projects.issuesUnavailableCheckRepositoryAccessAndRetryLoadIssues") : t("projects.loadingIssues")} error={!!read.error} />;
  if (!read.data.length) return <ViewState title={t("projects.noIssuesMatchedTheseFilters")} />;
  return <TableCard.Root><ul className="divide-y divide-secondary">{read.data.map(i => { const next = new URLSearchParams(params); next.set('issue', String(i.number)); const current = queue.data?.find(e => e.workReference?.type === 'github-issue' && e.workReference.id === String(i.number) && ['Queued', 'Assigned', 'Running'].includes(e.state)); return <li key={i.number} className="flex flex-wrap items-start justify-between gap-3 p-4"><div className="min-w-0 flex-1 basis-full space-y-2 sm:basis-auto"><ExternalLink href={issueUrl(project.repository, i)} className="font-medium text-primary">#{i.number} · {i.title}</ExternalLink><Link to={`?${next}`} className="block text-sm text-brand-secondary">{t('projects.issueDetails')}</Link><div className="flex flex-wrap gap-2"><StatusBadge tone={i.state.toLowerCase() === 'open' ? 'success' : 'gray'}>{localizeText(i.state)}</StatusBadge><StatusBadge tone={i.isEligible ? 'success' : 'warning'}>{i.isEligible ? statusLabel('Eligible') : statusLabel('Ineligible')}</StatusBadge><StatusBadge tone={current ? 'success' : 'gray'}>{current ? `${localizeText(current.state)} · ${current.id}` : t(queue.data ? 'projects.queueNotShown' : queue.error ? 'projects.queueUnavailable' : 'projects.queueLoading')}</StatusBadge></div><p className="text-sm text-tertiary">{t("projects.labels")}{' '}{i.labels.join(', ') || t('projects.none')}</p>{i.eligibilityReasons.map(r => <p key={r} className="text-sm text-warning-primary">{r}</p>)}{i.blockedBy.length > 0 && <AdvancedDisclosure title={t('projects.blockedByIssues', { count: i.blockedBy.length })}>{i.blockedBy.map(b => <p key={b.number}><ExternalLink href={issueUrl(project.repository, b)}>{`#${b.number} · ${b.title} · ${localizeText(b.state)}`}</ExternalLink></p>)}</AdvancedDisclosure>}</div><div className="flex flex-wrap gap-2">{i.isEligible && !current && <Button size="sm" color="secondary" isDisabled={w.locked(project.id) || !project.enabled} onPress={() => open({ kind: 'enqueue' }, i)}>{t("projects.enqueue")}{i.number}</Button>}</div></li>; })}</ul></TableCard.Root>;
}
function IssueDetail({ project, number, open }: { project: Project; number: number; open(change: IssueChange, before: import('./contracts').Issue): void }) {
  useLanguage();
  const read = useApiRead(issuePath(project.id, number), value => { const observed = issue(value); if (observed.number !== number) throw Error(t("projects.unexpectedIssueIdentity")); return observed; }), w = useProjects(), [params, setParams] = useSearchParams();
  if (!read.data) return <ViewState title={read.error ? t("projects.issueUnavailableOrDeletedCheckAccessAndReload") : t("projects.loadingIssue")} error={!!read.error} />;
  const i = read.data, disabled = w.locked(project.id);
  return <TableCard.Root><div className="space-y-4 p-5">
    <div className="flex flex-wrap items-start justify-between gap-2"><h3 className="break-words text-lg font-semibold text-primary"><ExternalLink href={issueUrl(project.repository, i)}>#{i.number} · {i.title}</ExternalLink></h3><Button color="secondary" onPress={() => { const next = new URLSearchParams(params); next.delete('issue'); setParams(next); }}>{t("projects.closeIssueDetail")}</Button></div>
    <StatusBadge tone={i.isEligible ? 'success' : 'warning'}>{localizeText(i.state)} · {i.isEligible ? statusLabel('Eligible') : statusLabel('Ineligible')}</StatusBadge>
    {i.eligibilityReasons.map(r => <p key={r} className="text-sm text-secondary">{r}</p>)}
    <p className="text-sm text-tertiary">{t("projects.labels2")}{' '}{i.labels.join(', ') || t('projects.none')}</p>
    <h4 className="font-semibold text-primary">{t("projects.configuredEligibilityLabels")}</h4>
    <div className="flex flex-wrap gap-2">{[project.issueReadyLabel, project.issueBlockedLabel].filter((l): l is string => !!l).map(label => { const applied = i.labels.some(l => l.toLowerCase() === label.toLowerCase()); return <Button key={label} color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'label', label, applied: !applied }, i)}>{applied ? t('projects.remove') : t('projects.add')} {label}</Button>; })}</div>
    {!project.issueReadyLabel && !project.issueBlockedLabel && <p className="text-sm text-tertiary">{t("projects.noEligibilityLabelsConfiguredEditProjectPolicyToConfigureThem")}</p>}
    <h4 className="font-semibold text-primary">{t("projects.nativeBlockedByRelationships")}</h4>
    {i.blockedBy.length ? i.blockedBy.map(b => <div key={b.number} className="flex flex-wrap items-center gap-2"><ExternalLink href={issueUrl(project.repository, b)} className="text-sm text-secondary">#{b.number} · {b.title} · {localizeText(b.state)}</ExternalLink><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'dependency', blockerIssueNumber: b.number, applied: false }, i)}>{t("projects.removeRelationship")}{b.number}</Button></div>) : <p className="text-sm text-tertiary">{t("projects.noBlockingIssues")}</p>}
    <Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'dependency', blockerIssueNumber: 0, applied: true }, i)}>{t("projects.addBlockedByIssue")}</Button>
    <h4 className="font-semibold text-primary">{t("projects.issueDescription")}</h4><pre className="whitespace-pre-wrap break-words text-sm text-secondary">{i.body || t("projects.noDescription")}</pre>
    <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'edit', title: i.title, body: i.body }, i)}>{t("projects.editTitleBody")}</Button><Button isDisabled={disabled || !i.isEligible || !project.enabled} onPress={() => open({ kind: 'enqueue' }, i)}>{t("projects.explicitlyEnqueue")}</Button><Button color="secondary" isDisabled={disabled} onPress={() => open({ kind: 'refresh' }, i)}>{t("projects.refreshQueuedEligibility")}</Button></div>
    <p className="text-sm text-tertiary">{t("projects.enqueueAndEligibilityRefreshRetainServerAdmissionChecksTheyNeverBypassPolicy")}</p>
  </div></TableCard.Root>;
}
export function IssueEditor() {
  useLanguage();
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
        if (!preview.previewOnly || preview.repository.toLowerCase() !== d.project.repository.toLowerCase()) throw Error(t("projects.invalidPreview"));
        if (!preview.changed) { setError(t("projects.noChangesToApplyCloseOrEditThisDraft")); return; }
        w.setIssueDraft({ ...d, preview });
      } else {
        const result = await w.submitIssue(d);
        if (d.change.kind === 'create' && !Array.isArray(result) && 'issueNumber' in result && result.issueNumber) { const next = new URLSearchParams(params); next.set('issue', String(result.issueNumber)); next.set('issues', '1'); navigate(`/projects/${encodeURIComponent(d.project.id)}?${next}`); }
      }
    } catch { setError(t("projects.resultUnavailableDraftRetainedCheckCurrentIssueRepositoryStateAndReconcileAny")); }
    finally { guard.current = false; setBusy(false); }
  }
  return <>
    <FormDialog isOpen={d.open} title={t(change.kind === 'create' ? 'projects.createIssueHeading' : change.kind === 'edit' ? 'projects.editIssueHeading' : 'projects.administerIssueHeading', { number: d.before ? ` #${d.before.number}` : '' })} description={`${d.project.name} · ${d.project.repository}. ${needsPreview ? t("projects.previewServerChangesBeforeApplying") : change.kind === 'enqueue' ? t("projects.explicitlyRequestWorkTheServerChecksCurrentAdmissionThisDoesNotGrant") : t("projects.reEvaluateQueuedEligibilityUsingCurrentGitHubEvidence")}`} pending={busy} onClose={() => w.setIssueDraft({ ...d, open: false })}>
      <form className="flex flex-col gap-4" onSubmit={event => { event.preventDefault(); void submit(); }}>
        <fieldset disabled={busy || locked} className="flex min-w-0 flex-col gap-3">
          {(change.kind === 'create' || change.kind === 'edit') && <><Input label={t("projects.issueTitle")} value={change.title} isRequired maxLength={256} onChange={title => update({ ...change, title })} /><TextArea label={t("projects.issueBody")} value={change.body} maxLength={65536} onChange={body => update({ ...change, body })} /></>}
          {change.kind === 'dependency' && <NumberInput label={t("projects.blockingIssueNumber")} min={1} max={2147483647} isRequired value={change.blockerIssueNumber ? String(change.blockerIssueNumber) : ''} onChange={value => update({ ...change, blockerIssueNumber: Number(value) })} />}
          {change.kind === 'label' && <p className="text-sm text-secondary">{change.applied ? t('projects.add') : t('projects.remove')}{' '}{t("projects.configuredLabel")}{change.label}”.</p>}
          {d.preview && <><h3 className="font-semibold text-primary">{t("projects.serverPreview")}</h3><p className="text-sm text-secondary">{d.preview.title}</p>{d.preview.body != null && <pre className="whitespace-pre-wrap break-words text-sm text-secondary">{d.preview.body}</pre>}{change.kind === 'dependency' && <p className="text-sm text-secondary">{change.applied ? t('projects.add') : t('projects.remove')}{' '}{t("projects.blockedByRelationshipWithIssue")}{change.blockerIssueNumber}.</p>}{change.kind === 'label' && <p className="text-sm text-secondary">{change.applied ? t('projects.add') : t('projects.remove')}{' '}{t("projects.label")}{change.label}”.</p>}</>}
        </fieldset>
        {error && <Notice error>{error}</Notice>}{locked && <Notice error>{t("projects.resultUncertainCloseAndReconcileAuthoritativeState")}</Notice>}
        <div className="flex flex-wrap justify-end gap-2"><Button autoFocus color="secondary" isDisabled={busy} onPress={() => w.setIssueDraft({ ...d, open: false })}>{t("projects.close")}</Button><Button color="secondary" isDisabled={busy || locked} onPress={() => setDiscard(true)}>{t("projects.discardDraft")}</Button><Button type="submit" isDisabled={busy || locked}>{busy ? t("projects.checking") : needsPreview ? d.preview ? t("projects.applyPreviewedChanges") : t("projects.previewChanges") : change.kind === 'enqueue' ? t("projects.enqueueIssue") : t("projects.refreshEligibility")}</Button></div>
      </form>
    </FormDialog>
    {discard && <ConfirmationDialog isOpen title={t("projects.discardIssueDraft")} description={t("projects.unsavedIssueChangesWillBeLostNoGitHubStateIsChanged")} destructive actionLabel={t("projects.discardDraft")} onClose={() => setDiscard(false)} onSubmit={async () => { w.setIssueDraft(undefined); }} />}
  </>;
}
