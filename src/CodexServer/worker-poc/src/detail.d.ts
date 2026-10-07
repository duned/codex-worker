import type { ReactElement, ReactNode } from 'react';
import type { NodeCommandSummary, WorkerObservation, ProjectSummary, ExecutionSummary, NodeSummary, WorkerDiagnostics } from './shared/api/contracts';
import type { AdministrationPresentation } from './features/workers/WorkerPocPage';
// Typed boundary for the retained, fixture-tested PoC presentation.
export function WorkerDetail(props: {
  preparation?: ReactNode; provisioning?: ReactNode; id: string; observations: WorkerObservation[] | null; loading?: boolean;
  nodes?: NodeSummary[] | null; projects?: ProjectSummary[] | null;
  executions?: ExecutionSummary[] | null; diagnostics?: WorkerDiagnostics | null;
  administrationHref?: string; workersHref?: string; nodeCommands?: NodeCommandSummary[] | null; administration?: AdministrationPresentation; readOnly: boolean; now?: number;
}): ReactElement;

export function CapabilityCard(props: { capability: import("./shared/api/contracts").Capability; commands?: NodeCommandSummary[] | null; children?: ReactNode }): ReactElement;
export { CapabilityCard as Capability };
