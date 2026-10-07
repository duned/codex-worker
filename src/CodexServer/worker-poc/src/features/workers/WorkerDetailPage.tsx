import { useParams, useLocation } from 'react-router-dom';
import { WorkerDetail } from '../../detail.jsx';
import { useApiRead } from '../../shared/api/session';
import { commands, workers, nodes as validateNodes, projects as validateProjects, executions as validateExecutions, diagnostics as validateDiagnostics } from '../../shared/api/validation';
import { useSession } from '../../shared/api/session';
import type { AdministrationPresentation } from './WorkerPocPage';
import { Notice } from '../../shared/Presentation';
import { canonicalPath, migrationBase } from '../../app/routes';
export function WorkerDetailPage({ workerId, administration }: { workerId?: string; administration?: AdministrationPresentation } = {}) {
  const params = useParams();
  const resourceId = workerId ?? params.resourceId;
  const session = useSession();
  const location = useLocation();
  if (!resourceId) throw new Error('Worker route ID missing.');
  const observations = useApiRead('/api/v1/workers', workers);
  const nodes = useApiRead('/api/v1/nodes', validateNodes);
  const projects = useApiRead('/api/v1/projects', validateProjects);
  const executions = useApiRead('/api/v1/executions?limit=50&offset=0', validateExecutions);
  const diagnostics = useApiRead(`/api/v1/workers/${encodeURIComponent(resourceId)}/diagnostics`, validateDiagnostics);
  const nodeCommands = useApiRead(`/api/v1/nodes/${encodeURIComponent(resourceId)}/commands`, commands);
  return <div className="space-y-6">
    <Notice>{session.live}</Notice>
    {observations.error && <Notice error>{observations.error}</Notice>}
    <WorkerDetail workersHref={workerId ? '/workers' : `${migrationBase}/workers`} administrationHref={workerId ? `/workers/${encodeURIComponent(workerId)}${location.search}` : canonicalPath(location.pathname, location.search)} id={resourceId} observations={observations.data ?? null} nodes={nodes.data ?? null}
      projects={projects.data ?? null} executions={executions.data ?? null} diagnostics={diagnostics.data ?? null}
      nodeCommands={nodeCommands.data ?? null} loading={!observations.data && !observations.error} now={Date.now()} administration={administration} readOnly={!administration} />
  </div>;
}
