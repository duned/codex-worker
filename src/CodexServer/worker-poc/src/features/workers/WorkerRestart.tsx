import { useRef, useState } from 'react';
import { useApiRead, useApiMutation, useRuntime, useSession } from '../../shared/api/session';
import { queryKeys } from '../../shared/api/runtime';
import { worker as validateWorker } from '../../shared/api/validation';
import type { WorkerObservation } from '../../shared/api/contracts';
import { Header, MenuItem, MenuSection } from 'react-aria-components';
import { t, useLanguage } from '../../shared/i18n';
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
  return <MenuSection className="poc-worker-actions-section" aria-label={t('workers.restart')}>
    <Header className="poc-worker-menu-section-header">{t('workers.restart')}</Header>
    <MenuItem id="restart-worker" textValue={t('workers.restart')} aria-label={t('workers.restart')} aria-describedby="poc-restart-state poc-restart-help" isDisabled={actionDisabled}
      onAction={() => { void request(); }} className={({ isFocused, isFocusVisible }) => `poc-worker-menu-item${isFocused ? ' is-focused' : ''}${isFocusVisible ? ' is-focus-visible' : ''}`}>
      <span id="poc-restart-state" className={`poc-worker-menu-item-state ${ready && !operation.fetching && !operation.hasError ? 'is-ready' : 'is-unavailable'}`} role="status" aria-live="polite">
        <span className={`poc-worker-card-dot ${ready && !operation.fetching && !operation.hasError ? 'tone-success' : 'tone-warning'}`} aria-hidden="true" />
        {restartState}
      </span>
      <span className="poc-worker-menu-item-title">{mutation.isPending ? t('workers.restartRequesting') : t('workers.restart')}</span>
      <span id="poc-restart-help" className="poc-worker-menu-item-detail">{t('workers.restartHelp')}</span>
      {requestMessage && <span className={`poc-worker-menu-item-result${requestMessage.failed ? ' is-error' : ''}`} role={requestMessage.failed ? 'alert' : 'status'} aria-live="polite">{requestMessage.text}</span>}
      {!requestMessage && mutation.isError && <span className="poc-worker-menu-item-result is-error" role="alert">{t('workers.restartOutcomeUncertain')}</span>}
    </MenuItem>
    {statusCheckAvailable && <MenuItem id="check-restart-status" textValue={t('workers.checkRestartStatus')} aria-label={t('workers.checkRestartStatus')} aria-describedby="poc-restart-check-help poc-restart-check-readonly" isDisabled={checkPending || mutation.isPending || operation.fetching}
      onAction={() => { void checkStatus(); }} className={({ isFocused, isFocusVisible }) => `poc-worker-menu-item${isFocused ? ' is-focused' : ''}${isFocusVisible ? ' is-focus-visible' : ''}`}>
      <span className="poc-worker-menu-item-title">{t('workers.checkRestartStatus')}</span>
      <span id="poc-restart-check-help" className="poc-worker-menu-item-detail">{t('workers.restartStatusCheckHelp')}</span>
      <span id="poc-restart-check-readonly" className="poc-worker-menu-item-note">{t('workers.restartStatusCheckReadOnly')}</span>
      {checkPending && <span className="poc-worker-menu-item-result" role="status" aria-live="polite">{t('workers.restartStatusChecking')}</span>}
      {checkMessage && <span className={`poc-worker-menu-item-result${checkMessage.failed ? ' is-error' : ''}`} role={checkMessage.failed ? 'alert' : 'status'} aria-live="polite">{checkMessage.text}</span>}
    </MenuItem>}
  </MenuSection>;
}
