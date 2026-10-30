import { useRef, useState } from 'react';
import { useApiMutation, useApiRead, useRuntime } from '../../shared/api/session';
import { t } from '../../shared/i18n';
import { Checkbox } from '../../untitled/components/base/checkbox/checkbox';
import { Button } from '../../untitled/components/base/buttons/button';
import { workerUpdate } from './worker-update';
import type { WorkerObservation } from '../../shared/api/contracts';
import { recoveryAvailable, restartStatusPaths } from './worker-restart-model.mjs';

export function WorkerRecoveryPanel({ worker, onRecovered }: { worker: WorkerObservation; onRecovered?: () => void | Promise<void> }) {
  const operation = useApiRead(restartStatusPaths(worker.workerId).operation, workerUpdate);
  if (operation.data?.recovery ? !operation.data.recovery.schedulingBlocked : !worker.schedulingBlocker) return null;
  return <section className="worker-editor-section my-4" aria-label={t('workers.recoveryChecklist')}>
    <div className="worker-editor-card">
      {!operation.data?.recovery && <>
        <p role="status" className="worker-editor-help">{operation.hasError ? t('workers.recoveryStale') : t('workers.restartCheckingAvailability')}</p>
        <Button color="secondary" isDisabled={operation.fetching} onPress={() => { void operation.refetch(); }}>{t('workers.recoveryRefresh')}</Button>
      </>}
      <WorkerRecovery workerId={worker.workerId} operation={operation} onRecovered={onRecovered} />
    </div>
  </section>;
}

export function WorkerRecovery({ workerId, operation, onRecovered }: {
  workerId: string;
  operation: ReturnType<typeof useApiRead<ReturnType<typeof workerUpdate>>>;
  onRecovered?: () => void | Promise<void>;
}) {
  const runtime = useRuntime();
  const guard = useRef(false);
  const [nodeQuiescent, setNodeQuiescent] = useState(false);
  const [message, setMessage] = useState<string>();
  const retryId = useRef<{ source: string; id: string } | undefined>(undefined);
  const current = operation.data;
  const retry = Boolean(!current?.restart && current?.expectedVersion && current.expectedVersion !== current.recovery?.reportedVersion);
  const available = retry ? current?.state === 'failed' && current.recovery?.retryEligible && nodeQuiescent : recoveryAvailable(current, nodeQuiescent);
  const path = `/api/v1/workers/${encodeURIComponent(workerId)}/update/${retry ? 'retry' : 'reconcile'}?nodeQuiescent=true`;
  const mutation = useApiMutation(path, path, 'POST', workerUpdate);
  async function refresh() {
    await mutation.reconcile(async () => { await operation.refetch({ throwOnError: true }); });
    mutation.reset();
  }
  async function recover() {
    if (guard.current || !available || operation.hasError || operation.fetching || mutation.isPending || mutation.locked) return;
    guard.current = true;
    setMessage(undefined);
    setNodeQuiescent(false);
    try {
      if (retry && current && retryId.current?.source !== current.operationId) retryId.current = { source: current.operationId, id: crypto.randomUUID().replaceAll('-', '') };
      const result = await mutation.mutateAsync(retry ? { operationId: retryId.current?.id } : undefined);
      setMessage(result?.state === 'succeeded' ? t('workers.recoverySuccess') : result?.state === 'failed' ? t('workers.recoveryFailure') : t('workers.recoveryInProgress'));
      await runtime.queries.invalidateQueries();
      if (result?.state === 'succeeded') await onRecovered?.();
    } catch {
      setMessage(t('workers.recoveryFailure'));
      // Keep the uncertain mutation fence until an explicit authoritative refresh.
      await operation.refetch();
    } finally {
      guard.current = false;
    }
  }
  if (!current?.recovery?.schedulingBlocked) return null;
  return <div className="space-y-3">
    <h3 className="text-sm font-semibold text-primary">{t('workers.recoverySchedulingPaused')}</h3>
    <dl className="grid grid-cols-2 gap-2 text-sm text-secondary">
      <dt>{t('workers.recoveryOperationType')}</dt><dd>{t(current.restart ? 'workers.recoveryRestart' : 'workers.recoveryUpdate')}</dd>
      <dt>{t('workers.recoveryExpectedVersion')}</dt><dd>{current.expectedVersion ?? t('workers.recoveryUnavailable')}</dd>
      <dt>{t('workers.recoveryOriginalVersion')}</dt><dd>{current.originalVersion ?? t('workers.recoveryUnavailable')}</dd>
      <dt>{t('workers.recoveryReportedVersion')}</dt><dd>{current.recovery.reportedVersion ?? t('workers.recoveryUnavailable')}</dd>
    </dl>
    {current.restart && current.expectedVersion !== current.recovery.reportedVersion && <p className="worker-editor-help">{t('workers.recoveryRestartMismatch')}</p>}
    {retry && <p className="worker-editor-help">{t('workers.recoveryRetryHelp')}</p>}
    <p className="worker-editor-help">{t('workers.recoveryHelp', { state: current.state })}</p>
    {current.state === 'failed' && <p className="worker-editor-help">{current.diagnostic?.startsWith('timeout-') ? t('workers.recoveryUncertain') : t('workers.recoveryFailed')}</p>}
    <p className="worker-editor-help">{t('workers.recoveryChecked', { time: new Date(current.recovery.checkedAtUtc).toLocaleString() })}</p>
    {operation.hasError && <p role="alert" className="text-sm text-error-primary">{t('workers.recoveryStale')}</p>}
    <ul className="space-y-2 text-sm text-secondary" aria-label={t('workers.recoveryChecklist')}>
      {current.recovery.checks.map(check => <li key={check.code}>
        <span className="sr-only">{check.satisfied ? t('workers.recoveryMet') : t('workers.recoveryBlocked')}: </span>
        <span aria-hidden="true" className={check.satisfied ? 'text-success-primary' : 'text-warning-primary'}>{check.satisfied ? '✓' : '○'}</span>{' '}
        {check.description}{!check.satisfied && <p className="worker-editor-help">{check.nextStep}</p>}
      </li>)}
    </ul>
    <Checkbox label={t('workers.recoveryQuiescent')} isSelected={nodeQuiescent} onChange={setNodeQuiescent} />
    <div className="flex flex-wrap gap-3">
      <Button color="secondary" isDisabled={operation.fetching || mutation.isPending} onPress={() => { void refresh().catch(() => setMessage(t('workers.recoveryStale'))); }}>{t('workers.recoveryRefresh')}</Button>
      <Button color="primary" isLoading={mutation.isPending} isDisabled={!available || operation.hasError || operation.fetching || mutation.isPending || mutation.locked}
        onPress={() => { void recover(); }}>{t(retry ? 'workers.recoveryRetryAction' : 'workers.recoveryAction')}</Button>
    </div>
    {message && <p role="status" className="text-sm text-secondary">{message}</p>}
  </div>;
}
