import { useEffect, useRef, useState } from 'react';
import { FormDialog, ConfirmationDialog } from '../../shared/Dialogs';
import { AdvancedDisclosure, Notice } from '../../shared/Presentation';
import { Input } from '../../untitled/components/base/input/input';
import { TextArea } from '../../shared/TextArea';
import { NumberInput } from '../../shared/NumberInput';
import { Button } from '../../untitled/components/base/buttons/button';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { useRuntime } from '../../shared/api/session';
import { repositoryPage, verification, type Definition, type Repository } from './contracts';
import { useProjects } from './Workspace';
import { definitionOf } from './model';
export function ProjectEditor() {
  const w = useProjects(), runtime = useRuntime(), d = w.draft;
  const [busy, setBusy] = useState(false), guard = useRef(false);
  const [error, setError] = useState(''), [discard, setDiscard] = useState(false);
  const [repositories, setRepositories] = useState<Repository[]>([]), [next, setNext] = useState<number | null>(null);
  const controller = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => controller.current?.abort(), []);
  if (!d) return null;
  const locked = w.locked(d.before?.id), definition = d.definition;
  const update = (patch: Partial<Definition>) => { w.setDraft({ ...d, definition: { ...definition, ...patch }, reviewed: undefined }); setError(''); };
  async function run(action: () => Promise<void>) {
    if (guard.current) return; guard.current = true; setBusy(true); setError('');
    controller.current?.abort(); controller.current = new AbortController();
    try { await action(); } catch { setError('Operation unavailable. Check Server GitHub access, repository permission and branch, or refresh the current revision. Draft retained.'); }
    finally { guard.current = false; setBusy(false); }
  }
  async function discover(page = 1) {
    await run(async () => {
      const result = await runtime.read('/api/v1/github/repositories?page=' + page, controller.current?.signal ?? new AbortController().signal, repositoryPage);
      setRepositories(old => page === 1 ? result.repositories : [...old, ...result.repositories]); setNext(result.nextPage);
    });
  }
  async function review() {
    if (!d) return;
    await run(async () => {
      const snapshot = definitionOf(definition);
      const checked = await runtime.preview('/api/v1/projects/verify', 'POST', { ...snapshot }, controller.current?.signal ?? new AbortController().signal, verification);
      if (!checked.repositoryReadable || !checked.branchExists || checked.repository.toLowerCase() !== snapshot.repository.trim().toLowerCase() || checked.defaultBranch !== snapshot.defaultBranch.trim()) throw Error('Verification failed.');
      w.setDraft({ ...d, reviewed: snapshot, step: 3 });
    });
  }
  const text = (key: 'name' | 'repository' | 'defaultBranch' | 'description' | 'issueReadyLabel' | 'issueBlockedLabel', label: string, required = false, maxLength?: number) => <Input label={label} value={definition[key] ?? ''} onChange={value => update({ [key]: value || (required || key === 'description' ? '' : null) })} isRequired={required} maxLength={maxLength} />;
  return <>
    <FormDialog isOpen={d.open} title={d.before ? 'Edit project' : 'Create project'} description={`Step ${d.step} of 3 · ${['Repository', 'Essential configuration', 'Review'][d.step - 1]}. Closing retains this transient draft until sign out or reload.`} pending={busy} onClose={() => w.setDraft({ ...d, open: false })}>
      <form className="flex flex-col gap-4" onInvalidCapture={event => { const details = event.target instanceof HTMLElement ? event.target.closest("details") : null; if (details) details.open = true; }} onSubmit={event => {
        event.preventDefault();
        if (locked || busy) return;
        if (d.step === 1) w.setDraft({ ...d, step: 2 });
        else if (d.step === 2) void review();
        else void run(async () => { await w.save(d); });
      }}>
        <fieldset disabled={busy || locked} className="flex min-w-0 flex-col gap-4">
          {d.step === 1 && <>
            <Button color="secondary" onPress={() => { void discover(); }}>Discover repositories</Button>
            {repositories.length > 0 && <label className="flex flex-col gap-2 text-sm text-secondary">Accessible repository<select className="w-full rounded-lg border border-secondary bg-primary p-2" value={repositories.some(r => r.repository === definition.repository) ? definition.repository : ''} onChange={event => { const selected = repositories.find(r => r.repository === event.target.value); if (selected) update(selected); }}><option value="">Choose repository</option>{repositories.map((r, index) => <option key={`${r.repository}-${index}`} value={r.repository}>{r.repository}</option>)}</select></label>}
            {next && <Button color="secondary" onPress={() => { void discover(next); }}>More repositories</Button>}
            <p className="text-sm text-tertiary">Discovery uses Server GitHub authentication. If unavailable, enter owner/repository manually and verify before save.</p>
            {text('repository', 'Repository (owner/repository)', true)}
          </>}
          {d.step === 2 && <>
            {text('name', 'Project name', true, 120)}{text('defaultBranch', 'Base branch', true, 200)}<TextArea label="Description" value={definition.description} maxLength={4000} onChange={description => update({ description })} />
            <Checkbox label="Enable automatic Issue discovery" isSelected={definition.automaticDiscovery?.enabled === true} onChange={enabled => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), enabled } })} />
            <p className="text-sm text-tertiary">Discovery can queue eligible Issues. Execution requires an authorized eligible Worker and available capacity. Lifecycle enablement is separate.</p>
            <AdvancedDisclosure title="Advanced · discovery and execution requirements">
              {(['intervalSeconds', 'pageSize', 'deadlineSeconds'] as const).map((key, index) => <NumberInput key={key} label={['Discovery interval (seconds)', 'Maximum discovery page size', 'Cycle deadline (seconds)'][index]} min={[30, 1, 10][index]} max={[86400, 100, 120][index]} value={String(definition.automaticDiscovery?.[key] ?? [300, 25, 120][index])} onChange={value => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { enabled: false, intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), [key]: Number(value) } })} isRequired />)}
              {text('issueReadyLabel', 'Issue ready label', false, 100)}{text('issueBlockedLabel', 'Issue blocked label', false, 100)}
              <p>Changing policy does not change GitHub labels. Open blocked-by dependencies prevent eligibility.</p>
              <h3 className="font-semibold">Typed Worker requirements</h3>
              {definition.requirements.map((r, index) => <div key={index} className="space-y-2 rounded-lg border border-secondary p-3">
                {(['type', 'name', 'version', 'scope'] as const).map(key => <Input key={key} label={`Requirement ${index + 1} ${key}`} value={r[key] ?? ''} isRequired={key === 'type' || key === 'name'} onChange={value => update({ requirements: definition.requirements.map((r, position) => position === index ? { ...r, [key]: value || (key === 'version' || key === 'scope' ? null : '') } : r) })} />)}
                <Button color="secondary" onPress={() => update({ requirements: definition.requirements.filter((_, position) => position !== index) })}>Remove requirement {index + 1}</Button>
              </div>)}
              <p>Types include runtime, tool, service, authentication and agent-provider. Versions accept numeric versions or &gt;= versions. Authentication scope accepts owner/repository.</p>
              <Button color="secondary" isDisabled={definition.requirements.length >= 64} onPress={() => update({ requirements: [...definition.requirements, { type: 'runtime', name: '', version: null, scope: null }] })}>Add requirement</Button>
            </AdvancedDisclosure>
          </>}
          {d.step === 3 && <><p className="font-medium text-primary">{definition.name} · {definition.repository} · {definition.defaultBranch}</p><p className="whitespace-pre-wrap text-sm text-secondary">{definition.description || 'No description.'}</p><p className="text-sm text-secondary">Ready label: {definition.issueReadyLabel || 'none'} · Blocked label: {definition.issueBlockedLabel || 'none'}</p><p className="text-sm text-secondary">Automatic discovery: {definition.automaticDiscovery?.enabled ? 'enabled' : 'disabled'} · Interval {definition.automaticDiscovery?.intervalSeconds ?? 300}s · Page size {definition.automaticDiscovery?.pageSize ?? 25} · Deadline {definition.automaticDiscovery?.deadlineSeconds ?? 120}s</p>{definition.requirements.map((r, index) => <p key={index} className="text-sm text-secondary">{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? ` for ${r.scope}` : ''}</p>)}<Notice>Repository and branch read verified. Worker checkout, push permission, Codex execution readiness and capacity remain separate checks.</Notice></>}
        </fieldset>
        {error && <Notice error>{error}</Notice>}
        {locked && <Notice error>Save result uncertain. Close and choose Check saved definition before another write.</Notice>}
        {w.conflict && (!d.before || w.conflict.id === d.before.id) && <Notice error>Revision {w.conflict.revision} is now current. Your draft was retained.<Button color="secondary" isDisabled={locked} onPress={() => { const current = w.conflict; if (current) { w.setDraft({ before: current, definition: definitionOf(current), step: 1, open: true }); w.setConflict(undefined); setError(''); } }}>Load current definition</Button></Notice>}
        <div className="flex flex-wrap justify-end gap-2">
          <Button autoFocus color="secondary" isDisabled={busy} onPress={() => w.setDraft({ ...d, open: false })}>Close</Button>
          <Button color="secondary" isDisabled={busy || locked} onPress={() => setDiscard(true)}>Discard draft</Button>
          {d.step > 1 && <Button color="secondary" isDisabled={busy || locked} onPress={() => w.setDraft({ ...d, step: d.step - 1 })}>Back</Button>}
          <Button type="submit" isDisabled={busy || locked || d.step === 3 && !d.reviewed}>{busy ? 'Checking…' : d.step === 1 ? 'Next' : d.step === 2 ? 'Verify and review' : 'Save reviewed project'}</Button>
        </div>
      </form>
    </FormDialog>
    {discard && <ConfirmationDialog isOpen title="Discard project draft?" description="Unsaved configuration will be lost. No Server definition is changed." actionLabel="Discard draft" destructive onClose={() => setDiscard(false)} onSubmit={async () => { w.setDraft(undefined); }} />}
  </>;
}
