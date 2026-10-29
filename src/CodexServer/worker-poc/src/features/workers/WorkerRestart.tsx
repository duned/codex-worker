import { useRef, useState } from 'react';
import { useApiRead, useApiMutation, useRuntime } from '../../shared/api/session';
import { workers } from '../../shared/api/validation';
import type { WorkerObservation } from '../../shared/api/contracts';
import { Button } from '../../untitled/components/base/buttons/button';
import { Notice } from '../../shared/Presentation';
import { t, useLanguage } from '../../shared/i18n';
import { workerUpdate } from './worker-update';
import { restartAvailable, restartPending } from './worker-restart-model.mjs';

export function WorkerRestart({ worker }: { worker: WorkerObservation }) {
  useLanguage();
  const runtime = useRuntime();
  const [refreshError, setRefreshError] = useState(false);
  const path = `/api/v1/workers/${encodeURIComponent(worker.workerId)}/update`;
  const operation = useApiRead(path, workerUpdate);
  const id = useRef(crypto.randomUUID().replaceAll('-', ''));
  const guard = useRef(false);
  const mutation = useApiMutation(path, path, 'POST', workerUpdate, async signal => {
    const current = (await runtime.read('/api/v1/workers', signal, workers)).find(item => item.workerId === worker.workerId);
    const pending = await runtime.read(path, signal, workerUpdate);
    if (!restartAvailable(current, pending)) throw new Error(t('workers.restartUnavailable'));
  });
  async function request() {
    if (guard.current) return;
    if (operation.data?.state === 'succeeded') id.current = crypto.randomUUID().replaceAll('-', '');
    guard.current = true;
    try { await mutation.mutateAsync({ operationId: id.current, restart: true }); await operation.refetch(); }
    catch { /* Mutation state retains failure and uncertainty until explicit reconciliation. */ }
    finally { guard.current = false; }
  }
  async function refresh() {
    try {
      await mutation.reconcile(async signal => { await runtime.read(path, signal, workerUpdate); });
      await operation.refetch();
      setRefreshError(false);
    } catch { setRefreshError(true); }
  }
  const current = operation.data;
  return <Notice>
    <Button color="secondary" isDisabled={mutation.isPending || mutation.locked || operation.fetching || operation.hasError || !restartAvailable(worker, current)} onPress={() => { void request(); }}>{t('workers.restart')}</Button>
    <p>{t('workers.restartHelp')}</p>
    <p role="status">{current?.restart ? t(`workers.restartState.${current.state}`) : restartPending(current) ? t('workers.restartUnavailable') : ''}</p>
    {current?.restart && current.diagnostic && <p>{current.diagnostic}</p>}
    {(mutation.isError || operation.hasError || refreshError) && <p role="alert">{t('workers.restartUnavailable')}</p>}
    <Button color="secondary" onPress={() => { void refresh(); }}>{t('workers.restartRefresh')}</Button>
  </Notice>;
}
