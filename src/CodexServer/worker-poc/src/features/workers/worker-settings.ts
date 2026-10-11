import { ApiError } from '../../shared/api/client';
import { worker as validateWorker } from '../../shared/api/validation';
import { type DashboardRuntime, queryKeys } from '../../shared/api/runtime';
import type { WorkerObservation } from '../../shared/api/contracts';
import { t } from '../../shared/i18n';

/** Persist Server assignment settings and publish the authoritative revision to both views. */
export async function saveWorkerSettings(runtime: DashboardRuntime, workerId: string, displayName: string,
  maximumParallelExecutions: number, revision: number, workerExecutionSlots?: number) {
  const path = `/api/v1/workers/${encodeURIComponent(workerId)}`, resource = path;
  const generation = runtime.snapshot().generation;
  const saved = await runtime.mutate(resource, `${path}/settings`, 'PUT', {
    displayName,
    maximumParallelExecutions,
    ...(workerExecutionSlots === undefined ? {} : { workerExecutionSlots }),
    expectedRevision: revision
  }, value => {
    const result = validateWorker(value);
    if (result.workerId !== workerId || result.displayName !== displayName ||
        result.configuredMaximumCapacity !== maximumParallelExecutions || result.configurationRevision !== revision + 1 ||
        (workerExecutionSlots !== undefined && result.workerExecutionSlots !== workerExecutionSlots))
      throw new Error('Unexpected Worker configuration response.');
    return result;
  }, async signal => {
    const current = await runtime.read(path, signal, validateWorker);
    if (current.workerId !== workerId) throw new ApiError(t('workers.editorWorkerIdentityChanged'));
    if ((current.configurationRevision ?? 1) !== revision) throw new ApiError(t('workers.editorRevisionConflict'));
  });
  runtime.queries.setQueryData(queryKeys.read(generation, path), saved);
  runtime.queries.setQueryData<WorkerObservation[]>(queryKeys.read(generation, '/api/v1/workers'), current =>
    current?.map(item => item.workerId === workerId ? saved : item));
  return saved;
}
