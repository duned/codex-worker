import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { useRuntime } from '../shared/api/session';
import { project as validateProject } from '../shared/api/validation';
import type { ProjectSummary } from '../shared/api/contracts';
import { toast } from '../shared/actions';
import { t, useLanguage } from '../shared/i18n';
import { Check, Field, inputClass, Notice, Select, SidePanel } from '../shared/ui';

/** Create or edit a project. Editing sends the revision it started from, so a concurrent change is rejected, not overwritten. */
export function ProjectPanel({ open, onClose, project }: { open: boolean; onClose(): void; project?: ProjectSummary }) {
  useLanguage();
  const runtime = useRuntime(), navigate = useNavigate();
  const blank = () => ({
    name: project?.name ?? '', repository: project?.repository ?? '', defaultBranch: project?.defaultBranch ?? 'main', description: project?.description ?? '',
    parallel: String(project?.maxParallelTasks ?? 1), validation: (project?.validationCommands ?? []).join('\n'),
    readyLabel: project?.issueReadyLabel ?? 'codex-ready', automatic: project?.automaticDiscovery?.enabled ?? false,
    integration: project?.integrationMode ?? 'direct'
  });
  const [form, setForm] = useState(blank), [error, setError] = useState(''), [saving, setSaving] = useState(false);
  useEffect(() => { if (open) { setForm(blank()); setError(''); } }, [open, project?.id, project?.revision]);
  const set = (key: keyof ReturnType<typeof blank>) => (value: string | boolean) => setForm(current => ({ ...current, [key]: value }));
  async function save() {
    const definition = {
      name: form.name.trim(), repository: form.repository.trim(), defaultBranch: form.defaultBranch.trim(), description: form.description.trim(),
      requirements: project?.requirements ?? [], issueReadyLabel: form.readyLabel.trim() || null, issueBlockedLabel: project?.issueBlockedLabel ?? null,
      automaticDiscovery: { ...(project?.automaticDiscovery ?? { intervalSeconds: 300, pageSize: 25, deadlineSeconds: 120 }), enabled: form.automatic },
      maxParallelTasks: Number(form.parallel) || 1,
      validationCommands: form.validation.split('\n').map(line => line.trim()).filter(Boolean),
      integrationMode: form.integration === 'pull-request' ? 'pull-request' : null
    };
    setSaving(true); setError('');
    try {
      const saved = project
        ? await runtime.mutate(`project-${project.id}`, `/api/v1/projects/${encodeURIComponent(project.id)}`, 'PUT', { definition, expectedRevision: project.revision }, validateProject)
        : await runtime.mutate('project-new', '/api/v1/projects', 'POST', definition, validateProject);
      toast(t(project ? 'projects.saved' : 'projects.created')); onClose();
      if (!project) navigate(`/projects/${encodeURIComponent(saved.id)}`);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : t('common.actionFailed'));
      try { await runtime.reconcile(project ? `project-${project.id}` : 'project-new', async () => { await runtime.queries.invalidateQueries(); }); } catch { /* keep lock */ }
    } finally { setSaving(false); }
  }
  return <SidePanel open={open} onClose={onClose} title={t(project ? 'projects.edit' : 'projects.add')}
    footer={<><Button color="secondary" onClick={onClose}>{t('common.cancel')}</Button><Button color="primary" isDisabled={saving || !form.name.trim() || !form.repository.trim()} onClick={() => void save()}>{t('common.save')}</Button></>}>
    <Field label={t('projects.name')}><input className={inputClass} value={form.name} onChange={event => set('name')(event.target.value)} /></Field>
    <Field label={t('projects.repository')} hint={t('projects.repositoryHint')}><input className={inputClass} value={form.repository} placeholder="owner/repository" onChange={event => set('repository')(event.target.value)} /></Field>
    <Field label={t('projects.baseBranch')} hint={t('projects.baseBranchHint')}><input className={inputClass} value={form.defaultBranch} onChange={event => set('defaultBranch')(event.target.value)} /></Field>
    <Field label={t('projects.parallel')} hint={t('projects.parallelHint')}><input className={inputClass} type="number" min={1} max={8} value={form.parallel} onChange={event => set('parallel')(event.target.value)} /></Field>
    <Field label={t('projects.validation')} hint={t('projects.validationHint')}><textarea className={`${inputClass} min-h-20 font-mono text-sm`} value={form.validation} onChange={event => set('validation')(event.target.value)} /></Field>
    <Select label={t('projects.integration')} hint={t('projects.integrationHint')} value={form.integration} onChange={set('integration')}
      options={[{ value: 'direct', label: t('projects.integrationDirect') }, { value: 'pull-request', label: t('projects.integrationPullRequest') }]} />
    <Field label={t('projects.readyLabel')} hint={t('projects.readyLabelHint')}><input className={inputClass} value={form.readyLabel} onChange={event => set('readyLabel')(event.target.value)} /></Field>
    <Check checked={form.automatic} onChange={set('automatic')}>{t('projects.automatic', { label: form.readyLabel || 'codex-ready' })}</Check>
    <Field label={t('projects.descriptionField')}><textarea className={`${inputClass} min-h-16`} value={form.description} onChange={event => set('description')(event.target.value)} /></Field>
    {error && <Notice tone="bad">{error}</Notice>}
  </SidePanel>;
}
