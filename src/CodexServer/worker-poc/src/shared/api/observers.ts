import type { ObserverSummary, ObserverExecutionObservation, ObserverDetail } from './contracts';
import type { Validator } from './client';
import { record } from './validation';

function text(value: unknown): asserts value is string {
  if (typeof value !== 'string') throw new Error('Invalid observer text.');
}
function timestamp(value: unknown) {
  text(value);
  if (!Number.isFinite(Date.parse(value))) throw new Error('Invalid observer timestamp.');
}
function integer(value: unknown, minimum: number, maximum: number) {
  if (!Number.isSafeInteger(value) || Number(value) < minimum || Number(value) > maximum) throw new Error('Invalid observer number.');
}
export const observerSummary: Validator<ObserverSummary> = value => {
  const item = record(value);
  for (const key of ['observerId', 'registrationState', 'freshnessMeaning']) text(item[key]);
  if (!/^obs_[0-9a-f]{32}$/.test(String(item.observerId)) || item.mode !== 'standalone-observer'
    || !['unknown', 'fresh', 'stale'].includes(String(item.freshness))) throw new Error('Invalid observer identity or freshness.');
  for (const key of ['name', 'version', 'reportedLifecycle', 'reportedReadiness']) if (item[key] != null) text(item[key]);
  for (const key of ['lastReceivedAtUtc', 'snapshotReceivedAtUtc', 'observedAtUtc']) if (item[key] != null) timestamp(item[key]);
  integer(item.staleAfterSeconds, 10, 3600);
  if (item.maximumCapacity != null) integer(item.maximumCapacity, 1, 8);
  if (item.activeExecutions != null) integer(item.activeExecutions, 0, 8);
  return value as ObserverSummary;
};
export const observerExecution: Validator<ObserverExecutionObservation> = value => {
  const item = record(value), execution = record(item.execution);
  timestamp(item.observedAtUtc); timestamp(item.receivedAtUtc); timestamp(execution.startedAtUtc);
  for (const key of ['localExecutionId', 'project', 'repository', 'issueTitle', 'stage']) text(execution[key]);
  for (const key of ['outcome', 'validationResult']) if (execution[key] != null) text(execution[key]);
  integer(execution.issueNumber, 1, 2147483647);
  if (execution.completedAtUtc != null) timestamp(execution.completedAtUtc);
  if (execution.durationMilliseconds != null) integer(execution.durationMilliseconds, 0, Number.MAX_SAFE_INTEGER);
  return value as ObserverExecutionObservation;
};
function bounded<T>(value: unknown, maximum: number, validate: Validator<T>): T[] {
  if (!Array.isArray(value) || value.length > maximum) throw new Error('Invalid observer page.');
  return value.map(validate);
}
export const observers: Validator<ObserverSummary[]> = value => bounded(value, 100, observerSummary);
export const observerHistory: Validator<ObserverExecutionObservation[]> = value => bounded(value, 100, observerExecution);
export const observerDetail: Validator<ObserverDetail> = value => {
  const item = record(value);
  return { observer: observerSummary(item.observer), currentExecutions: bounded(item.currentExecutions, 256, observerExecution) };
};
