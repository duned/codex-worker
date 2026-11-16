export type StatusTone = 'gray' | 'success' | 'warning' | 'error' | 'info';
export function isActiveExecutionState(value: unknown): boolean;
export function statusColor(value: unknown): StatusTone;
