import { useRef, useState } from 'react';
import { useApiRead, useApiMutation, useRuntime, useSession } from '../../shared/api/session';
import { queryKeys } from '../../shared/api/runtime';
import { worker as validateWorker } from '../../shared/api/validation';
import type { WorkerObservation } from '../../shared/api/contracts';
import { t, useLanguage } from '../../shared/i18n';
import { Button } from '../../untitled/components/base/buttons/button';
import { workerUpdate } from './worker-update';
import { restartAvailable, restartStatusPaths } from './worker-restart-model.mjs';

export function WorkerRestart({ worker, onStatusChecked, onNotification }: { worker: WorkerObservation; onStatusChecked?: () => void | Promise<void>; onNotification?: (message: { text: string; failed: boolean } | null) => void }) {
  useLanguage();
  const runtime = useRuntime();
  const session = useSession();
  const [checkPending, setCheckPending] = useState(false);
  const [checkMessage, setCheckMessage] = useState<{ text: string; failed: boolean } | null>(null);
  const [requestMessage, setRequestMessage] = useState<{ text: string; failed: boolean } | null>(null);
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
    setRequestMessage(null);
    onNotification?.(null);
    try {
      const result = await mutation.mutateAsync({ operationId: id.current, restart: true });
      if (result?.restart) {
        const message = t(`workers.restartState.${result.state}`);
        const failed = result.state === 'failed';
        setRequestMessage({ text: message, failed });
        onNotification?.({ text: message, failed });
      }
    } catch {
      // An uncertain response keeps the mutation fence until a status check.
      const message = t('workers.restartOutcomeUncertain');
      setRequestMessage({ text: message, failed: true });
      onNotification?.({ text: message, failed: true });
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
    onNotification?.(null);
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
        const message = t('workers.restartStatusCheckSuccess', { operation: operationMessage, service: serviceMessage });
        setCheckMessage({ text: message, failed: false });
        onNotification?.({ text: message, failed: false });
      }
    } catch {
      if (runtime.snapshot().generation === generation) {
        const message = t('workers.restartStatusCheckFailure');
        setCheckMessage({ text: message, failed: true });
        onNotification?.({ text: message, failed: true });
      }
    } finally {
      checkGuard.current = false;
      if (runtime.snapshot().generation === generation) setCheckPending(false);
    }
  }

  const current = operation.data;
  const ready = restartAvailable(worker, current);
  const restartState = mutation.isPending ? t('workers.restartRequesting')
    : operation.fetching && current === undefined ? t('workers.restartCheckingAvailability')
    : operation.hasError && current === undefined ? t('workers.restartStatusUnknown')
      : current?.restart ? t(`workers.restartState.${current.state}`)
        : ready ? t('workers.restartReady') : t('workers.restartUnavailable');
  const actionDisabled = mutation.isPending || mutation.locked || operation.fetching || (operation.hasError && current === undefined) || !restartAvailable(worker, current);
  const statusCheckAvailable = Boolean(current?.restart || operation.hasError || mutation.isError || mutation.locked || requestMessage);
  return <section className="worker-editor-section" aria-label={t('workers.restart')}>
    <div className="worker-editor-section-heading"><h2>{t('workers.restart')}</h2></div>
    <div className="worker-editor-card">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0 flex-1 space-y-1">
          <p id="poc-restart-state" className="poc-worker-action-state" role="status" aria-live="polite">
            <span className={`poc-worker-card-dot ${ready && !operation.fetching && !operation.hasError ? 'tone-success' : 'tone-warning'}`} aria-hidden="true" />
            {restartState}
          </p>
          <p className="worker-editor-help">{t('workers.restartHelp')}</p>
          {requestMessage && <p className={`text-sm ${requestMessage.failed ? 'text-error-primary' : 'text-secondary'}`} role={requestMessage.failed ? 'alert' : 'status'} aria-live="polite">{requestMessage.text}</p>}
          {!requestMessage && mutation.isError && <p className="text-sm text-error-primary" role="alert">{t('workers.restartOutcomeUncertain')}</p>}
        </div>
        <Button color="primary" aria-describedby="poc-restart-state" isDisabled={actionDisabled} onPress={() => { void request(); }}>
          {mutation.isPending ? t('workers.restartRequesting') : t('workers.restart')}
        </Button>
      </div>
      {statusCheckAvailable && <div className="mt-4 flex flex-wrap items-start justify-between gap-4 border-t border-secondary pt-4">
        <div className="min-w-0 flex-1 space-y-1">
          <p className="text-sm font-semibold text-primary">{t('workers.checkRestartStatus')}</p>
          <p id="poc-restart-check-help" className="worker-editor-help">{t('workers.restartStatusCheckHelp')}</p>
          <p id="poc-restart-check-readonly" className="worker-editor-help">{t('workers.restartStatusCheckReadOnly')}</p>
          {checkPending && <p className="text-sm text-secondary" role="status" aria-live="polite">{t('workers.restartStatusChecking')}</p>}
          {checkMessage && <p className={`text-sm ${checkMessage.failed ? 'text-error-primary' : 'text-secondary'}`} role={checkMessage.failed ? 'alert' : 'status'} aria-live="polite">{checkMessage.text}</p>}
        </div>
        <Button color="secondary" aria-describedby="poc-restart-check-help poc-restart-check-readonly" isDisabled={checkPending || mutation.isPending || operation.fetching}
          onPress={() => { void checkStatus(); }}>{checkPending ? t('workers.restartStatusChecking') : t('workers.checkRestartStatus')}</Button>
      </div>}
    </div>
  </section>;
}
