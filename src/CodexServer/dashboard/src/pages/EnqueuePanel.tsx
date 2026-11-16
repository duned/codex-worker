import { useEffect, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import type { ProjectSummary } from '../shared/api/contracts';
import { useAction } from '../shared/actions';
import { t, useLanguage } from '../shared/i18n';
import { Field, inputClass, SidePanel } from '../shared/ui';

/** Queue one GitHub Issue for this project. Engine and effort come from the Issue's own metadata. */
export function EnqueuePanel({ open, onClose, project, issue }: { open: boolean; onClose(): void; project: ProjectSummary; issue?: number }) {
  useLanguage();
  const action = useAction(), [number, setNumber] = useState(issue ? String(issue) : '');
  useEffect(() => { if (open) setNumber(issue ? String(issue) : ''); }, [open, issue]);
  const valid = /^[1-9]\d{0,8}$/.test(number);
  return <SidePanel open={open} onClose={onClose} title={t('issues.enqueue')}
    footer={<><Button color="secondary" onClick={onClose}>{t('common.cancel')}</Button>
      <Button color="primary" isDisabled={!valid || !!action.busy} onClick={() => void action.run(`enqueue-${project.id}-${number}`,
        `/api/v1/projects/${encodeURIComponent(project.id)}/github/issues/${number}/enqueue`, 'POST', {}, 'actions.queued').then(done => { if (done) onClose(); })}>{t('issues.enqueueSubmit')}</Button></>}>
    <p className="text-sm text-tertiary">{t('issues.enqueueContext', { project: project.name, repository: project.repository })}</p>
    <Field label={t('issues.number')} hint={t('issues.numberHint')}><input className={inputClass} inputMode="numeric" value={number} onChange={event => setNumber(event.target.value.trim())} /></Field>
    <p className="text-sm text-tertiary">{t('issues.metadataHint')}</p>
  </SidePanel>;
}
