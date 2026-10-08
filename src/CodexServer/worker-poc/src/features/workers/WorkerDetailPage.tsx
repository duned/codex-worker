import { useLanguage } from '../../shared/i18n';
import { useParams, useLocation } from 'react-router-dom';
import { WorkerDetail } from '../../detail.jsx';
import { useApiRead } from '../../shared/api/session';
import { commands, workers, nodes as validateNodes, projects as validateProjects, executions as validateExecutions, diagnostics as validateDiagnostics } from '../../shared/api/validation';
import type { AdministrationPresentation } from './WorkerPocPage';
import { Notice } from '../../shared/Presentation';
import { canonicalPath } from '../../app/routes';
import { WorkerAdvanced } from './WorkerAdvanced';
import { WorkerPreparation } from './WorkerPreparation';
import { NodeProvisioning } from '../nodes/NodeProvisioning';
export function WorkerDetailPage({ workerId, administration }: { workerId?: string; administration?: AdministrationPresentation } = {}) {
  useLanguage();
  const params = useParams();
  const resourceId = workerId ?? params.resourceId;
  const location = useLocation();
  if (!resourceId) throw new Error('Worker route ID missing.');
  const observations = useApiRead('/api/v1/workers', workers);
  const nodes = useApiRead('/api/v1/nodes', validateNodes);
  const projects = useApiRead('/api/v1/projects', validateProjects);
  const executions = useApiRead('/api/v1/executions?limit=50&offset=0', validateExecutions);
  const diagnostics = useApiRead(`/api/v1/workers/${encodeURIComponent(resourceId)}/diagnostics`, validateDiagnostics);
  const nodeCommands = useApiRead(`/api/v1/nodes/${encodeURIComponent(resourceId)}/commands`, commands);
  const observation = observations.data?.find(item => item.workerId === resourceId);
  const projectId = new URLSearchParams(location.search).get('project');
  return <div className="space-y-6">
    {observations.error && <Notice error>{observations.error}</Notice>}
    <WorkerDetail workersHref={workerId ? '/workers' : '/workers'} administrationHref={workerId ? `/workers/${encodeURIComponent(workerId)}${location.search}` : canonicalPath(location.pathname, location.search)} id={resourceId} observations={observations.data ?? null} nodes={nodes.data ?? null}
      projects={projects.data ?? null} executions={executions.data ?? null} diagnostics={diagnostics.data ?? null}
      preparation={administration && observation ? <WorkerPreparation worker={observation} diagnostics={diagnostics.data} projects={projects.data} node={nodes.data?.find(item => item.id === resourceId)} /> : undefined}
      provisioning={administration ? <NodeProvisioning key={resourceId} nodeId={resourceId} repository={projects.data?.find(item => item.id === projectId)?.repository} /> : undefined}
      nodeCommands={nodeCommands.data ?? null} loading={!observations.data && !observations.error} now={Date.now()} administration={administration} readOnly={!administration} />
    {administration && observation && <WorkerAdvanced worker={observation} diagnostics={diagnostics.data} />}
  </div>;
}
