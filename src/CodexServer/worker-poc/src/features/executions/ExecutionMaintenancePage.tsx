import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../shared/api/client';
import type { ExecutionMaintenanceCommand, ExecutionMaintenanceObservation, NodeSummary, WorkerObservation } from '../../shared/api/contracts';
import { nodes, projects, serverStatus, worker as validateWorker, workers } from '../../shared/api/validation';
import { queryKeys } from '../../shared/api/runtime';
import { useApiRead, useRuntime, useSession } from '../../shared/api/session';
import { statusLabel, t, useLanguage, type TranslationKey } from '../../shared/i18n';
import { timestamp, statusColor } from '../../model';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { Notice, PageHeading, StatusBadge, ViewState } from '../../shared/Presentation';
import { executionList, attentionAssessment, lastActivity, maintenanceCommand, maintenanceCommands, maintenanceDetail, maintenanceReason, offset, query, timeSince, type Execution } from './model';

const historyLimit = 50;
const capability = 'execution-maintenance-v1';
type QueueRow = {
  key: string;
  item?: Execution;
  observation?: ExecutionMaintenanceObservation;
  workerId: string;
  worker?: WorkerObservation;
  observedAt?: string;
  reason: TranslationKey;
  next: TranslationKey;
  activity?: string;
};

function tone(value: string): 'gray' | 'success' | 'warning' | 'error' {
  const result = statusColor(value);
  return result === 'success' || result === 'warning' || result === 'error' ? result : 'gray';
}

function isMaintenanceCapable(worker: WorkerObservation | undefined) {
  return !!worker?.capabilities?.some(entry => entry.type === 'protocol' && entry.name === capability);
}

function latestInventories(commands: ExecutionMaintenanceCommand[]) {
  const result = new Map<string, ExecutionMaintenanceCommand>();
  for (const command of commands) {
    if (command.request.action === 'inventory' && !result.has(command.request.workerId)) result.set(command.request.workerId, command);
  }
  return result;
}

function serverRow(item: Execution, worker: WorkerObservation | undefined, node: NodeSummary | undefined,
  workerLoading: boolean, workerUnavailable: boolean, inventory: ExecutionMaintenanceCommand | undefined): QueueRow | undefined {
  const assessment = attentionAssessment(item, worker, node, Date.now(), workerLoading ? 'loading' : workerUnavailable ? 'unavailable' : 'current');
  const report = inventory?.status === 'succeeded' && inventory.report?.outcome === 'succeeded' ? inventory.report : undefined;
  const observation = report?.observations.find(value => value.serverExecutionId === item.id ||
    !!item.workerExecutionId && value.executionId === item.workerExecutionId);
  const assignmentMismatch = !!observation && (!!item.assignmentId && observation.assignmentId !== item.assignmentId ||
    !!item.lease && observation.generation !== item.lease.generation);
  const reportPending = !!observation && !['none', 'acknowledged'].includes(observation.reportingStatus);
  const workerNeedsReview = !!observation && (assignmentMismatch || reportPending ||
    ['uncertain', 'integration-conflict', 'codex-interrupted'].includes(observation.recoveryState.toLowerCase()));
  const noLease = !item.lease && (!!assessment || !!observation);
  const missingObservation = inventory?.status === 'succeeded' && inventory.report?.outcome === 'succeeded' && !observation;
  if (!assessment && !workerNeedsReview && !noLease && !missingObservation) return undefined;

  let reason: TranslationKey, next: TranslationKey;
  if (noLease) {
    reason = 'maintenance.noServerLease'; next = 'maintenance.attention.reviewExecution';
  } else if (assessment?.canReconcile || assessment?.classification === 'reconciliation-required' || observation && ['uncertain', 'integration-conflict', 'codex-interrupted'].includes(observation.recoveryState.toLowerCase())) {
    reason = 'maintenance.needsReconciliation'; next = 'maintenance.attention.reconcileExecution';
  } else if (worker?.availability !== 'online' && worker?.availability !== 'draining' && worker) {
    reason = 'maintenance.attention.workerOffline'; next = 'maintenance.attention.waitForWorker';
  } else if (workerUnavailable && !worker) {
    reason = 'maintenance.attention.workerStateUnavailable'; next = 'maintenance.attention.refreshWorkerState';
  } else if (!worker && !workerLoading) {
    reason = 'maintenance.attention.workerRecordMissing'; next = 'maintenance.attention.refreshWorkerState';
  } else if (assignmentMismatch || assessment?.classification === 'provenance-uncertain') {
    reason = 'maintenance.needsReconciliation'; next = 'maintenance.attention.reviewExecution';
  } else if (assessment?.classification === 'stale-lease') {
    reason = assessment.reason as TranslationKey; next = assessment.nextAction as TranslationKey;
  } else if (reportPending || workerNeedsReview) {
    reason = 'maintenance.staleWorkerReport'; next = 'maintenance.attention.inspectExecution';
  } else if (assessment) {
    reason = assessment.reason as TranslationKey; next = assessment.nextAction as TranslationKey;
  } else if (!observation && (worker?.availability === 'online' || worker?.availability === 'draining')) {
    reason = 'maintenance.observationMissing'; next = isMaintenanceCapable(worker) ? 'maintenance.requestWorkerInventory' : 'maintenance.protocolUnavailable';
  } else {
    reason = 'maintenance.needsReconciliation'; next = 'maintenance.attention.reviewExecution';
  }

  return { key: item.id, item, observation, workerId: item.assignedWorkerId ?? item.lease?.workerId ?? '', worker,
    observedAt: inventory?.completedAtUtc ?? inventory?.createdAtUtc, reason, next, activity: lastActivity(item) };
}

function workerOnlyRows(commands: ExecutionMaintenanceCommand[], serverItems: Execution[], workersById: Map<string, WorkerObservation | undefined>): QueueRow[] {
  const currentIds = new Set(serverItems.map(item => item.id));
  const inventories = latestInventories(commands);
  const rows: QueueRow[] = [];
  for (const [workerId, command] of inventories) {
    if (command.status !== 'succeeded' || command.report?.outcome !== 'succeeded') continue;
    for (const observation of command.report.observations) {
      if (observation.archived) continue;
      if (observation.serverExecutionId && currentIds.has(observation.serverExecutionId)) continue;
      if (rows.length >= 100) return rows;
      const hasServerIdentity = !!observation.serverExecutionId;
      const reason: TranslationKey = !hasServerIdentity ? 'maintenance.workerOnlyRecord' : 'maintenance.observationOutsidePage';
      rows.push({ key: `${workerId}:${observation.executionId}`, observation, workerId,
        worker: workersById.get(workerId), observedAt: command.completedAtUtc ?? command.createdAtUtc,
        reason, next: hasServerIdentity ? 'maintenance.openServerExecution' : 'maintenance.preserveWorkerOnlyRecord' });
    }
  }
  return rows;
}

function historyFailure(error: unknown, version?: string) {
  if (error instanceof ApiError && error.status === 404)
    return t('maintenance.serverIncompatible', { version: version || t('shared.versionUnknown') });
  if (error instanceof ApiError && error.status === 403) return t('maintenance.permissionDenied');
  return t('maintenance.operationHistoryUnavailable');
}

export function ExecutionMaintenancePage() {
  useLanguage();
  const runtime = useRuntime(), session = useSession(), [params, setParams] = useSearchParams();
  const search = `?${params}`;
  const executionsRead = useApiRead(query(search), executionList);
  const projectRead = useApiRead('/api/v1/projects', projects);
  const workerRead = useApiRead('/api/v1/workers', workers);
  const nodeRead = useApiRead('/api/v1/nodes', nodes);
  const statusRead = useApiRead('/api/status', serverStatus);
  const operationsOffset = Math.min(10000, Number(/^[0-9]+$/.test(params.get('operationOffset') ?? '') ? params.get('operationOffset') : 0));
  const historyPath = `/api/v1/maintenance/executions?limit=${historyLimit}&offset=${operationsOffset}`;
  const latestHistoryPath = `/api/v1/maintenance/executions?limit=${historyLimit}&offset=0`;
  const history = useQuery({ queryKey: queryKeys.read(session.generation, historyPath), enabled: session.authenticated,
    queryFn: ({ signal }) => runtime.read(historyPath, signal, maintenanceCommands) });
  const latestHistory = useQuery({ queryKey: queryKeys.read(session.generation, latestHistoryPath), enabled: session.authenticated,
    queryFn: ({ signal }) => runtime.read(latestHistoryPath, signal, maintenanceCommands) });
  const historyItems = history.data ?? [];
  const latestHistoryItems = latestHistory.data ?? [];
  const inventoryByWorker = useMemo(() => latestInventories(latestHistoryItems), [latestHistoryItems]);
  const projectsById = new Map((projectRead.data ?? []).map(project => [project.id, project]));
  const workersById = new Map((workerRead.data ?? []).map(worker => [worker.workerId, worker]));
  const nodesById = new Map((nodeRead.data ?? []).map(node => [node.id, node]));
  const serverItems = executionsRead.data ?? [];
  const rows = serverItems.flatMap(item => {
    const worker = workersById.get(item.assignedWorkerId ?? '');
    const row = serverRow(item, worker, nodesById.get(item.assignedWorkerId ?? ''), workerRead.loading, !!workerRead.error,
      inventoryByWorker.get(item.assignedWorkerId ?? ''));
    return row ? [row] : [];
  });
  rows.push(...workerOnlyRows(latestHistoryItems, serverItems, workersById));
  const workerIds = [...new Set([...serverItems.map(item => item.assignedWorkerId).filter((id): id is string => !!id),
    ...latestHistoryItems.map(item => item.request.workerId), ...(workerRead.data ?? []).filter(isMaintenanceCapable).map(item => item.workerId)])].slice(0, 100);
  const activeByWorker = new Map<string, ExecutionMaintenanceCommand>();
  for (const command of latestHistoryItems) if (['pending', 'running', 'uncertain'].includes(command.status) && !activeByWorker.has(command.request.workerId)) activeByWorker.set(command.request.workerId, command);
  const [pendingWorker, setPendingWorker] = useState<string>();
  const [selectedOperationId, setSelectedOperationId] = useState<string>();
  const [selectedWorkerId, setSelectedWorkerId] = useState<string>();
  const [feedback, setFeedback] = useState<{ text: string; error?: boolean }>();
  const [operationPending, setOperationPending] = useState(false);
  useEffect(() => {
    setPendingWorker(undefined); setSelectedOperationId(undefined); setSelectedWorkerId(undefined); setFeedback(undefined); setOperationPending(false);
  }, [session.generation]);
  const operationPath = selectedOperationId ? `/api/v1/maintenance/executions/${encodeURIComponent(selectedOperationId)}` : '/api/v1/maintenance/executions/00000000000000000000000000000000';
  const operation = useQuery({ queryKey: queryKeys.read(session.generation, operationPath), enabled: session.authenticated && !!selectedOperationId,
    queryFn: ({ signal }) => runtime.read(operationPath, signal, maintenanceDetail) });

  function setPage(value: number) {
    const next = new URLSearchParams(params); next.set('offset', String(value)); setParams(next);
  }
  function setOperationsPage(value: number) {
    const next = new URLSearchParams(params); if (value === 0) next.delete('operationOffset'); else next.set('operationOffset', String(value)); setParams(next);
  }
  async function refreshAll() {
    if (pendingWorker || operationPending) return;
    await Promise.all([executionsRead.refetch(), projectRead.refetch(), workerRead.refetch(), nodeRead.refetch(), history.refetch(), latestHistory.refetch(), statusRead.refetch()]);
    if (selectedOperationId) await refreshOperation();
  }
  async function inspectWorkerInventory(workerId: string) {
    const resource = `/api/v1/maintenance/workers/${encodeURIComponent(workerId)}`;
    if (pendingWorker || latestHistory.isError || latestHistory.isLoading || activeByWorker.has(workerId) || runtime.locked(resource)) return;
    setPendingWorker(workerId); setFeedback(undefined);
    const operationId = globalThis.crypto.randomUUID().replaceAll('-', '').toLowerCase();
    const request = { operationId, workerId, action: 'inventory' as const, apply: false, timeoutSeconds: 60, limit: 50, offset: 0 };
    try {
      const created = await runtime.mutate(resource, '/api/v1/maintenance/executions', 'POST', request, value => {
        const result = maintenanceCommand(value);
        if (result.request.operationId !== operationId || result.request.workerId !== workerId || result.request.action !== 'inventory' || result.request.apply)
          throw new Error('Unexpected inventory operation.');
        return result;
      }, async signal => {
        const workerHistory = await runtime.read(`/api/v1/maintenance/executions?workerId=${encodeURIComponent(workerId)}&limit=50&offset=0`, signal, maintenanceCommands);
        if (workerHistory.some(command => ['pending', 'running', 'uncertain'].includes(command.status))) throw new Error('maintenance.workerOperationInProgress');
        const currentWorker = await runtime.read(`/api/v1/workers/${encodeURIComponent(workerId)}`, signal, validateWorker);
        if (currentWorker.workerId !== workerId || !['online', 'draining'].includes(currentWorker.availability)) throw new Error('maintenance.workerOffline');
        if (!isMaintenanceCapable(currentWorker)) throw new Error('maintenance.protocolUnavailable');
        const freshNodes = await runtime.read('/api/v1/nodes', signal, nodes);
        if (freshNodes.find(node => node.id === workerId)?.observationsStale) throw new Error('maintenance.workerDataOutdated');
      });
      if (runtime.snapshot().generation === session.generation) {
        setSelectedOperationId(created.request.operationId);
        setSelectedWorkerId(workerId);
        setFeedback({ text: t('maintenance.inventorySubmitted') });
        await Promise.all([history.refetch(), latestHistory.refetch()]);
      }
    } catch (error) {
      if (runtime.snapshot().generation === session.generation) {
        const key = error instanceof Error && error.message.startsWith('maintenance.') ? error.message as TranslationKey : 'maintenance.operationUnavailable';
        setFeedback({ text: t(key), error: true });
        if (runtime.locked(resource)) { setSelectedOperationId(operationId); setSelectedWorkerId(workerId); }
        await Promise.all([history.refetch(), latestHistory.refetch()]);
      }
    } finally { setPendingWorker(undefined); }
  }
  async function refreshOperation() {
    if (!selectedOperationId || operationPending) return;
    const generation = session.generation, currentOperationId = selectedOperationId;
    const selectedCommand = historyItems.find(command => command.request.operationId === currentOperationId)
      ?? latestHistoryItems.find(command => command.request.operationId === currentOperationId);
    const workerId = selectedCommand?.request.workerId ?? selectedWorkerId;
    if (!workerId) { setFeedback({ text: t('maintenance.operationUnavailable'), error: true }); return; }
    const resource = `/api/v1/maintenance/workers/${encodeURIComponent(workerId)}`;
    let operationMissing = false;
    setOperationPending(true); setFeedback(undefined);
    try {
      await runtime.reconcile(resource, async signal => {
        try {
          const detail = await runtime.read(`/api/v1/maintenance/executions/${encodeURIComponent(currentOperationId)}`, signal, maintenanceDetail);
          if (detail.operation.request.operationId !== currentOperationId || detail.operation.request.workerId !== workerId) throw new Error('maintenance.operationUnavailable');
          runtime.queries.setQueryData(queryKeys.read(generation, `/api/v1/maintenance/executions/${encodeURIComponent(currentOperationId)}`), detail);
        } catch (error) {
          if (!(error instanceof ApiError) || error.status !== 404) throw error;
          const path = `/api/v1/maintenance/executions?workerId=${encodeURIComponent(workerId)}&limit=50&offset=0`;
          const commands = await runtime.read(path, signal, maintenanceCommands);
          if (commands.some(command => command.request.operationId === currentOperationId)) throw error;
          runtime.queries.setQueryData(queryKeys.read(generation, path), commands);
          operationMissing = true;
        }
      });
      await Promise.all([history.refetch(), latestHistory.refetch(), executionsRead.refetch(), workerRead.refetch(), nodeRead.refetch()]);
      if (runtime.snapshot().generation === generation) setFeedback({ text: t(operationMissing ? 'maintenance.operationNotAccepted' : 'maintenance.authoritativeStatusRefreshed') });
    } catch { if (runtime.snapshot().generation === generation) setFeedback({ text: t('maintenance.operationUnavailable'), error: true }); }
    finally { if (runtime.snapshot().generation === generation) setOperationPending(false); }
  }
  async function cancelPendingOperation() {
    if (!selectedOperationId || !selected?.operation || selected.operation.status !== 'pending' || operationPending) return;
    const generation = session.generation, currentOperationId = selectedOperationId, command = selected.operation;
    const resource = `/api/v1/maintenance/workers/${encodeURIComponent(command.request.workerId)}`;
    setOperationPending(true); setFeedback(undefined);
    try {
      await runtime.mutate(resource, `${operationPath}/cancel`, 'POST', undefined, value => {
        const result = maintenanceCommand(value);
        if (result.request.operationId !== currentOperationId || result.request.workerId !== command.request.workerId || result.status !== 'cancelled')
          throw new Error('maintenance.operationUnavailable');
        return result;
      }, async signal => {
        const current = await runtime.read(operationPath, signal, maintenanceDetail);
        if (current.operation.request.operationId !== currentOperationId || current.operation.status !== 'pending') throw new Error('maintenance.actionUnavailable');
      });
      if (runtime.snapshot().generation === generation) {
        setFeedback({ text: t('maintenance.pendingCancelled') });
        await Promise.all([history.refetch(), latestHistory.refetch()]);
      }
    } catch (error) {
      if (runtime.snapshot().generation === generation) {
        const key = error instanceof Error && error.message.startsWith('maintenance.') ? error.message as TranslationKey : 'maintenance.operationUnavailable';
        setFeedback({ text: t(key), error: true });
      }
    } finally { if (runtime.snapshot().generation === generation) setOperationPending(false); }
  }

  const selected = operation.data;
  const selectedOperation = selected?.operation;
  const selectedServerExecutionId = selectedOperation?.request.serverExecutionId ?? selected?.observations.find(row => row.execution?.id)?.execution?.id;
  const version = statusRead.data?.version;
  const baseList = new URLSearchParams(params); baseList.delete('view'); baseList.delete('operationOffset');
  const listSearch = baseList.toString();

  return <>
    <PageHeading title={t('maintenance.executionMaintenance')} actions={<div className="flex flex-wrap gap-2">
      <Link className="inline-flex min-h-10 items-center rounded-lg px-3 text-sm font-semibold text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={`/executions${listSearch ? `?${listSearch}` : ''}`}>{t('maintenance.allExecutions')}</Link>
      <Button color="secondary" isDisabled={!!pendingWorker || operationPending} onPress={() => { void refreshAll(); }}>{t('maintenance.refreshInventory')}</Button>
    </div>} />
    <p className="-mt-5 mb-5 max-w-4xl text-sm text-secondary">{t('maintenance.queueDescription')}</p>
    {feedback && <div className="mb-4"><Notice error={feedback.error}>{feedback.text}</Notice></div>}
    {executionsRead.error && <ViewState title={t('maintenance.serverInventoryUnavailable')} error />}
    {!executionsRead.data && !executionsRead.error && <ViewState title={t('executions.loadingExecutions')} />}
    {workerRead.error && <div className="mb-4"><Notice error>{t('maintenance.workerObservationUnavailable')}</Notice></div>}

    <TableCard.Root>
      <section aria-labelledby="maintenance-worker-inventory" className="space-y-4 p-5 sm:p-6">
        <div className="flex flex-wrap items-start justify-between gap-3"><div><h2 id="maintenance-worker-inventory" className="text-lg font-semibold text-primary">{t('maintenance.workerInventory')}</h2><p className="mt-1 max-w-3xl text-sm text-secondary">{t('maintenance.workerInventoryDescription')}</p></div>
          {(history.isLoading || latestHistory.isLoading) && <span role="status" className="text-sm text-tertiary">{t('maintenance.loadingOperationHistory')}</span>}
        </div>
        {(history.error || latestHistory.error) && <Notice error>{historyFailure(history.error ?? latestHistory.error, version)}</Notice>}
        {!history.error && !latestHistory.error && !latestHistoryItems.length && !latestHistory.isLoading && <p className="text-sm text-tertiary">{t('maintenance.noInventoryYet')}</p>}
        <ul className="grid gap-3 lg:grid-cols-2">
          {workerIds.map(workerId => {
            const worker = workersById.get(workerId), node = nodesById.get(workerId), active = activeByWorker.get(workerId);
            const stale = node?.observationsStale === true, capable = isMaintenanceCapable(worker), offline = !worker || !['online', 'draining'].includes(worker.availability);
            const disabled = latestHistory.isError || latestHistory.isLoading || !!pendingWorker || offline || stale || !capable || !!active ||
              runtime.locked(`/api/v1/maintenance/workers/${encodeURIComponent(workerId)}`);
            const inventory = inventoryByWorker.get(workerId);
            return <li key={workerId} className="flex min-w-0 flex-wrap items-center justify-between gap-3 rounded-lg border border-secondary p-4">
              <div className="min-w-0"><p className="font-medium text-primary">{worker?.displayName ?? workerId}</p><p className="mt-1 break-all text-xs text-tertiary">{workerId}</p>
                <p className="mt-1 text-sm text-secondary">{worker ? statusLabel(worker.availability) : t('maintenance.workerRecordMissing')}{' · '}{stale ? t('maintenance.outdated') : node ? t('maintenance.current') : t('maintenance.freshnessUnknown')}</p>
                {active && <p className="mt-1 text-xs text-warning-primary">{t('maintenance.workerOperationOutstanding', { operation: active.request.operationId, status: statusLabel(active.status) })}</p>}
                {!active && !offline && !stale && !capable && <p className="mt-1 text-xs text-tertiary">{t('maintenance.protocolUnavailable')}</p>}
                {inventory && <p className="mt-1 text-xs text-tertiary">{t('maintenance.lastInventoryAt', { time: timestamp(inventory.completedAtUtc ?? inventory.createdAtUtc) })}</p>}
              </div>
              <Button color="secondary" size="sm" isDisabled={disabled} onPress={() => { void inspectWorkerInventory(workerId); }}>{pendingWorker === workerId ? t('shared.submitting') : t('maintenance.refreshWorkerInventory')}</Button>
            </li>;
          })}
        </ul>
      </section>
    </TableCard.Root>

    <section aria-labelledby="maintenance-attention-heading" className="mt-6 space-y-3">
      <div className="flex flex-wrap items-end justify-between gap-3"><div><h2 id="maintenance-attention-heading" className="text-lg font-semibold text-primary">{t('maintenance.needsAttention')}</h2><p className="mt-1 text-sm text-tertiary">{t('maintenance.attentionPageBound')}</p></div>
        <span className="text-sm text-tertiary">{t('executions.offset')}{' '}{offset(search)}{' · '}{rows.length} {t('maintenance.rowsOnPage')}</span>
      </div>
      {executionsRead.data && rows.length === 0 && <ViewState title={t('maintenance.noAttentionOnPage')} />}
      {!!rows.length && <TableCard.Root><ul className="divide-y divide-secondary">{rows.map(row => {
        const project = row.item ? projectsById.get(row.item.projectId) : undefined;
        const reference = row.item?.workReference;
        const issue = reference?.type === 'github-issue' ? `#${reference.id}` : reference?.id;
        const age = timeSince(row.activity, Date.now());
        const executionHref = row.item ? `/executions/${encodeURIComponent(row.item.id)}${search}`
          : row.observation?.serverExecutionId ? `/executions/${encodeURIComponent(row.observation.serverExecutionId)}${search}` : undefined;
        return <li key={row.key} className="grid gap-4 p-5 lg:grid-cols-[minmax(14rem,1fr)_minmax(14rem,1.1fr)_minmax(14rem,1fr)_minmax(12rem,0.8fr)] lg:items-start">
          <div className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.projectIssue')}</p>
            <p className="mt-1 font-semibold text-primary">{row.item ? project?.name ?? row.item.projectId : row.observation?.project ?? t('maintenance.projectUnknown')}{issue || row.observation?.issueNumber ? ` · ${issue ?? `#${row.observation?.issueNumber}`}` : ''}</p>
            {row.item && <Link className="mt-1 block text-sm text-brand-secondary hover:underline" to={`/projects/${encodeURIComponent(row.item.projectId)}`}>{project?.name ?? row.item.projectId}</Link>}
            <p className="mt-1 text-sm text-secondary">{row.worker?.displayName ?? row.workerId}{' · '}{row.worker?.availability ? statusLabel(row.worker.availability)
              : workerRead.loading ? t('maintenance.attention.workerStateLoading')
              : workerRead.error ? t('maintenance.attention.workerStateUnavailable') : t('maintenance.workerRecordMissing')}</p>
          </div>
          <div className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.executionAndClassification')}</p>
            <div className="mt-2 flex flex-wrap gap-2">{row.item && <StatusBadge tone={tone(row.item.state)}>{statusLabel(row.item.state)}</StatusBadge>}
              {row.observation && <StatusBadge tone={tone(row.observation.state)}>{statusLabel(row.observation.state)}</StatusBadge>}
              {row.observation?.archived && <StatusBadge tone="gray">{t('maintenance.archivedObservation')}</StatusBadge>}
              <StatusBadge tone="warning">{t(row.reason)}</StatusBadge></div>
            {row.item && <p className="mt-2 break-all text-xs text-secondary">{t('maintenance.serverExecutionId')}: {row.item.id}</p>}
            {row.item?.workerExecutionId && <p className="mt-1 break-all text-xs text-tertiary">{t('maintenance.workerExecutionId')}: {row.item.workerExecutionId}</p>}
            {!row.item && row.observation && <p className="mt-2 break-all text-xs text-secondary">{t('maintenance.workerExecutionId')}: {row.observation.executionId}</p>}
          </div>
          <div className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.ownershipLeaseReporting')}</p>
            <p className="mt-1 text-sm text-secondary">{row.item ? t('maintenance.serverStateValue', { state: statusLabel(row.item.state), recovery: statusLabel(row.item.recoveryState) }) : t('maintenance.workerStateValue', { state: statusLabel(row.observation?.state), recovery: statusLabel(row.observation?.recoveryState) })}</p>
            {row.item && <p className="mt-1 break-words text-xs text-tertiary">{row.item.lease ? t('maintenance.leaseValue', { owner: row.item.lease.workerId, generation: row.item.lease.generation, state: statusLabel(row.item.lease.state) }) : t('maintenance.noServerLease')}</p>}
            {row.observation && <p className="mt-1 break-words text-xs text-tertiary">{t('maintenance.workerReportValue', { assignment: row.observation.assignmentId ?? t('shared.unknown'), generation: row.observation.generation ?? t('shared.unknown'), reporting: statusLabel(row.observation.reportingStatus) })}</p>}
            {!row.observation && <p className="mt-1 text-xs text-tertiary">{t('maintenance.observationMissing')}</p>}
          </div>
          <div className="min-w-0"><p className="text-xs font-semibold uppercase tracking-wide text-tertiary">{t('maintenance.reasonAndNextAction')}</p>
            {row.activity ? <p className="mt-1 text-sm text-secondary">{t('maintenance.lastProgressAge', { time: timestamp(row.activity), age: age ?? t('maintenance.activityUnknown') })}</p> : <p className="mt-1 text-sm text-tertiary">{t('maintenance.activityUnknown')}</p>}
            {row.observedAt && <p className="mt-1 text-xs text-tertiary">{t('maintenance.observationAt', { time: timestamp(row.observedAt) })}</p>}
            <p className="mt-2 text-sm text-secondary">{t(row.next)}</p>
            {executionHref && <Link className="mt-3 inline-flex min-h-10 items-center rounded-lg px-3 text-sm font-semibold text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={executionHref}>{row.item ? t('maintenance.inspectPreviewExecution') : t('maintenance.openServerExecution')}</Link>}
          </div>
        </li>;
      })}</ul></TableCard.Root>}
      <div className="flex flex-wrap items-center gap-3"><Button color="secondary" isDisabled={offset(search) === 0 || executionsRead.loading} onPress={() => setPage(Math.max(0, offset(search) - 50))}>{t('executions.previousPage')}</Button>
        <Button color="secondary" isDisabled={!executionsRead.data || executionsRead.data.length < 50 || offset(search) >= 10000} onPress={() => setPage(Math.min(10000, offset(search) + 50))}>{t('executions.nextPage')}</Button></div>
    </section>

    <section aria-labelledby="maintenance-history-heading" className="mt-8 space-y-3">
      <div className="flex flex-wrap items-end justify-between gap-3"><div><h2 id="maintenance-history-heading" className="text-lg font-semibold text-primary">{t('maintenance.auditHistory')}</h2><p className="mt-1 text-sm text-tertiary">{t('maintenance.auditHistoryBound')}</p></div>
        <Button color="secondary" size="sm" isDisabled={history.isFetching || !!pendingWorker} onPress={() => { void history.refetch(); }}>{t('maintenance.refreshOperationHistory')}</Button></div>
      {(history.error || latestHistory.error) && <Notice error>{historyFailure(history.error ?? latestHistory.error, version)}</Notice>}
      {history.isLoading && <p role="status" className="text-sm text-tertiary">{t('maintenance.loadingOperationHistory')}</p>}
      {!history.isLoading && history.data?.length === 0 && <ViewState title={t('maintenance.noPriorOperations')} />}
      {!!historyItems.length && <TableCard.Root><ul className="divide-y divide-secondary">{historyItems.map(command => {
        const targetId = command.request.serverExecutionId ?? command.report?.observations.find(item => item.serverExecutionId)?.serverExecutionId;
        const firstObservation = command.report?.observations[0];
        return <li key={command.request.operationId} className="flex flex-wrap items-center justify-between gap-3 p-4 sm:px-5">
          <div className="min-w-0"><p className="font-medium text-primary">{statusLabel(command.request.action)} · {statusLabel(command.status)}</p>
            <p className="mt-1 break-all text-xs text-tertiary">{command.request.operationId}</p>
            <p className="mt-1 text-sm text-secondary">{workersById.get(command.request.workerId)?.displayName ?? command.request.workerId}{firstObservation ? ` · ${firstObservation.project} · #${firstObservation.issueNumber}` : ''}</p>
            <p className="mt-1 text-xs text-tertiary">{timestamp(command.completedAtUtc ?? command.createdAtUtc)}{command.report ? ` · ${statusLabel(command.report.outcome)} · ${maintenanceReason(command.report.reason)}` : ''}</p>
          </div>
          <div className="flex flex-wrap gap-2"><Button color="secondary" size="sm" isDisabled={operationPending} onPress={() => { setSelectedOperationId(command.request.operationId); setSelectedWorkerId(command.request.workerId); }}>{t('maintenance.openOperation')}</Button>
            {targetId && <Link className="inline-flex min-h-9 items-center rounded-lg px-3 text-sm font-semibold text-brand-secondary hover:bg-secondary focus-visible:outline-2 focus-visible:outline-offset-2" to={`/executions/${encodeURIComponent(targetId)}${search}`}>{t('maintenance.executionDetails')}</Link>}</div>
        </li>;
      })}</ul></TableCard.Root>}
      <div className="flex flex-wrap items-center gap-3"><Button color="secondary" isDisabled={operationsOffset === 0 || history.isFetching} onPress={() => setOperationsPage(Math.max(0, operationsOffset - historyLimit))}>{t('executions.previousPage')}</Button>
        <Button color="secondary" isDisabled={!history.data || history.data.length < historyLimit || operationsOffset >= 10000} onPress={() => setOperationsPage(Math.min(10000, operationsOffset + historyLimit))}>{t('executions.nextPage')}</Button>
        <span className="text-sm text-tertiary">{t('executions.offset')}{' '}{operationsOffset}</span></div>
    </section>

    {selectedOperationId && <TableCard.Root><section aria-labelledby="maintenance-operation-result" className="mt-6 space-y-3 p-5 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-3"><div><h2 id="maintenance-operation-result" className="text-lg font-semibold text-primary">{t('maintenance.operationResult')}</h2><p className="break-all text-xs text-tertiary">{selectedOperationId}</p></div>
        <div className="flex flex-wrap gap-2"><Button color="secondary" isDisabled={operationPending} onPress={() => { void refreshOperation(); }}>{t('maintenance.refreshOperation')}</Button><Button color="tertiary" isDisabled={operationPending || runtime.locked(`/api/v1/maintenance/workers/${encodeURIComponent(selectedWorkerId ?? selected?.operation.request.workerId ?? '')}`)} onPress={() => { setSelectedOperationId(undefined); setSelectedWorkerId(undefined); }}>{t('maintenance.closeOperationResult')}</Button></div></div>
      {operation.isPending ? <p role="status" className="text-sm text-secondary">{t('maintenance.loadingOperation')}</p>
        : operation.error ? <Notice error>{t('maintenance.operationUnavailable')}</Notice>
        : selected && <>
          <div className="flex flex-wrap gap-2"><StatusBadge tone={tone(selected.status)}>{statusLabel(selected.status)}</StatusBadge><StatusBadge tone="gray">{statusLabel(selected.workerStatus)}</StatusBadge>{selected.operation.report && <StatusBadge tone={tone(selected.operation.report.outcome)}>{statusLabel(selected.operation.report.outcome)}</StatusBadge>}</div>
          <p className="text-sm text-secondary">{selected.operation.report ? maintenanceReason(selected.operation.report.reason) : t('maintenance.operationWaitingForWorker')}</p>
          {selected.operation.status === 'pending' && <Button color="secondary" isDisabled={operationPending || runtime.locked(`/api/v1/maintenance/workers/${encodeURIComponent(selected.operation.request.workerId)}`)} onPress={() => { void cancelPendingOperation(); }}>{t('maintenance.cancelPendingOperation')}</Button>}
          {selectedServerExecutionId && <Link className="text-sm font-medium text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(selectedServerExecutionId)}${search}`}>{t('maintenance.inspectPreviewExecution')}</Link>}
          {!!selected.observations.length && <ul className="divide-y divide-secondary rounded-lg border border-secondary">{selected.observations.map(row => <li key={row.observation.executionId} className="flex flex-wrap items-center justify-between gap-3 p-3 text-sm">
            <span className="min-w-0 break-words">{row.observation.project} · #{row.observation.issueNumber}<span className="mt-1 block break-all text-xs text-tertiary">{row.observation.executionId}</span></span>
            <span className="flex flex-wrap gap-2"><StatusBadge tone={tone(row.observation.state)}>{statusLabel(row.observation.state)}</StatusBadge>{row.observation.archived && <StatusBadge tone="gray">{t('maintenance.archivedObservation')}</StatusBadge>}<StatusBadge tone={row.status === 'confirmed' ? 'success' : 'warning'}>{statusLabel(row.status)}</StatusBadge><StatusBadge tone={tone(row.observation.reportingStatus)}>{statusLabel(row.observation.reportingStatus)}</StatusBadge></span>
            {row.execution?.id && <Link className="text-sm text-brand-secondary hover:underline" to={`/executions/${encodeURIComponent(row.execution.id)}${search}`}>{t('maintenance.executionDetails')}</Link>}
          </li>)}</ul>}
          {!selected.observations.length && selected.operation.report?.outcome && <Notice>{t('maintenance.operationNoObservations')}</Notice>}
        </>}
    </section></TableCard.Root>}
  </>;
}
