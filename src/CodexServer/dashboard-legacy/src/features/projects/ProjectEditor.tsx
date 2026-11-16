import { t, useLanguage, type TranslationKey, localizeText } from '../../shared/i18n';
import { useEffect, useRef, useState } from 'react';
import { FormDialog, ConfirmationDialog } from '../../shared/Dialogs';
import { AdvancedDisclosure, Notice } from '../../shared/Presentation';
import { Input } from '../../shared/Input';
import { TextArea } from '../../shared/TextArea';
import { NumberInput } from '../../shared/NumberInput';
import { Button } from '../../untitled/components/base/buttons/button';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { Toggle } from '../../untitled/components/base/toggle/toggle';
import { useRuntime } from '../../shared/api/session';
import { repositoryPage, verification, type Definition, type Repository } from './contracts';
import { useProjects } from './Workspace';
import { definitionOf } from './model';
export function ProjectEditor() {
  useLanguage();
  const w = useProjects(), runtime = useRuntime(), d = w.draft;
  const editedProjectId = d?.before?.id;
  const [busy, setBusy] = useState(false), guard = useRef(false);
  const [error, setError] = useState(''), [discard, setDiscard] = useState(false);
  const [copyStatus, setCopyStatus] = useState('');
  const [repositories, setRepositories] = useState<Repository[]>([]), [next, setNext] = useState<number | null>(null);
  const controller = useRef<AbortController | undefined>(undefined);
  const editBody = useRef<HTMLFieldSetElement | null>(null);
  useEffect(() => () => controller.current?.abort(), []);
  useEffect(() => { if (error && editedProjectId !== undefined) editBody.current?.scrollTo({ top: 0 }); }, [error, editedProjectId]);
  if (!d) return null;
  const locked = w.locked(d.before?.id), definition = d.definition;
  const update = (patch: Partial<Definition>) => { w.setDraft({ ...d, definition: { ...definition, ...patch }, reviewed: undefined }); setError(''); };
  async function run(action: () => Promise<void>) {
    if (guard.current) return; guard.current = true; setBusy(true); setError('');
    controller.current?.abort(); controller.current = new AbortController();
    try { await action(); } catch { setError(t("projects.operationUnavailableCheckServerGitHubAccessRepositoryPermissionAndBranchOrRefresh")); }
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
      if (!checked.repositoryReadable || !checked.branchExists || checked.repository.toLowerCase() !== snapshot.repository.trim().toLowerCase() || checked.defaultBranch !== snapshot.defaultBranch.trim()) throw Error(t("projects.verificationFailed"));
      w.setDraft({ ...d, reviewed: snapshot, step: 3 });
    });
  }
  async function saveExisting() {
    if (!d) return;
    await run(async () => {
      const snapshot = definitionOf(definition);
      const checked = await runtime.preview('/api/v1/projects/verify', 'POST', { ...snapshot }, controller.current?.signal ?? new AbortController().signal, verification);
      if (!checked.repositoryReadable || !checked.branchExists || checked.repository.toLowerCase() !== snapshot.repository.trim().toLowerCase() || checked.defaultBranch !== snapshot.defaultBranch.trim()) throw Error(t("projects.verificationFailed"));
      await w.save({ ...d, reviewed: snapshot });
    });
  }
  async function copyRepository() {
    try {
      await navigator.clipboard.writeText(definition.repository);
      setCopyStatus(t('projects.repositoryCopied'));
    } catch {
      setCopyStatus(t('projects.repositoryCopyFailed'));
    }
  }
  const text = (key: 'name' | 'repository' | 'defaultBranch' | 'description' | 'issueReadyLabel' | 'issueBlockedLabel', label: string, required = false, maxLength?: number) => <Input label={label} value={definition[key] ?? ''} onChange={value => update({ [key]: value || (required || key === 'description' ? '' : null) })} isRequired={required} maxLength={maxLength} />;

  const validationEditor = <section className="flex flex-col gap-3" aria-label={t('projects.validationCommands')}>
    <h3 className="font-semibold text-primary">{t('projects.validationCommands')}</h3>
    <p className="text-sm text-tertiary">{t('projects.validationCommandsHelp')}</p>
    <Checkbox label={t('projects.validationUseDefaults')} isSelected={definition.validationCommands === null} onChange={useDefaults => update({ validationCommands: useDefaults ? null : [] })} />
    {definition.validationCommands?.map((command, index) => <div key={index} className="flex flex-col gap-2 rounded-lg border border-secondary p-3">
      <TextArea label={t('projects.validationCommand', { number: index + 1 })} value={command} isRequired maxLength={4000} onChange={value => update({ validationCommands: definition.validationCommands?.map((item, position) => position === index ? value : item) ?? [] })} />
      <div className="flex flex-wrap gap-2">
        <Button color="secondary" isDisabled={index === 0} aria-label={t('projects.validationMoveUp', { number: index + 1 })} onPress={() => {
          const commands = [...(definition.validationCommands ?? [])];
          [commands[index - 1], commands[index]] = [commands[index], commands[index - 1]];
          update({ validationCommands: commands });
        }}>{t('projects.moveUp')}</Button>
        <Button color="secondary" isDisabled={index === (definition.validationCommands?.length ?? 0) - 1} aria-label={t('projects.validationMoveDown', { number: index + 1 })} onPress={() => {
          const commands = [...(definition.validationCommands ?? [])];
          [commands[index], commands[index + 1]] = [commands[index + 1], commands[index]];
          update({ validationCommands: commands });
        }}>{t('projects.moveDown')}</Button>
        <Button color="secondary" aria-label={t('projects.validationRemove', { number: index + 1 })} onPress={() => update({ validationCommands: definition.validationCommands?.filter((_, position) => position !== index) ?? [] })}>{t('projects.removeCommand')}</Button>
      </div>
    </div>)}
    <Button color="secondary" isDisabled={(definition.validationCommands?.length ?? 0) >= 64} onPress={() => update({ validationCommands: [...(definition.validationCommands ?? []), ''] })}>{t('projects.addCommand')}</Button>
  </section>;

  if (d.before) return <>
    <FormDialog isOpen={d.open} title={t('projects.editProject')} description={t('projects.editorDescription')} pending={busy} variant="side-panel" onClose={() => w.setDraft({ ...d, open: false })}
      headerActions={<div className="flex flex-wrap items-center justify-end gap-2">
        <Button color="tertiary" aria-label={t('shared.closeDialog')} isDisabled={busy} onPress={() => w.setDraft({ ...d, open: false })}>
          <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" width="20" height="20"><path d="M6 6l12 12M18 6 6 18" stroke="currentColor" strokeWidth="2" strokeLinecap="round" /></svg>
        </Button>
        <Button color="secondary" isDisabled={busy} onPress={() => w.setDraft({ ...d, open: false })}>{t('projects.cancel')}</Button>
        <Button type="submit" form="project-edit-form" color="primary" className="bg-success-solid hover:bg-green-700" isDisabled={busy || locked}>{busy ? t('projects.checking') : t('projects.saveChanges')}</Button>
      </div>}>
      <form id="project-edit-form" className="flex min-h-0 flex-1 flex-col" onInvalidCapture={event => { const details = event.target instanceof HTMLElement ? event.target.closest('details') : null; if (details) details.open = true; }} onSubmit={event => { event.preventDefault(); if (!locked && !busy) void saveExisting(); }}>
        <fieldset ref={editBody} disabled={busy || locked} className="flex min-h-0 min-w-0 flex-1 flex-col gap-5 overflow-y-auto p-5 sm:p-8">
          <legend className="sr-only">{t('projects.editProject')}</legend>
          {error && <Notice error>{error}</Notice>}
          {locked && <Notice error>{t('projects.saveResultUncertainCloseAndChooseCheckSavedDefinitionBeforeAnotherWrite')}</Notice>}
          {w.conflict && w.conflict.id === d.before.id && <Notice error>{t('projects.revision')} {w.conflict.revision} {t('projects.isNowCurrentYourDraftWasRetained')}<Button color="secondary" isDisabled={locked} onPress={() => { const current = w.conflict; if (current) { w.setDraft({ before: current, definition: definitionOf(current), step: 1, open: true }); w.setConflict(undefined); setError(''); } }}>{t('projects.loadCurrentDefinition')}</Button></Notice>}
          <section aria-labelledby="project-general-heading" className="rounded-xl border border-secondary bg-primary p-5 shadow-xs sm:p-6">
            <div className="mb-5">
              <h2 id="project-general-heading" className="text-base font-semibold text-primary">{t('projects.general')}</h2>
              <p className="mt-1 text-sm text-tertiary">{t('projects.generalDescription')}</p>
            </div>
            <div className="flex flex-col gap-4">
              <Button color="secondary" className="self-start" onPress={() => { void discover(); }}>{t('projects.discoverRepositories')}</Button>
              {repositories.length > 0 && <label className="flex flex-col gap-2 text-sm text-secondary">{t('projects.accessibleRepository')}<select className="w-full rounded-lg border border-secondary bg-primary p-2 text-primary" value={repositories.some(repository => repository.repository === definition.repository) ? definition.repository : ''} onChange={event => { const selected = repositories.find(repository => repository.repository === event.target.value); if (selected) update(selected); }}><option value="">{t('projects.chooseRepository')}</option>{repositories.map((repository, index) => <option key={`${repository.repository}-${index}`} value={repository.repository}>{repository.repository}</option>)}</select></label>}
              {next && <Button color="secondary" className="self-start" onPress={() => { void discover(next); }}>{t('projects.moreRepositories')}</Button>}
              <p className="text-sm text-tertiary">{t('projects.discoveryUsesServerGitHubAuthenticationIfUnavailableEnterOwnerRepositoryManuallyAnd')}</p>
              <div className="flex items-end gap-2">
                <div className="min-w-0 flex-1">{text('repository', t('projects.repositoryOwnerRepository'), true, 200)}</div>
                <Button color="secondary" aria-label={t('projects.copyRepository')} onPress={() => { void copyRepository(); }}>
                  <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" width="16" height="16"><rect x="8" y="8" width="12" height="12" rx="2" stroke="currentColor" strokeWidth="1.8"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3" stroke="currentColor" strokeWidth="1.8"/></svg>
                </Button>
              </div>
              <p className="-mt-2 text-sm text-tertiary">{t('projects.repositoryHelper')}</p>
              {copyStatus && <p role="status" className="-mt-2 text-sm text-tertiary">{copyStatus}</p>}
              <div className="grid gap-4 sm:grid-cols-2">
                {text('name', t('projects.projectName'), true, 120)}
                {text('defaultBranch', t('projects.baseBranch'), true, 200)}
              </div>
              <div>
                <TextArea label={t('projects.description')} value={definition.description} maxLength={4000} onChange={description => update({ description })} />
                <p className="mt-2 text-sm text-tertiary">{t('projects.descriptionHelper')}</p>
              </div>
            </div>
          </section>

          <section aria-labelledby="project-execution-heading" className="rounded-xl border border-secondary bg-primary p-5 shadow-xs sm:p-6">
            <div className="mb-5">
              <h2 id="project-execution-heading" className="text-base font-semibold text-primary">{t('projects.executionAndDiscovery')}</h2>
              <p className="mt-1 text-sm text-tertiary">{t('projects.executionAndDiscoveryDescription')}</p>
            </div>
            <div className="flex flex-col gap-5">
              <div className="space-y-2">
                <label className="flex flex-col gap-2 text-sm font-medium text-secondary" htmlFor="project-concurrency">
                  {t('projects.projectConcurrency')}
                  <select id="project-concurrency" aria-describedby="project-concurrency-help" className="w-full rounded-lg border border-secondary bg-primary p-2 text-primary" value={definition.maxParallelTasks === null ? '' : String(definition.maxParallelTasks)} onChange={event => update({ maxParallelTasks: event.target.value === '' ? null : Number(event.target.value) })}>
                    <option value="">{t('projects.projectConcurrencyAutomatic')}</option>
                    {Array.from({ length: 8 }, (_, index) => index + 1).map(limit => <option key={limit} value={limit}>{limit}</option>)}
                  </select>
                </label>
                <p id="project-concurrency-help" className="text-sm text-tertiary">{t('projects.projectConcurrencyExplanation')}</p>
              </div>
              <Toggle accent="success" aria-label={t('projects.automaticDiscovery2')} label={t('projects.automaticDiscovery2')} hint={t('projects.discoveryToggleHelper')} isSelected={definition.automaticDiscovery?.enabled === true} onChange={enabled => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), enabled } })} />
              <p className="-mt-4 text-sm text-tertiary">{t('projects.discoveryCanQueueEligibleIssuesExecutionRequiresAnAuthorizedEligibleWorkerAnd')}</p>
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {(['intervalSeconds', 'pageSize', 'deadlineSeconds'] as const).map((key, index) => <div key={key}>
                  <NumberInput label={[t('projects.discoveryIntervalSeconds'), t('projects.maximumDiscoveryPageSize'), t('projects.cycleDeadlineSeconds')][index]} min={[30, 1, 10][index]} max={[86400, 100, 120][index]} value={String(definition.automaticDiscovery?.[key] ?? [300, 25, 120][index])} onChange={value => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { enabled: false, intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), [key]: Number(value) } })} isRequired />
                  <p className="mt-2 text-sm text-tertiary">{[t('projects.discoveryIntervalHelper'), t('projects.discoveryPageSizeHelper'), t('projects.discoveryDeadlineHelper')][index]}</p>
                </div>)}
              </div>
              <div className="grid gap-4 sm:grid-cols-2">
                {text('issueReadyLabel', t('projects.issueReadyLabel'), false, 100)}
                {text('issueBlockedLabel', t('projects.issueBlockedLabel'), false, 100)}
              </div>
              <p className="text-sm text-tertiary">{t('projects.changingPolicyDoesNotChangeGitHubLabelsOpenBlockedByDependenciesPrevent')}</p>
            </div>
          </section>

          {validationEditor}
          <section aria-labelledby="project-requirements-heading" className="rounded-xl border border-secondary bg-primary p-5 shadow-xs sm:p-6">
            <div className="mb-5">
              <h2 id="project-requirements-heading" className="text-base font-semibold text-primary">{t('projects.requirements')}</h2>
              <p className="mt-1 text-sm text-tertiary">{t('projects.requirementsHelper')}</p>
            </div>
            <div className="flex flex-col gap-3">
              {definition.requirements.map((requirement, index) => <div key={index} className="rounded-lg border border-secondary bg-secondary p-4">
                <div className="mb-4 flex items-center justify-between gap-3">
                  <div className="flex min-w-0 items-center gap-2">
                    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" width="18" height="18" className="shrink-0 text-tertiary"><path d="M12 3v5m0 8v5M3 12h5m8 0h5M5.64 5.64l3.54 3.54m5.64 5.64 3.54 3.54m0-12.72-3.54 3.54m-5.64 5.64-3.54 3.54" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/></svg>
                    <span className="truncate text-sm font-medium text-primary">{requirement.name || t('projects.requirementName')}</span>
                    <span className="rounded-full bg-primary px-2 py-0.5 text-xs font-medium text-secondary ring-1 ring-secondary">{requirement.type || t('projects.requirementType')}</span>
                  </div>
                  <Button color="tertiary" aria-label={`${t('projects.removeRequirement')} ${index + 1}`} onPress={() => update({ requirements: definition.requirements.filter((_, position) => position !== index) })}>
                    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" width="16" height="16"><path d="M4 7h16M10 11v6m4-6v6M6 7l1 14h10l1-14M9 7V4h6v3" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"/></svg>
                  </Button>
                </div>
                <div className="grid gap-4 sm:grid-cols-2">
                  {(['type', 'name', 'version', 'scope'] as const).map(key => <Input key={key} label={t('projects.requirementLabel', { number: index + 1, field: t(`projects.requirement${key.charAt(0).toUpperCase() + key.slice(1)}` as TranslationKey) })} value={requirement[key] ?? ''} isRequired={key === 'type' || key === 'name'} onChange={value => update({ requirements: definition.requirements.map((item, position) => position === index ? { ...item, [key]: value || (key === 'version' || key === 'scope' ? null : '') } : item) })} />)}
                </div>
              </div>)}
              <Button color="secondary" className="w-full justify-center border border-dashed border-secondary" isDisabled={definition.requirements.length >= 64} onPress={() => update({ requirements: [...definition.requirements, { type: 'runtime', name: '', version: null, scope: null }] })}>{t('projects.addRequirement')}</Button>
              <p className="text-sm text-tertiary">{t('projects.typesIncludeRuntimeToolServiceAuthenticationAndAgentProviderVersionsAcceptNumeric')}</p>
            </div>
          </section>

          <div className="flex justify-end border-t border-secondary pt-2">
            <Button color="tertiary" isDisabled={busy} onPress={() => setDiscard(true)}>{t('projects.discardDraft')}</Button>
          </div>
        </fieldset>
      </form>
    </FormDialog>
    {discard && <ConfirmationDialog isOpen title={t('projects.discardProjectDraft')} description={t('projects.unsavedConfigurationWillBeLostNoServerDefinitionIsChanged')} actionLabel={t('projects.discardDraft')} destructive onClose={() => setDiscard(false)} onSubmit={async () => { w.setDraft(undefined); }} />}
  </>;

  return <>
    <FormDialog isOpen={d.open} title={d.before ? t("projects.editProject") : t("projects.createProject")} description={t('projects.editorStep', { step: d.step, title: [t('projects.repository'), t("projects.essentialConfiguration"), t('projects.review')][d.step - 1] })} pending={busy} onClose={() => w.setDraft({ ...d, open: false })}>
      <form className="flex flex-col gap-4" onInvalidCapture={event => { const details = event.target instanceof HTMLElement ? event.target.closest("details") : null; if (details) details.open = true; }} onSubmit={event => {
        event.preventDefault();
        if (locked || busy) return;
        if (d.step === 1) w.setDraft({ ...d, step: 2 });
        else if (d.step === 2) void review();
        else void run(async () => { await w.save(d); });
      }}>
        <fieldset disabled={busy || locked} className="flex min-w-0 flex-col gap-4">
          {d.step === 1 && <>
            <Button color="secondary" onPress={() => { void discover(); }}>{t("projects.discoverRepositories")}</Button>
            {repositories.length > 0 && <label className="flex flex-col gap-2 text-sm text-secondary">{t("projects.accessibleRepository")}<select className="w-full rounded-lg border border-secondary bg-primary p-2" value={repositories.some(r => r.repository === definition.repository) ? definition.repository : ''} onChange={event => { const selected = repositories.find(r => r.repository === event.target.value); if (selected) update(selected); }}><option value="">{t("projects.chooseRepository")}</option>{repositories.map((r, index) => <option key={`${r.repository}-${index}`} value={r.repository}>{r.repository}</option>)}</select></label>}
            {next && <Button color="secondary" onPress={() => { void discover(next); }}>{t("projects.moreRepositories")}</Button>}
            <p className="text-sm text-tertiary">{t("projects.discoveryUsesServerGitHubAuthenticationIfUnavailableEnterOwnerRepositoryManuallyAnd")}</p>
            {text('repository', t("projects.repositoryOwnerRepository"), true)}
          </>}
          {d.step === 2 && <>
            {text('name', t("projects.projectName"), true, 120)}{text('defaultBranch', t("projects.baseBranch"), true, 200)}<TextArea label={t("projects.description")} value={definition.description} maxLength={4000} onChange={description => update({ description })} />
            <div className="space-y-2">
              <label className="flex flex-col gap-2 text-sm text-secondary" htmlFor="project-concurrency">
                {t('projects.projectConcurrency')}
                <select id="project-concurrency" aria-describedby="project-concurrency-help" className="w-full rounded-lg border border-secondary bg-primary p-2 text-primary" value={definition.maxParallelTasks === null ? '' : String(definition.maxParallelTasks)} onChange={event => update({ maxParallelTasks: event.target.value === '' ? null : Number(event.target.value) })}>
                  <option value="">{t('projects.projectConcurrencyAutomatic')}</option>
                  {Array.from({ length: 8 }, (_, index) => index + 1).map(limit => <option key={limit} value={limit}>{limit}</option>)}
                </select>
              </label>
              <p id="project-concurrency-help" className="text-sm text-tertiary">{t('projects.projectConcurrencyExplanation')}</p>
            </div>
            <Checkbox label={t("projects.enableAutomaticIssueDiscovery")} isSelected={definition.automaticDiscovery?.enabled === true} onChange={enabled => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), enabled } })} />
            <p className="text-sm text-tertiary">{t("projects.discoveryCanQueueEligibleIssuesExecutionRequiresAnAuthorizedEligibleWorkerAnd")}</p>
            {validationEditor}
            <AdvancedDisclosure title={t("projects.advancedDiscoveryAndExecutionRequirements")}>
              {(['intervalSeconds', 'pageSize', 'deadlineSeconds'] as const).map((key, index) => <NumberInput key={key} label={[t("projects.discoveryIntervalSeconds"), t("projects.maximumDiscoveryPageSize"), t("projects.cycleDeadlineSeconds")][index]} min={[30, 1, 10][index]} max={[86400, 100, 120][index]} value={String(definition.automaticDiscovery?.[key] ?? [300, 25, 120][index])} onChange={value => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { enabled: false, intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), [key]: Number(value) } })} isRequired />)}
              {text('issueReadyLabel', t("projects.issueReadyLabel"), false, 100)}{text('issueBlockedLabel', t("projects.issueBlockedLabel"), false, 100)}
              <p>{t("projects.changingPolicyDoesNotChangeGitHubLabelsOpenBlockedByDependenciesPrevent")}</p>
              <h3 className="font-semibold">{t("projects.typedWorkerRequirements")}</h3>
              {definition.requirements.map((r, index) => <div key={index} className="space-y-2 rounded-lg border border-secondary p-3">
                {(['type', 'name', 'version', 'scope'] as const).map(key => <Input key={key} label={t('projects.requirementLabel', { number: index + 1, field: t(`projects.requirement${key.charAt(0).toUpperCase() + key.slice(1)}` as TranslationKey) })} value={r[key] ?? ''} isRequired={key === 'type' || key === 'name'} onChange={value => update({ requirements: definition.requirements.map((r, position) => position === index ? { ...r, [key]: value || (key === 'version' || key === 'scope' ? null : '') } : r) })} />)}
                <Button color="secondary" onPress={() => update({ requirements: definition.requirements.filter((_, position) => position !== index) })}>{t("projects.removeRequirement")}{' '}{index + 1}</Button>
              </div>)}
              <p>{t("projects.typesIncludeRuntimeToolServiceAuthenticationAndAgentProviderVersionsAcceptNumeric")}</p>
              <Button color="secondary" isDisabled={definition.requirements.length >= 64} onPress={() => update({ requirements: [...definition.requirements, { type: 'runtime', name: '', version: null, scope: null }] })}>{t("projects.addRequirement")}</Button>
            </AdvancedDisclosure>
          </>}
          {d.step === 3 && <><p className="font-medium text-primary">{definition.name} · {definition.repository} · {definition.defaultBranch}</p><p className="whitespace-pre-wrap text-sm text-secondary">{definition.description || t("projects.noDescription")}</p><p className="text-sm text-secondary">{t("projects.projectConcurrency")}: {concurrencyLabel(definition.maxParallelTasks)}</p><p className="text-sm text-secondary">{t("projects.readyLabel")}{' '}{definition.issueReadyLabel || localizeText('none')}{' '}{t("projects.blockedLabel")}{' '}{definition.issueBlockedLabel || localizeText('none')}</p><p className="text-sm text-secondary">{t("projects.automaticDiscovery")}{' '}{definition.automaticDiscovery?.enabled ? localizeText('enabled') : localizeText('disabled')}{' '}{t("projects.interval")}{' '}{definition.automaticDiscovery?.intervalSeconds ?? 300}{t("projects.sPageSize")}{' '}{definition.automaticDiscovery?.pageSize ?? 25}{' '}{t("projects.deadline")}{' '}{definition.automaticDiscovery?.deadlineSeconds ?? 120}{t("projects.s")}</p><div className="text-sm text-secondary"><h3 className="font-semibold">{t('projects.validationCommands')}</h3>{definition.validationCommands === null ? <p>{t('projects.validationUseDefaults')}</p> : <ol className="list-decimal pl-5">{definition.validationCommands.map((command, index) => <li key={index} className="whitespace-pre-wrap break-words">{command}</li>)}</ol>}</div>{definition.requirements.map((r, index) => <p key={index} className="text-sm text-secondary">{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? t('projects.scope', { scope: r.scope }) : ''}</p>)}<Notice>{t("projects.repositoryAndBranchReadVerifiedWorkerCheckoutPushPermissionCodexExecutionReadiness")}</Notice></>}
        </fieldset>
        {error && <Notice error>{error}</Notice>}
        {locked && <Notice error>{t("projects.saveResultUncertainCloseAndChooseCheckSavedDefinitionBeforeAnotherWrite")}</Notice>}
        {w.conflict && <Notice error>{t("projects.revision")}{' '}{w.conflict.revision}{' '}{t("projects.isNowCurrentYourDraftWasRetained")}<Button color="secondary" isDisabled={locked} onPress={() => { const current = w.conflict; if (current) { w.setDraft({ before: current, definition: definitionOf(current), step: 1, open: true }); w.setConflict(undefined); setError(''); } }}>{t("projects.loadCurrentDefinition")}</Button></Notice>}
        <div className="flex flex-wrap justify-end gap-2">
          <Button autoFocus color="secondary" isDisabled={busy} onPress={() => w.setDraft({ ...d, open: false })}>{t("projects.close")}</Button>
          <Button color="secondary" isDisabled={busy || locked} onPress={() => setDiscard(true)}>{t("projects.discardDraft")}</Button>
          {d.step > 1 && <Button color="secondary" isDisabled={busy || locked} onPress={() => w.setDraft({ ...d, step: d.step - 1 })}>{t("projects.back")}</Button>}
          <Button type="submit" isDisabled={busy || locked || d.step === 3 && !d.reviewed}>{busy ? t("projects.checking") : d.step === 1 ? t('projects.next') : d.step === 2 ? t("projects.verifyAndReview") : t("projects.saveReviewedProject")}</Button>
        </div>
      </form>
    </FormDialog>
    {discard && <ConfirmationDialog isOpen title={t("projects.discardProjectDraft")} description={t("projects.unsavedConfigurationWillBeLostNoServerDefinitionIsChanged")} actionLabel={t("projects.discardDraft")} destructive onClose={() => setDiscard(false)} onSubmit={async () => { w.setDraft(undefined); }} />}
  </>;
}
function concurrencyLabel(limit: number | null) {
  return limit === null ? t('projects.projectConcurrencyAutomatic') : t(limit === 1 ? 'projects.projectConcurrencyOneExecution' : 'projects.projectConcurrencyExecutions', { count: limit });
}
