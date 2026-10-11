import { useRef, useState, type FormEvent } from 'react';
import { ApiError } from '../../shared/api/client';
import { worker as validateWorker } from '../../shared/api/validation';
import type { WorkerAdministration, WorkerReadiness } from '../../shared/api/contracts';
import { queryKeys } from '../../shared/api/runtime';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { FormDialog } from '../../shared/Dialogs';
import { Input } from '../../shared/Input';
import { NumberInput } from '../../shared/NumberInput';
import { Notice, StatusBadge } from '../../shared/Presentation';
import { t, useLanguage, localizeText } from '../../shared/i18n';
import { statusColor } from '../../shared/status-model.mjs';
import { Button } from '../../untitled/components/base/buttons/button';
import { saveWorkerSettings } from './worker-settings';
import { workerUpdate } from './worker-update';
import { workerConfiguration, workerVersionState, workerUpdateBlockReason } from './worker-editor-model.mjs';

interface WorkerEditorProps {
  worker: WorkerAdministration;
  diagnostics?: WorkerReadiness;
  diagnosticsLoading?: boolean;
  diagnosticsError?: boolean;
  serverVersion?: string;
  serverVersionLoading?: boolean;
  onClose(): void;
}

const versionLabels = {
  current: 'workers.editorVersionCurrent',
  available: 'workers.editorVersionAvailable',
  ahead: 'workers.editorVersionAhead',
  unavailable: 'workers.editorVersionUnavailable',
  loading: 'workers.editorVersionLoading',
  'in-progress': 'workers.editorUpdateInProgress',
  failed: 'workers.editorUpdateFailed'
} as const;
const statusTones = ['gray', 'success', 'warning', 'error', 'info', 'purple'] as const;
function reportedTone(value: string | undefined) {
  return statusTones.find(tone => tone === statusColor(value)) ?? 'gray';
}

export function WorkerEditor({ worker, diagnostics, diagnosticsLoading, diagnosticsError, serverVersion, serverVersionLoading, onClose }: WorkerEditorProps) {
  useLanguage();
  const runtime = useRuntime(), session = useSession();
  const path = `/api/v1/workers/${encodeURIComponent(worker.workerId)}`;
  const resource = path;
  const [baseline, setBaseline] = useState(() => workerConfiguration(worker));
  const [displayName, setDisplayName] = useState(baseline.displayName);
  const [capacity, setCapacity] = useState(baseline.maximumParallelExecutions);
  const [slots, setSlots] = useState(baseline.workerExecutionSlots);
  const [revision, setRevision] = useState(worker.configurationRevision ?? 1);
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshRequired, setRefreshRequired] = useState(false);
  const [error, setError] = useState('');
  const [updateMessage, setUpdateMessage] = useState('');
  const [updateBusy, setUpdateBusy] = useState(false);
  const updateGuard = useRef(false);
  const updateId = useRef(crypto.randomUUID().replaceAll('-', ''));
  const updateRead = useApiRead(`${path}/update`, workerUpdate);
  const update = updateRead.data;
  const updatePending = update?.state === 'waiting-for-idle' || update?.state === 'updating' || update?.state === 'reconnecting';
  const updateBlock = workerUpdateBlockReason(worker, { busy: updateBusy || busy, pending: updatePending, loading: updateRead.loading, error: updateRead.hasError, failed: update?.state === 'failed', locked: runtime.locked(`${path}/update`) });
  const guard = useRef(false);
  const body = useRef<HTMLFieldSetElement | null>(null);
  const locked = runtime.locked(resource);
  const hasCurrentCapacity = Number.isInteger(worker.maximumCapacity) && Number(worker.maximumCapacity) >= 1;
  const version = workerVersionState(worker, serverVersion, serverVersionLoading);
  const currentName = displayName.trim();
  const currentCapacity = Number(capacity);
  const validCapacity = Number.isInteger(currentCapacity) && currentCapacity >= 1 && currentCapacity <= 8;
  const validSlots = Number.isInteger(Number(slots)) && Number(slots) >= 1 && Number(slots) <= 8;
  const dirty = slots !== baseline.workerExecutionSlots || currentName !== baseline.displayName.trim() || capacity !== baseline.maximumParallelExecutions;
  const canSubmit = dirty && !busy && !locked && !refreshRequired && !refreshing;

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (guard.current || !canSubmit || !currentName || currentName.length > 200 || !validCapacity || !validSlots) return;
    guard.current = true;
    setBusy(true);
    setError('');
    try {
      await saveWorkerSettings(runtime, worker.workerId, currentName, currentCapacity, revision, Number(slots));
      onClose();
    } catch (failure) {
      const preflightConflict = failure instanceof ApiError && (failure.status === 409 || failure.message === t('workers.editorRevisionConflict'));
      const uncertain = runtime.locked(resource);
      setRefreshRequired(preflightConflict || uncertain);
      const status = failure instanceof ApiError ? failure.status : undefined;
      setError(preflightConflict ? t('workers.editorRevisionConflict')
        : status === 400 ? t('workers.editorValidationFailed')
          : status === 401 || status === 403 ? t('workers.editorAuthorizationFailed')
            : status === 404 ? t('workers.editorWorkerMissing')
              : uncertain ? t('workers.editorSaveUnconfirmed') : t('workers.editorSaveFailed'));
      if (body.current) body.current.scrollTo({ top: 0 });
    } finally {
      guard.current = false;
      setBusy(false);
    }
  }

  async function refreshConfiguration() {
    if (refreshing || busy) return;
    setRefreshing(true);
    setError('');
    try {
      let current: WorkerAdministration | undefined;
      await runtime.reconcile(resource, async signal => {
        current = await runtime.read(path, signal, validateWorker);
        if (current.workerId !== worker.workerId) throw new ApiError(t('workers.editorWorkerIdentityChanged'));
      });
      if (!current) throw new Error('Worker configuration was not returned.');
      const form = workerConfiguration(current);
      setDisplayName(form.displayName);
      setCapacity(form.maximumParallelExecutions);
      setSlots(form.workerExecutionSlots);
      setBaseline(form);
      setRevision(current.configurationRevision ?? 1);
      setRefreshRequired(false);
      setError('');
      runtime.queries.setQueryData(queryKeys.read(session.generation, path), current);
    } catch {
      setError(t('workers.editorRefreshFailed'));
    } finally { setRefreshing(false); }
  }

  async function requestUpdate() {
    if (updateGuard.current || updateBlock || version.state !== 'available') return;
    updateGuard.current = true;
    setUpdateBusy(true);
    setUpdateMessage('');
    try {
      const current = await runtime.mutate(`${path}/update`, `${path}/update`, 'POST', { operationId: updateId.current }, workerUpdate,
        async signal => {
          const currentWorker = await runtime.read(path, signal, validateWorker);
          if (currentWorker.workerId !== worker.workerId || workerUpdateBlockReason(currentWorker))
            throw new ApiError(t('workers.editorUpdateUnavailable'));
        });
      if (!current || current.workerId !== worker.workerId) throw new Error('Unexpected Worker update.');
      runtime.queries.setQueryData(queryKeys.read(session.generation, `${path}/update`), current);
    } catch {
      setUpdateMessage(t('workers.editorUpdateRequestFailed'));
    } finally { updateGuard.current = false; setUpdateBusy(false); }
  }

  async function refreshUpdate() {
    try {
      await runtime.reconcile(`${path}/update`, async signal => {
        const current = await runtime.read(`${path}/update`, signal, workerUpdate);
        if (current && current.workerId !== worker.workerId) throw new Error('Unexpected Worker update.');
        runtime.queries.setQueryData(queryKeys.read(session.generation, `${path}/update`), current);
      });
      setUpdateMessage('');
    } catch { setUpdateMessage(t('workers.editorUpdateRequestFailed')); }
  }

  const close = () => { if (!busy) onClose(); };
  const workerOccupancy = Number.isInteger(worker.activeExecutions) && Number(worker.activeExecutions) >= 0
    ? t('workers.editorOccupiedCount', { count: Number(worker.activeExecutions) }) : t('workers.editorOccupiedUnknown');
  const assignmentOccupancy = Number.isInteger(worker.activeAssignments) && Number(worker.activeAssignments) >= 0
    ? t('workers.editorServerAssignments', { count: Number(worker.activeAssignments) }) : t('workers.editorAssignmentsUnknown');
  const provisioning = diagnostics?.provisioningState;
  const provisioningValue = diagnosticsLoading ? t('workers.editorProvisioningLoading')
    : diagnosticsError ? t('workers.editorProvisioningUnavailable')
      : worker.availability === 'stale' ? t('workers.editorProvisioningStale')
        : provisioning ?? t('workers.editorProvisioningUnavailable');
  const provisioningTone = diagnosticsLoading || diagnosticsError || worker.availability === 'stale' || !provisioning
    ? 'gray' : reportedTone(provisioning);
  const versionTone = version.state === 'current' ? 'success' : version.state === 'available' || version.state === 'in-progress' ? 'warning'
    : version.state === 'failed' ? 'error' : version.state === 'ahead' ? 'info' : 'gray';

  return <FormDialog isOpen title={t('workers.editorTitle')} description={t('workers.editorDescription')} onClose={close}
    pending={busy} variant="side-panel" headerActions={<div className="worker-editor-header-actions">
      <Button color="tertiary" aria-label={t('shared.closeDialog')} isDisabled={busy} onPress={close}>
        <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" width="20" height="20"><path d="M6 6l12 12M18 6 6 18" stroke="currentColor" strokeWidth="2" strokeLinecap="round" /></svg>
      </Button>
      <Button color="secondary" isDisabled={busy} onPress={close}>{t('workers.editorCancel')}</Button>
      <Button type="submit" form="worker-edit-form" color="primary" className="bg-success-solid hover:bg-green-700" isDisabled={!canSubmit}>{busy ? t('workers.editorSaving') : t('workers.editorSaveChanges')}</Button>
    </div>}>
    <form id="worker-edit-form" className="worker-editor-form" onInvalidCapture={() => { body.current?.scrollTo({ top: 0 }); }} onSubmit={event => { void save(event); }}>
      <fieldset ref={body} disabled={busy} className="worker-editor-body">
        <legend className="sr-only">{t('workers.editorTitle')}</legend>
        {error && <Notice error>{error}{refreshRequired && <Button color="secondary" isDisabled={refreshing || busy} onPress={() => { void refreshConfiguration(); }}>{refreshing ? t('workers.editorRefreshing') : t('workers.editorRefreshCurrent')}</Button>}</Notice>}

        <section aria-labelledby="worker-editor-general" className="worker-editor-section">
          <div className="worker-editor-section-heading"><h2 id="worker-editor-general">{t('workers.editorGeneral')}</h2><p>{t('workers.editorGeneralDescription')}</p></div>
          <div className="worker-editor-card">
            <Input label={t('workers.editorWorkerName')} value={displayName} onChange={setDisplayName} isRequired maxLength={200} pattern=".*\S.*" />
            <p className="worker-editor-help">{t('workers.editorNameHelp')}</p>
          </div>
        </section>

        <section aria-labelledby="worker-editor-capacity" className="worker-editor-section">
          <div className="worker-editor-section-heading"><h2 id="worker-editor-capacity">{t('workers.editorExecutionCapacity')}</h2><p>{t('workers.editorCapacityDescription')}</p></div>
          <div className="worker-editor-card worker-editor-capacity-card">
            <div className="worker-editor-capacity-input">
              <NumberInput label={t('workers.editorMaximumParallelExecutions')} value={capacity} min={1} max={8} isRequired onChange={setCapacity} />
              <p className="worker-editor-help">{t('workers.editorCapacityHelp')}</p>
            </div>
            <div className="worker-editor-capacity-input">
              <NumberInput label={t('workers.editorExecutionSlots')} value={slots} min={1} max={8} isRequired onChange={setSlots} />
              <p className="worker-editor-help">{t('workers.editorSlotsHelp')}</p>
              {worker.workerExecutionSlots != null && worker.workerExecutionSlots !== worker.maximumCapacity && <p role="status">{t('workers.editorSlotsPending')}</p>}
            </div>
            <dl className="worker-editor-context">
              <div><dt>{t('workers.editorCurrentOccupancy')}</dt><dd>{workerOccupancy}</dd></div>
              <div><dt>{t('workers.editorWorkerReportedCapacity')}</dt><dd>{hasCurrentCapacity ? t('workers.editorCapacityCount', { count: Number(worker.maximumCapacity) }) : t('workers.editorValueUnknown')}</dd></div>
              <div><dt>{t('workers.editorServerAssignmentsLabel')}</dt><dd>{assignmentOccupancy}</dd></div>
            </dl>
          </div>
        </section>

        <section aria-labelledby="worker-editor-provisioning" className="worker-editor-section">
          <div className="worker-editor-section-heading"><h2 id="worker-editor-provisioning">{t('workers.editorProvisioning')}</h2><p>{t('workers.editorProvisioningDescription')}</p></div>
          <div className="worker-editor-card worker-editor-provisioning-card">
            <div><p className="worker-editor-overline">{t('workers.editorCurrentState')}</p><p className="worker-editor-primary-value">{localizeText(provisioningValue)}</p></div>
            <StatusBadge tone={provisioningTone} withDot>{localizeText(provisioningValue)}</StatusBadge>
            <dl className="worker-editor-context worker-editor-provisioning-context">
              <div><dt>{t('workers.editorHost')}</dt><dd>{worker.platform ?? t('workers.editorValueUnknown')}</dd></div>
              <div><dt>{t('workers.editorProfile')}</dt><dd>{t('workers.editorProfileUnavailable')}</dd></div>
            </dl>
          </div>
          <p className="worker-editor-help">{t('workers.editorProvisioningDetailHelp')}</p>
        </section>

        <section aria-labelledby="worker-editor-version" className="worker-editor-section">
          <div className="worker-editor-section-heading"><h2 id="worker-editor-version">{t('workers.editorWorkerVersion')}</h2><p>{t('workers.editorVersionDescription')}</p></div>
          <div className="worker-editor-card worker-editor-version-card">
            <div className="worker-editor-version-heading"><div><p className="worker-editor-overline">{t('workers.editorVersionComparison')}</p><p className="worker-editor-help">{t(versionLabels[version.state])}</p></div><StatusBadge tone={versionTone} withDot>{t(versionLabels[version.state])}</StatusBadge></div>
            <div className="worker-editor-version-values">
              <div><p className="worker-editor-overline">{t('workers.editorThisWorker')}</p><strong>{version.workerVersion ?? worker.workerVersion ?? t('workers.editorValueUnknown')}</strong><span>{t('workers.editorInstalled')}</span></div>
              <span className="worker-editor-version-arrow" aria-hidden="true">→</span>
              <div><p className="worker-editor-overline">{t('workers.editorServer')}</p><strong>{version.serverVersion ?? serverVersion ?? t('workers.editorValueUnknown')}</strong><span>{t('workers.editorAvailableVersion')}</span></div>
            </div>
            {version.state === 'available' && <>
              <Notice>{t('workers.editorUpdateAvailableHelp')}</Notice>
              <Button color="primary" className="bg-success-solid hover:bg-green-700" aria-describedby="worker-update-help" isDisabled={Boolean(updateBlock)} onPress={() => { void requestUpdate(); }}>{t('workers.editorUpdateWorker')}</Button>
              <p id="worker-update-help" role="status" className="worker-editor-help">{updateBlock ? t(`workers.editorUpdateBlock.${updateBlock}`) : t('workers.editorUpdateRecoveryHelp')}</p>
            </>}
            {update && <Notice error={update.state === 'failed'}><span role="status">{update.state === 'succeeded' && update.diagnostic === 'already-current'
              ? t('workers.editorRemoteUpdate.alreadyCurrent') : t(`workers.editorRemoteUpdate.${update.state}`)}</span></Notice>}
            {(updateMessage || updateRead.hasError) && <Notice error>{updateMessage || t('workers.editorUpdateUnavailable')}<Button color="secondary" onPress={() => { void refreshUpdate(); }}>{t('workers.editorRefreshCurrent')}</Button></Notice>}
            {version.state === 'in-progress'  && <Notice>{t('workers.editorUpdateProgressHelp', { state: localizeText(version.lifecycle) })}</Notice>}
            {version.state === 'failed' && <Notice error>{t('workers.editorUpdateFailureHelp')}</Notice>}
            {version.state === 'ahead' && <Notice>{t('workers.editorWorkerAheadHelp')}</Notice>}
            {version.state === 'loading' && <Notice>{t('workers.editorVersionLoadingHelp')}</Notice>}
            {version.state === 'unavailable' && <Notice>{t('workers.editorVersionUnavailableHelp')}</Notice>}
          </div>
        </section>
      </fieldset>
    </form>
  </FormDialog>;
}
