import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useRuntime, useSession } from '../../shared/api/session';
import { ApiError, type Validator } from '../../shared/api/client';
import { ConfirmationDialog } from '../../shared/Dialogs';
import { Notice } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { t, useLanguage } from '../../shared/i18n';
import { execution, type Execution } from './model';

type Candidate = { projectId: string; serverExecutionId: string; workerExecutionId: string; integrationBase: string;
  kind: string; automaticRetryExhausted: boolean; preservedCommit?: string | null; refusalReason?: string | null };
type Preview = { executionId: string; workerId?: string; candidate?: Candidate; eligible: boolean; reason: string };
export const conflictPreview: Validator<Preview> = value => {
  if (!value || typeof value !== 'object') throw new Error('Invalid recovery preview.');
  const item = value as Preview, candidate = item.candidate;
  if (typeof item.executionId !== 'string' || typeof item.eligible !== 'boolean' || typeof item.reason !== 'string' ||
      item.workerId != null && typeof item.workerId !== 'string' ||
      candidate != null && (typeof candidate.projectId !== 'string' || typeof candidate.serverExecutionId !== 'string' ||
        typeof candidate.workerExecutionId !== 'string' || typeof candidate.integrationBase !== 'string' ||
        typeof candidate.kind !== 'string' || typeof candidate.automaticRetryExhausted !== 'boolean' ||
        candidate.preservedCommit != null && typeof candidate.preservedCommit !== 'string' ||
        candidate.refusalReason != null && typeof candidate.refusalReason !== 'string') ||
      item.eligible && (!item.workerId || !candidate || !candidate.preservedCommit || candidate.kind !== 'Integration'))
    throw new Error('Invalid recovery preview.');
  return item;
};

export function ManagedConflictRecovery({ item }: { item: Execution }) {
  useLanguage();
  const runtime = useRuntime(), session = useSession(), base = `/api/v1/executions/${encodeURIComponent(item.id)}`;
  const [preview, setPreview] = useState<Preview>(), [confirm, setConfirm] = useState(false), [pending, setPending] = useState(false);
  const [operationId] = useState(() => crypto.randomUUID().replaceAll('-', ''));
  const [submitted, setSubmitted] = useState(false), [result, setResult] = useState<Execution>(), [message, setMessage] = useState('');
  const path = `${base}/conflict-recovery`, locked = runtime.locked(path);
  async function inspect() {
    const generation = session.generation; setPending(true); setPreview(undefined);
    try {
      await runtime.reconcile(path, async signal => {
        if (submitted) {
          try {
            const current = await runtime.read(`/api/v1/executions/${operationId}`, signal, execution);
            if (current.retryOfExecutionId !== item.id) throw new Error('Recovery identity mismatch.');
            setResult(current); return;
          } catch (error) { if (!(error instanceof ApiError) || error.status !== 404) throw error; }
        }
        const current = await runtime.read(path, signal, conflictPreview);
        if (current.executionId !== item.id || current.candidate &&
            (current.candidate.projectId !== item.projectId || current.candidate.workerExecutionId !== item.workerExecutionId ||
              current.workerId !== item.assignedWorkerId)) throw new Error('Recovery preview identity mismatch.');
        setPreview(current);
      });
      setMessage('');
    } catch (error) { if (runtime.snapshot().generation === generation) setMessage(error instanceof Error ? error.message : t('conflicts.unavailable')); }
    finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  async function request() {
    if (!preview?.eligible || !preview.candidate || !preview.workerId) return;
    const generation = session.generation; setPending(true); setSubmitted(true);
    try {
      const current = await runtime.mutate(path, path, 'POST', { operationId, workerId: preview.workerId, candidate: preview.candidate }, execution);
      if (runtime.snapshot().generation === generation) { setResult(current); setConfirm(false); setPreview(undefined); }
    } catch (error) {
      if (runtime.snapshot().generation === generation) { setMessage(error instanceof Error ? error.message : t('conflicts.unavailable')); setConfirm(false); }
    } finally { if (runtime.snapshot().generation === generation) setPending(false); }
  }
  return <section aria-labelledby="managed-conflict-recovery" className="rounded-xl bg-primary p-5 shadow-xs ring-1 ring-secondary sm:p-6">
    <h2 id="managed-conflict-recovery" className="text-lg font-semibold text-primary">{t('conflicts.title')}</h2>
    <p className="mt-2 text-sm text-secondary">{t('conflicts.description')}</p>
    <div className="mt-4 flex flex-wrap gap-3">
      <Button color="secondary" isDisabled={pending} onPress={() => { void inspect(); }}>{t('conflicts.inspect')}</Button>
      {preview?.eligible && !result && <Button isDisabled={pending || locked} onPress={() => setConfirm(true)}>{t('conflicts.request')}</Button>}
    </div>
    {preview && <div className="mt-4"><Notice>{preview.reason}</Notice>{preview.candidate && <details className="mt-3 break-all text-sm text-secondary"><summary>{t('conflicts.evidence')}</summary>
      <p>{t('conflicts.worker')}: {preview.workerId}</p><p>{t('conflicts.execution')}: {preview.candidate.workerExecutionId}</p>
      <p>{t('conflicts.commit')}: {preview.candidate.preservedCommit}</p><p>{t('conflicts.base')}: {preview.candidate.integrationBase}</p>
      <p>{preview.candidate.automaticRetryExhausted ? t('conflicts.exhausted') : t('conflicts.automaticAvailable')}</p>
    </details>}</div>}
    {message && <div className="mt-4"><Notice>{message} {t('conflicts.refreshResult')}</Notice></div>}
    {result && <Link className="mt-4 block text-sm font-semibold text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(result.id)}`}>{t('conflicts.result')}</Link>}
    {confirm && <ConfirmationDialog isOpen title={t('conflicts.request')} description={t('conflicts.confirm')}
      actionLabel={t('conflicts.request')} onClose={() => setConfirm(false)} onSubmit={request} disabled={pending || locked || !preview?.eligible} />}
  </section>;
}
