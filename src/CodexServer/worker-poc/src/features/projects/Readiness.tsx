import { Link } from 'react-router-dom';
import { useApiRead } from '../../shared/api/session';
import { workers, diagnostics, record } from '../../shared/api/validation';
import { AdvancedDisclosure, Notice, StatusBadge, ViewState } from '../../shared/Presentation';
import type { Project } from './contracts';
import { array } from './contracts';
interface Report { projectId: string; isEligible: boolean; missingRequirements: string[]; workerReportedRevision?: number; materializationState?: string; diagnosticCode?: string; observationStatus: string }
const observationLabels: Record<string, string> = { 'worker-reported-current-revision': 'Current reported revision', 'stale-heartbeat': 'Stale heartbeat · check Worker connection', 'cached-worker-observation': 'Cached observation · reconnect Worker', 'stale-revision': 'Stale revision · synchronize Worker configuration', 'not-reported': 'Not reported · prepare Worker' };
export function readiness(value: unknown) {
  const shared = diagnostics(value), d = record(value);
  const projects = array(d.projects, value => {
    const p = record(value);
    if (typeof p.projectId !== 'string' || typeof p.isEligible !== 'boolean' || typeof p.observationStatus !== 'string') throw Error('Invalid readiness.');
    for (const key of ['materializationState', 'diagnosticCode']) if (p[key] != null && typeof p[key] !== 'string') throw Error('Invalid evidence.');
    if (p.workerReportedRevision != null && (typeof p.workerReportedRevision !== 'number' || !Number.isSafeInteger(p.workerReportedRevision) || p.workerReportedRevision < 1)) throw Error('Invalid revision.');
    array(p.missingRequirements, r => { if (typeof r !== 'string') throw Error('Invalid reason.'); return r; });
    return value as Report;
  });
  return { ...shared, projects };
}
export function ProjectWorkers({ project }: { project: Project }) {
  const read = useApiRead('/api/v1/workers', workers);
  return <section className="space-y-4"><h2 className="text-lg font-semibold text-primary">Worker-reported readiness and preparation</h2>
    <p className="text-sm text-tertiary">Managed revisions are delivered to authorized Workers. Association, tool readiness, repository permission and checkout materialization are separate evidence. Preparation does not enable scheduling.</p>
    {!read.data ? <ViewState title={read.error ? 'Worker inventory unavailable. Refresh Workers to inspect preparation.' : 'Loading Workers…'} error={!!read.error} /> : !read.data.length ? <ViewState title="No Worker exists yet."><a href="/workers?prepare=1" className="text-sm text-brand-secondary">Add and prepare a Worker</a></ViewState> : read.data.map(worker => <WorkerProject key={worker.workerId} project={project} id={worker.workerId} name={worker.displayName ?? worker.workerId} availability={worker.availability} />)}
  </section>;
}
function WorkerProject({ project, id, name, availability }: { project: Project; id: string; name: string; availability: string }) {
  const read = useApiRead(`/api/v1/workers/${encodeURIComponent(id)}/diagnostics`, readiness), p = read.data?.projects.find(p => p.projectId === project.id);
  // Server observationStatus is authoritative; retain all distinctions rather than
  // treating capability eligibility as permission to schedule or a ready checkout.
  return <div className="space-y-2 rounded-lg border border-secondary p-4">
    <Link to={`/workers/${encodeURIComponent(id)}?step=preparation&project=${encodeURIComponent(project.id)}`} className="font-medium text-primary">{name}</Link>
    <a href={`/workers/${encodeURIComponent(id)}?step=preparation&project=${encodeURIComponent(project.id)}`} className="ml-3 text-sm text-brand-secondary">Prepare / associate Worker</a>
    <StatusBadge tone={availability === 'online' ? 'success' : 'warning'}>{availability}</StatusBadge>
    {!read.data ? <Notice error={!!read.error}>{read.error ? 'Readiness unavailable. Inspect Worker diagnostics.' : 'Loading readiness…'}</Notice> : <>
      <p className="text-sm text-secondary">Configuration synchronization: {read.data.configurationSynchronization}</p>
      <p className="text-sm text-secondary">Reported revision: {p?.workerReportedRevision ?? 'not reported'} · Central revision: {project.revision}</p>
      <StatusBadge tone={p?.observationStatus === 'worker-reported-current-revision' && availability === 'online' ? 'success' : 'warning'}>{observationLabels[p?.observationStatus ?? 'not-reported'] ?? 'Worker observation unavailable'}</StatusBadge>
      <p className="text-sm text-secondary">Checkout materialization: <StatusBadge tone={p?.materializationState === 'failed' ? 'error' : 'gray'}>{p?.materializationState ?? 'unverified'}</StatusBadge></p>
      <p className="text-sm text-secondary">Capability requirements: {p ? p.isEligible ? 'match reported capabilities' : 'unmet' : 'not reported'}. Execution readiness and scheduling remain separate.</p>
      {p?.missingRequirements.map(reason => <Notice key={reason} error>{reason}</Notice>)}
      <AdvancedDisclosure title="Advanced · Worker readiness evidence"><p>GitHub API readiness: {String(read.data.gitHubReady)} · Git readiness: {String(read.data.gitReady)} · AI agent readiness: {String(read.data.aiAgentReady)}</p><p>Observation status: {p?.observationStatus ?? 'not-reported'}</p><p>Provisioning: {read.data.provisioningState}</p>{p?.diagnosticCode && <p>Diagnostic: {p.diagnosticCode}</p>}</AdvancedDisclosure>
    </>}
  </div>;
}
/** Compact list evidence, reusing the same deduplicated Server diagnostics reads. */
export function ProjectBlockers({ project }: { project: Project }) {
  const read = useApiRead('/api/v1/workers', workers);
  if (!read.data) return <p className="text-sm text-tertiary">Worker preparation {read.error ? 'unavailable · inspect Workers' : 'loading…'}</p>;
  if (!read.data.length) return <p className="text-sm text-warning-primary">No Worker exists. Add and prepare a Worker.</p>;
  return <div className="space-y-1 text-sm text-secondary"><p className="text-xs text-tertiary">Readiness observations · up to 3 Workers</p>{read.data.slice(0, 3).map(worker => <WorkerBlocker key={worker.workerId} project={project} id={worker.workerId} name={worker.displayName ?? worker.workerId} />)}</div>;
}
function WorkerBlocker({ project, id, name }: { project: Project; id: string; name: string }) {
  const read = useApiRead(`/api/v1/workers/${encodeURIComponent(id)}/diagnostics`, readiness), p = read.data?.projects.find(p => p.projectId === project.id);
  const observation = observationLabels[p?.observationStatus ?? 'not-reported'] ?? 'Observation unavailable · inspect Worker';
  return <p>{name}: {!read.data ? read.error ? 'readiness unavailable · inspect Worker' : 'loading readiness…' : <>{observation}{p?.missingRequirements.length ? ` · ${p.missingRequirements.join('; ')}` : ''}{p?.materializationState === 'failed' ? ' · Checkout failed · inspect preparation' : ''}</>}</p>;
}
