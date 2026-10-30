import { record } from '../../shared/api/validation';
export interface WorkerUpdate { operationId: string; workerId: string; state: 'waiting-for-idle' | 'updating' | 'reconnecting' | 'succeeded' | 'failed'; diagnostic?: string; expectedVersion?: string; restart?: boolean; recovery?: { schedulingBlocked: boolean; checkedAtUtc: string; eligible: boolean; checks: { code: string; satisfied: boolean; description: string; nextStep: string }[] } }
export function workerUpdate(value: unknown): WorkerUpdate | null {
  if (value === null) return null;
  const item = record(value);
  if (typeof item.operationId !== 'string' || !/^[a-f0-9]{32}$/.test(item.operationId) ||
      typeof item.workerId !== 'string' || !/^[a-f0-9]{32}$/.test(item.workerId) ||
      !['waiting-for-idle', 'updating', 'reconnecting', 'succeeded', 'failed'].includes(String(item.state)) ||
      (item.diagnostic != null && typeof item.diagnostic !== 'string') ||
      (item.restart != null && typeof item.restart !== 'boolean') ||
      (item.expectedVersion != null && typeof item.expectedVersion !== 'string')) throw new Error('Invalid Worker update.');
  if (item.recovery != null) {
    const recovery = record(item.recovery);
    if (typeof recovery.schedulingBlocked !== 'boolean' || typeof recovery.eligible !== 'boolean' ||
        typeof recovery.checkedAtUtc !== 'string' || !Number.isFinite(Date.parse(recovery.checkedAtUtc)) ||
        !Array.isArray(recovery.checks) || recovery.checks.length > 20 || recovery.checks.some(value => {
          const check = record(value);
          return typeof check.code !== 'string' || typeof check.satisfied !== 'boolean' ||
            typeof check.description !== 'string' || typeof check.nextStep !== 'string';
        })) throw new Error('Invalid Worker recovery status.');
  }
  return item as unknown as WorkerUpdate;
}
