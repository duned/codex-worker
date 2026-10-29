import { record } from '../../shared/api/validation';
export interface WorkerUpdate { operationId: string; workerId: string; state: 'waiting-for-idle' | 'updating' | 'reconnecting' | 'succeeded' | 'failed'; diagnostic?: string; expectedVersion?: string; restart?: boolean }
export function workerUpdate(value: unknown): WorkerUpdate | null {
  if (value === null) return null;
  const item = record(value);
  if (typeof item.operationId !== 'string' || !/^[a-f0-9]{32}$/.test(item.operationId) ||
      typeof item.workerId !== 'string' || !/^[a-f0-9]{32}$/.test(item.workerId) ||
      !['waiting-for-idle', 'updating', 'reconnecting', 'succeeded', 'failed'].includes(String(item.state)) ||
      (item.diagnostic != null && typeof item.diagnostic !== 'string') ||
      (item.restart != null && typeof item.restart !== 'boolean') ||
      (item.expectedVersion != null && typeof item.expectedVersion !== 'string')) throw new Error('Invalid Worker update.');
  return item as unknown as WorkerUpdate;
}
