import type { ServerStatus } from './api/contracts.js';

export type ServerStatusPresentation =
  | { kind: 'unavailable'; tone: 'error'; freshness: 'stale' | 'unknown' }
  | { kind: 'loading'; tone: 'gray'; freshness: 'current' | 'unknown' }
  | { kind: 'unknown'; tone: 'gray'; freshness: 'current' | 'unknown' }
  | { kind: 'ready'; tone: 'success'; freshness: 'current' | 'unknown' }
  | { kind: 'offline'; tone: 'error'; freshness: 'current' | 'unknown' }
  | { kind: 'not-ready'; tone: 'warning'; freshness: 'current' | 'unknown'; state: string };

export function serverStatusPresentation(
  data: ServerStatus | undefined,
  hasError: boolean,
  loading: boolean,
  updatedAt: number,
): ServerStatusPresentation;
