import { useMemo } from 'react';
import { useApiRead } from './api/session';
import { executions, nodes, projects, workers } from './api/validation';
import type { ExecutionSummary, ProjectSummary, WorkerObservation } from './api/contracts';

// The Server hides archived executions unless a list asks for them.
export const recentExecutionsPath = '/api/v1/executions?limit=100&offset=0';
export const archivedExecutionsPath = '/api/v1/executions?limit=100&offset=0&archived=only';
export const useProjects = () => useApiRead('/api/v1/projects', projects);
export const useWorkers = () => useApiRead('/api/v1/workers', workers);
export const useNodes = () => useApiRead('/api/v1/nodes', nodes);
export const useExecutions = (path = recentExecutionsPath) => useApiRead(path, executions);
export const projectExecutionsPath = (projectId: string, issue?: number) =>
  `/api/v1/executions?projectId=${encodeURIComponent(projectId)}&workType=github-issue${issue ? `&workId=${issue}` : ''}&limit=100&offset=0`;

/** Display names for rows that only carry identifiers. */
export function useNames() {
  const projectList = useProjects(), workerList = useWorkers();
  return useMemo(() => {
    const projectNames = new Map((projectList.data ?? []).map(item => [item.id, item] as [string, ProjectSummary]));
    const workerNames = new Map((workerList.data ?? []).map(item => [item.workerId, item] as [string, WorkerObservation]));
    return {
      project: (id: string) => projectNames.get(id)?.name ?? id,
      projectOf: (id: string) => projectNames.get(id),
      worker: (id?: string) => id ? workerNames.get(id)?.displayName || id.slice(0, 8) : '—',
      issueTitle: (execution: ExecutionSummary) => execution.workReference?.title ?? ''
    };
  }, [projectList.data, workerList.data]);
}
