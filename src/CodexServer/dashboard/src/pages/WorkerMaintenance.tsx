import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { useAction } from '../shared/actions';
import { useApiRead } from '../shared/api/session';
import type { WorkerObservation } from '../shared/api/contracts';
import { workerRelease, workerUpdate } from '../shared/api/validation';
import { t, tryT, useLanguage } from '../shared/i18n';
import { isOnline, maintenanceBlocking, maintenanceMessage } from '../shared/model';
import { Ago, Card, Check, KeyValues, Notice, Pill } from '../shared/ui';

const operationId = () => crypto.randomUUID().replaceAll('-', '');

/** Restart and update for one Worker. The Server drains the Worker first, so running work always finishes. */
export function WorkerMaintenance({ worker, onProgress }: { worker: WorkerObservation; onProgress(): void }) {
  useLanguage();
  const base = `/api/v1/workers/${encodeURIComponent(worker.workerId)}`;
  const release = useApiRead(`${base}/release`, workerRelease), update = useApiRead(`${base}/update`, workerUpdate);
  const action = useAction();
  const [confirm, setConfirm] = useState<'restart' | 'update'>(), [quiescent, setQuiescent] = useState(false);
  const info = release.data, operation = update.data ?? undefined, online = isOnline(worker);
  const blocking = maintenanceBlocking(operation);
  const failed = operation?.state === 'failed' && !operation.drainReleasedAtUtc;
  // Recovery checks are only meaningful, and only read, while a failed operation still holds the drain.
  const recovery = useApiRead(`${base}/update/recovery`, workerUpdate, failed);
  const checks = failed ? recovery.data?.recovery : undefined;
  const busy = !!action.busy || update.loading;
  const canRestart = online && !!info?.restartSupported && !blocking, canUpdate = online && !!info?.updateAvailable && !!info.selfUpdateSupported && !blocking;
  const inProgress = !!operation && ['waiting-for-idle', 'updating', 'reconnecting'].includes(operation.state);
  const refetchUpdate = update.refetch;
  // Detail reads are not refreshed by the Server freshness markers; follow an operation until it settles.
  useEffect(() => {
    if (!inProgress) return;
    const id = setInterval(() => { void refetchUpdate(); onProgress(); }, 5000);
    return () => clearInterval(id);
  }, [inProgress, refetchUpdate, onProgress]);

  // The Server compares the latest release with the Worker's current version on every read, so a new
  // installed version or a settled operation only needs a fresh read, not a new release lookup.
  const refetchRelease = release.refetch, settled = `${worker.workerVersion}|${inProgress ? '' : operation?.state ?? ''}`;
  const lastSettled = useRef(settled);
  useEffect(() => {
    if (lastSettled.current === settled) return;
    lastSettled.current = settled;
    void refetchRelease();
  }, [settled, refetchRelease]);

  async function request(kind: 'restart' | 'update') {
    if (await action.run(`worker-update-${worker.workerId}`, `${base}/update`, 'POST', { operationId: operationId(), restart: kind === 'restart' },
      kind === 'restart' ? 'maintenance.restartRequested' : 'maintenance.updateRequested')) setConfirm(undefined);
  }
  const recover = (path: string, body: unknown, done: 'maintenance.drainReleased' | 'maintenance.updateRetried' | 'maintenance.reconciled') =>
    action.run(`worker-update-${worker.workerId}`, `${base}/update/${path}?nodeQuiescent=true`, 'POST', body, done).then(() => setQuiescent(false));

  const versionState = !info ? null : info.updateAvailable ? <Pill tone="warn">{t('maintenance.updateAvailable')}</Pill>
    : info.latestVersion ? <Pill tone="ok">{t('maintenance.upToDate')}</Pill> : <Pill tone="idle">{t('maintenance.unknown')}</Pill>;

  return <Card title={t('maintenance.title')} actions={<div className="flex flex-wrap gap-2">
    <Button size="sm" color="secondary" isDisabled={!canRestart || busy} onClick={() => setConfirm('restart')}>{t('maintenance.restart')}</Button>
    {info?.updateAvailable && <Button size="sm" color="primary" isDisabled={!canUpdate || busy} onClick={() => setConfirm('update')}>
      {t('maintenance.updateTo', { version: info.latestVersion ?? '' })}</Button>}
  </div>}>
    <KeyValues items={[
      [t('maintenance.installed'), worker.workerVersion ? `v${worker.workerVersion}` : '—'],
      [t('maintenance.latest'), info?.latestVersion ? <span className="flex flex-wrap items-center gap-2">v{info.latestVersion} {versionState}</span>
        : info?.diagnostic ? t('maintenance.latestUnavailable') : release.loading ? t('common.loading') : '—'],
      ...(info?.checkedAtUtc ? [[t('maintenance.checked'), <Ago value={info.checkedAtUtc} />] as [string, ReactNode]] : [])
    ]} />

    {confirm && <div className="mt-4 rounded-lg bg-secondary p-4">
      <p className="text-sm text-secondary">{t(confirm === 'restart' ? 'maintenance.confirmRestart' : 'maintenance.confirmUpdate', { version: info?.latestVersion ?? '' })}</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <Button size="sm" color="primary" isDisabled={busy} onClick={() => void request(confirm)}>{t(confirm === 'restart' ? 'maintenance.confirmRestartButton' : 'maintenance.confirmUpdateButton')}</Button>
        <Button size="sm" color="secondary" onClick={() => setConfirm(undefined)}>{t('common.cancel')}</Button>
      </div>
    </div>}

    {operation && <div className="mt-4"><Notice tone={operation.state === 'failed' ? 'bad' : operation.state === 'succeeded' ? 'ok' : 'run'}>
      {t(maintenanceMessage(operation), { version: operation.expectedVersion ?? '' })}
      {operation.requestedAtUtc && <span className="text-tertiary"> · <Ago value={operation.requestedAtUtc} /></span>}
    </Notice></div>}

    {failed && <div className="mt-4 flex flex-col gap-3">
      {checks?.checks.filter(check => !check.satisfied).map(check => <p key={check.code} className="text-sm text-secondary">
        <span className="font-medium text-primary">{tryT(`maintenance.check.${check.code}`, check.description)}</span> {tryT(`maintenance.check.${check.code}.todo`, check.nextStep)}</p>)}
      <Check checked={quiescent} onChange={setQuiescent}>{t('maintenance.quiescent')}</Check>
      <div className="flex flex-wrap gap-2">
        {operation.restart && <Button size="sm" color="secondary" isDisabled={!quiescent || busy || !checks?.releaseDrainEligible}
          onClick={() => void recover('release-drain', { operationId: operation.operationId, restart: true }, 'maintenance.drainReleased')}>{t('maintenance.releaseDrain')}</Button>}
        {!operation.restart && <Button size="sm" color="secondary" isDisabled={!quiescent || busy || !checks?.retryEligible}
          onClick={() => void recover('retry', { operationId: operationId(), restart: false }, 'maintenance.updateRetried')}>{t('maintenance.retryUpdate')}</Button>}
        <Button size="sm" color="secondary" isDisabled={!quiescent || busy || !checks?.eligible}
          onClick={() => void recover('reconcile', {}, 'maintenance.reconciled')}>{t('maintenance.reconcile')}</Button>
      </div>
    </div>}
    {!online && <p className="mt-4 text-sm text-tertiary">{t('maintenance.offline')}</p>}
    {online && info && !info.restartSupported && <p className="mt-4 text-sm text-tertiary">{t('maintenance.restartNotEnabled')}</p>}
    {online && info?.updateAvailable && !info.selfUpdateSupported && <p className="mt-4 text-sm text-tertiary">{t('maintenance.updateNotEnabled')}</p>}
  </Card>;
}
