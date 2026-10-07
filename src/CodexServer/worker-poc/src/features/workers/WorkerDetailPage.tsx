import { useParams, useLocation } from 'react-router-dom';
import { WorkerDetail } from '../../detail.jsx';
import { useApiRead } from '../../shared/api/session';
import type { WorkerObservation, ProjectSummary, ExecutionSummary, NodeSummary, WorkerDiagnostics } from '../../shared/api/contracts';
import { canonicalPath } from '../../app/routes';
export function WorkerDetailPage() {
  const { resourceId } = useParams();
  const location = useLocation();
  if (!resourceId) throw new Error('Worker route ID missing.');
  const observations = useApiRead<WorkerObservation[]>('/api/v1/workers');
  const nodes = useApiRead<NodeSummary[]>('/api/v1/nodes');
  const projects = useApiRead<ProjectSummary[]>('/api/v1/projects');
  const executions = useApiRead<ExecutionSummary[]>('/api/v1/executions?limit=50');
  const diagnostics = useApiRead<WorkerDiagnostics>(`/api/v1/workers/${encodeURIComponent(resourceId)}/diagnostics`);
  return <div className="space-y-6">
    {observations.error && <p role="alert">{observations.error}</p>}
    <WorkerDetail administrationHref={canonicalPath(location.pathname, location.search)} id={resourceId} observations={observations.data ?? null} nodes={nodes.data ?? null}
      projects={projects.data ?? null} executions={executions.data ?? null} diagnostics={diagnostics.data ?? null}
      nodeCommands={null} loading={!observations.data && !observations.error} now={Date.now()} readOnly />
  </div>;
}
