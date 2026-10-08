import { t, useLanguage, type TranslationKey, localizeText } from '../../shared/i18n';
import { useEffect, useRef, useState } from 'react';
import { FormDialog, ConfirmationDialog } from '../../shared/Dialogs';
import { AdvancedDisclosure, Notice } from '../../shared/Presentation';
import { Input } from '../../shared/Input';
import { TextArea } from '../../shared/TextArea';
import { NumberInput } from '../../shared/NumberInput';
import { Button } from '../../untitled/components/base/buttons/button';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { useRuntime } from '../../shared/api/session';
import { repositoryPage, verification, type Definition, type Repository } from './contracts';
import { useProjects } from './Workspace';
import { definitionOf } from './model';
export function ProjectEditor() {
  useLanguage();
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
  const text = (key: 'name' | 'repository' | 'defaultBranch' | 'description' | 'issueReadyLabel' | 'issueBlockedLabel', label: string, required = false, maxLength?: number) => <Input label={label} value={definition[key] ?? ''} onChange={value => update({ [key]: value || (required || key === 'description' ? '' : null) })} isRequired={required} maxLength={maxLength} />;
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
            <Checkbox label={t("projects.enableAutomaticIssueDiscovery")} isSelected={definition.automaticDiscovery?.enabled === true} onChange={enabled => update({ automaticDiscovery: { ...(definition.automaticDiscovery ?? { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), enabled } })} />
            <p className="text-sm text-tertiary">{t("projects.discoveryCanQueueEligibleIssuesExecutionRequiresAnAuthorizedEligibleWorkerAnd")}</p>
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
          {d.step === 3 && <><p className="font-medium text-primary">{definition.name} · {definition.repository} · {definition.defaultBranch}</p><p className="whitespace-pre-wrap text-sm text-secondary">{definition.description || t("projects.noDescription")}</p><p className="text-sm text-secondary">{t("projects.readyLabel")}{' '}{definition.issueReadyLabel || localizeText('none')}{' '}{t("projects.blockedLabel")}{' '}{definition.issueBlockedLabel || localizeText('none')}</p><p className="text-sm text-secondary">{t("projects.automaticDiscovery")}{' '}{definition.automaticDiscovery?.enabled ? localizeText('enabled') : localizeText('disabled')}{' '}{t("projects.interval")}{' '}{definition.automaticDiscovery?.intervalSeconds ?? 300}{t("projects.sPageSize")}{' '}{definition.automaticDiscovery?.pageSize ?? 25}{' '}{t("projects.deadline")}{' '}{definition.automaticDiscovery?.deadlineSeconds ?? 120}{t("projects.s")}</p>{definition.requirements.map((r, index) => <p key={index} className="text-sm text-secondary">{r.type} · {r.name}{r.version ? ` ${r.version}` : ''}{r.scope ? t('projects.scope', { scope: r.scope }) : ''}</p>)}<Notice>{t("projects.repositoryAndBranchReadVerifiedWorkerCheckoutPushPermissionCodexExecutionReadiness")}</Notice></>}
        </fieldset>
        {error && <Notice error>{error}</Notice>}
        {locked && <Notice error>{t("projects.saveResultUncertainCloseAndChooseCheckSavedDefinitionBeforeAnotherWrite")}</Notice>}
        {w.conflict && (!d.before || w.conflict.id === d.before.id) && <Notice error>{t("projects.revision")}{' '}{w.conflict.revision}{' '}{t("projects.isNowCurrentYourDraftWasRetained")}<Button color="secondary" isDisabled={locked} onPress={() => { const current = w.conflict; if (current) { w.setDraft({ before: current, definition: definitionOf(current), step: 1, open: true }); w.setConflict(undefined); setError(''); } }}>{t("projects.loadCurrentDefinition")}</Button></Notice>}
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
