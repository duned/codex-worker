import type { ReactElement } from 'react';
import type { WorkerObservation, ProjectSummary, ExecutionSummary, NodeSummary, WorkerDiagnostics } from './shared/api/contracts';
// Typed boundary for the retained, fixture-tested PoC presentation. No new mutation owner.
export function WorkerDetail(props: {
  id: string; observations: WorkerObservation[] | null; loading?: boolean;
  nodes?: NodeSummary[] | null; projects?: ProjectSummary[] | null;
  executions?: ExecutionSummary[] | null; diagnostics?: WorkerDiagnostics | null;
  administrationHref?: string; nodeCommands?: null; readOnly: true; now?: number;
}): ReactElement;
