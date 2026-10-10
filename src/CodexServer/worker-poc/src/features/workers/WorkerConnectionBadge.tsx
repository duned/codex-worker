import type { NodeSummary, WorkerObservation } from '../../shared/api/contracts';
import { localizeText, useLanguage } from '../../shared/i18n';
import { workerConnectionStatus } from '../../model';
import { StatusBadge } from '../../shared/Presentation';

export function WorkerConnectionBadge({ worker, node, compact = true }: {
  worker: WorkerObservation;
  node?: NodeSummary;
  compact?: boolean;
}) {
  useLanguage();
  const status = workerConnectionStatus(worker, node);
  return <StatusBadge compact={compact} tone={status.tone} withDot>{localizeText(status.state)}</StatusBadge>;
}
