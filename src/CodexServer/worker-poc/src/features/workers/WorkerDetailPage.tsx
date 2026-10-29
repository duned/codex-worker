import { t, useLanguage } from '../../shared/i18n';
import { useParams, useLocation } from 'react-router-dom';
import { WorkerDetail } from '../../detail.jsx';
import { useApiRead } from '../../shared/api/session';
import { commands, workers, nodes as validateNodes, projects as validateProjects, executions as validateExecutions, diagnostics as validateDiagnostics } from '../../shared/api/validation';
import type { AdministrationPresentation } from './WorkerPocPage';
import { Notice } from '../../shared/Presentation';
import { canonicalPath } from '../../app/routes';
import { WorkerRestart } from './WorkerRestart';
import { WorkerJournal } from './WorkerJournal';
import { WorkerAdvanced } from './WorkerAdvanced';
import { WorkerPreparation } from './WorkerPreparation';
import { NodeProvisioning } from '../nodes/NodeProvisioning';
import { useState } from 'react';
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
  const [statusRefreshing, setStatusRefreshing] = useState(false);
  const [statusMessage, setStatusMessage] = useState<{ text: string; failed: boolean } | null>(null);
  const [statusUpdatedAt, setStatusUpdatedAt] = useState<number>();
  const [journalOpenRequest, setJournalOpenRequest] = useState(0);
  const observation = observations.data?.find(item => item.workerId === resourceId);
  const projectId = new URLSearchParams(location.search).get('project');
  const statusUpdatedAtCandidate = Math.max(statusUpdatedAt ?? 0, observations.updatedAt, nodes.updatedAt, diagnostics.updatedAt);
  async function refreshVisibleStatus() {
    await Promise.all([
      observations.refetch({ throwOnError: true }),
      nodes.refetch({ throwOnError: true }),
      diagnostics.refetch({ throwOnError: true })
    ]);
  }
  async function reloadWorkerStatus() {
    if (statusRefreshing || administration?.pending) return;
    setStatusRefreshing(true);
    setStatusMessage(null);
    try {
      await Promise.all([refreshVisibleStatus(), administration?.onRefreshStatus()]);
      setStatusUpdatedAt(Date.now());
      setStatusMessage({ text: t('workers.workerStatusRefreshSuccess'), failed: false });
    } catch {
      setStatusMessage({ text: t('workers.workerStatusRefreshFailure'), failed: true });
    } finally {
      setStatusRefreshing(false);
    }
  }
  function refreshAfterRestartCheck() {
    setJournalOpenRequest(value => value + 1);
  }
  return <div className="poc-worker-detail-page">
    {observation?.workerExecutionSlots != null && observation.workerExecutionSlots !== observation.maximumCapacity && <Notice>{t('workers.editorSlotsPending')}</Notice>}
    {observations.error && <Notice error>{observations.error}</Notice>}
    <WorkerDetail workersHref={workerId ? '/workers' : '/workers'} administrationHref={workerId ? `/workers/${encodeURIComponent(workerId)}${location.search}` : canonicalPath(location.pathname, location.search)} id={resourceId} observations={observations.data ?? null} nodes={nodes.data ?? null}
      projects={projects.data ?? null} executions={executions.data ?? null} diagnostics={diagnostics.data ?? null}
      preparation={administration && observation ? <WorkerPreparation worker={observation} diagnostics={diagnostics.data} projects={projects.data} node={nodes.data?.find(item => item.id === resourceId)} /> : undefined}
      provisioning={administration ? <NodeProvisioning key={resourceId} nodeId={resourceId} repository={projects.data?.find(item => item.id === projectId)?.repository} /> : undefined}
      restart={administration && observation ? <WorkerRestart key={resourceId} worker={observation} onStatusChecked={refreshAfterRestartCheck} /> : undefined}
      nodeCommands={nodeCommands.data ?? null} loading={!observations.data && !observations.error} now={Date.now()} administration={administration} readOnly={!administration}
      statusRefreshing={statusRefreshing} statusMessage={statusMessage} statusUpdatedAt={statusUpdatedAtCandidate || undefined} onRefreshStatus={reloadWorkerStatus} />
    {observation && <WorkerJournal workerId={resourceId} openRequest={journalOpenRequest} />}
    {administration && observation && <WorkerAdvanced worker={observation} diagnostics={diagnostics.data} />}
  </div>;
}
