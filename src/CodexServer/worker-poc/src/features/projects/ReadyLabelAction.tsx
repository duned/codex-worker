import { useRef, useState } from 'react';
import { t, useLanguage } from '../../shared/i18n';
import { Button } from '../../untitled/components/base/buttons/button';
import type { Issue, Project } from './contracts';
import { useProjects } from './Workspace';

export function ReadyLabelAction({ project, issue }: { project: Project; issue: Issue }) {
  useLanguage();
  const w = useProjects(), guard = useRef(false);
  const [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [failed, setFailed] = useState(false);
  const label = project.issueReadyLabel;
  const available = !!label && issue.state.toLowerCase() === 'open' && !issue.labels.some(value => value.toLowerCase() === label.toLowerCase());
  async function apply() {
    if (!available || !label || guard.current || w.locked(project.id)) return;
    guard.current = true; setBusy(true); setMessage(''); setFailed(false);
    try {
      await w.submitIssue({ project, before: issue, change: { kind: 'label', label, applied: true }, open: false }, true);
      setMessage(t('projects.readyApplied', { label }));
    } catch {
      setFailed(true); setMessage(t('projects.readyFailed'));
    } finally { guard.current = false; setBusy(false); }
  }
  return <div className="space-y-2">
    {available && <Button size="sm" color="secondary" isDisabled={busy || w.locked(project.id)} onPress={() => { void apply(); }}>{t(busy ? 'projects.readyApplying' : 'projects.applyReady', { label: label ?? '' })}</Button>}
    {message && <p role={failed ? 'alert' : 'status'} className={failed ? 'text-sm text-error-primary' : 'text-sm text-secondary'}>{message}</p>}
  </div>;
}
