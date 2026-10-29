import { useRef, useState } from 'react';
import { useApiRead, useApiMutation, useRuntime, useSession } from '../../shared/api/session';
import { queryKeys } from '../../shared/api/runtime';
import { worker as validateWorker } from '../../shared/api/validation';
import type { WorkerObservation } from '../../shared/api/contracts';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { t, useLanguage } from '../../shared/i18n';
import { workerUpdate } from './worker-update';
import { restartAvailable, restartStatusPaths } from './worker-restart-model.mjs';

export function WorkerRestart({ worker, onStatusChecked }: { worker: WorkerObservation; onStatusChecked?: () => void | Promise<void> }) {
  useLanguage();
  const runtime = useRuntime();
  const session = useSession();
  const [checkPending, setCheckPending] = useState(false);
  const [checkMessage, setCheckMessage] = useState<{ text: string; failed: boolean } | null>(null);
  const path = `/api/v1/workers/${encodeURIComponent(worker.workerId)}/update`;
  const paths = restartStatusPaths(worker.workerId);
  const operation = useApiRead(path, workerUpdate);
  const id = useRef(crypto.randomUUID().replaceAll('-', ''));
  const guard = useRef(false);
  const checkGuard = useRef(false);
  const mutation = useApiMutation(path, path, 'POST', workerUpdate);

  async function request() {
    if (guard.current || mutation.isPending || mutation.locked || !restartAvailable(worker, operation.data)) return;
    if (operation.data?.state === 'succeeded') id.current = crypto.randomUUID().replaceAll('-', '');
    guard.current = true;
    setCheckMessage(null);
    try {
      await mutation.mutateAsync({ operationId: id.current, restart: true });
    } catch {
      // An uncertain response keeps the mutation fence until a status check.
    } finally {
      guard.current = false;
    }
  }

  async function checkStatus() {
    if (checkGuard.current || mutation.isPending || operation.fetching) return;
    checkGuard.current = true;
    const generation = session.generation;
    setCheckPending(true);
    setCheckMessage(null);
    try {
      let latest: ReturnType<typeof workerUpdate> | undefined;
      let service: ReturnType<typeof validateWorker> | undefined;
      await mutation.reconcile(async signal => {
        [latest, service] = await Promise.all([
          runtime.read(paths.operation, signal, workerUpdate),
          runtime.read(paths.worker, signal, validateWorker)
        ]);
        if (runtime.snapshot().generation !== generation) return;
        runtime.queries.setQueryData(queryKeys.read(generation, paths.operation), latest);
        runtime.queries.setQueryData(queryKeys.read(generation, paths.worker), service);
        await onStatusChecked?.();
      });
      if (latest === undefined || !service) throw new Error('Restart status was incomplete.');
      mutation.reset();
      if (runtime.snapshot().generation === generation) {
        const operationMessage = latest?.restart ? t(`workers.restartState.${latest.state}`) : t('workers.restartStatusNoRequest');
        const serviceMessage = service.availability === 'online' ? t('workers.restartServiceOnline') : t('workers.restartServiceUnavailable');
        setCheckMessage({ text: t('workers.restartStatusCheckSuccess', { operation: operationMessage, service: serviceMessage }), failed: false });
      }
    } catch {
      if (runtime.snapshot().generation === generation) {
        setCheckMessage({ text: t('workers.restartStatusCheckFailure'), failed: true });
      }
    } finally {
      checkGuard.current = false;
      if (runtime.snapshot().generation === generation) setCheckPending(false);
    }
  }

  const current = operation.data;
  const ready = restartAvailable(worker, current);
  const restartState = operation.fetching && current === undefined ? t('workers.restartCheckingAvailability')
    : operation.hasError && current === undefined ? t('workers.restartStatusUnknown')
      : current?.restart ? t(`workers.restartState.${current.state}`)
        : ready ? t('workers.restartReady') : t('workers.restartUnavailable');
  const actionDisabled = mutation.isPending || mutation.locked || !restartAvailable(worker, current);
  return <TableCard.Root className="poc-dashboard-panel poc-control-panel poc-restart-panel">
    <div className="poc-panel-heading"><h2>{t('workers.restart')}</h2></div>
    <div className="poc-restart-content">
      <p className={`poc-restart-state ${ready && !operation.fetching && !operation.hasError ? 'is-ready' : 'is-unavailable'}`}>
        <span className={`poc-worker-card-dot ${ready && !operation.fetching && !operation.hasError ? 'tone-success' : 'tone-warning'}`} aria-hidden="true" />
        {restartState}
      </p>
      <p className="poc-restart-help">{t('workers.restartHelp')}</p>
      <Button color="primary" size="md" className="w-full" isDisabled={actionDisabled} onPress={() => { void request(); }}>
        {t('workers.restart')}
      </Button>
      {mutation.isError && <p className="poc-control-message is-error" role="alert">{t('workers.restartOutcomeUncertain')}</p>}
      <div className="poc-restart-check">
        <h3>{t('workers.restartStatusCheckHeading')}</h3>
        <p>{t('workers.restartStatusCheckHelp')}</p>
        <p>{t('workers.restartStatusCheckReadOnly')}</p>
        <Button color="secondary" size="md" className="w-full" isDisabled={checkPending || mutation.isPending || operation.fetching} onPress={() => { void checkStatus(); }}>
          {checkPending ? t('workers.restartStatusChecking') : t('workers.checkRestartStatus')}
        </Button>
        {checkMessage && <p className="poc-control-message" role={checkMessage.failed ? 'alert' : 'status'} aria-live="polite">{checkMessage.text}</p>}
      </div>
    </div>
  </TableCard.Root>;
}
