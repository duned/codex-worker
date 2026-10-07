import { useApiRead } from '../../shared/api/session';
import { workers, projects, executions, serverStatus } from '../../shared/api/validation';
import { useServerReadiness } from '../server/useServerReadiness';
import { preparedWorker } from '../server/readiness';
import { aggregateCapacity, executionNeedsAttention, workerAttention } from './model';
import { AdvancedDisclosure, PageHeading, ResourceIdentity, StatusBadge } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import type { ExecutionSummary } from '../../shared/api/contracts';

export function HomePage() {
  const registry = useApiRead('/api/v1/workers', workers);
  const catalog = useApiRead('/api/v1/projects', projects);
  const activity = useApiRead('/api/v1/executions?limit=50&offset=0', executions);
  const status = useApiRead('/api/status', serverStatus);
  const readiness = useServerReadiness();
  const prepared = preparedWorker(readiness.inventory.data);
  const resume = readiness.inventory.data?.find(node => node.kind === 'worker');
  const milestones = [
    { id: 'github', action: readiness.github.complete ? 'Inspect connection' : 'Connect Server GitHub', title: 'Connect Server GitHub', complete: readiness.github.complete, known: !!readiness.inventory.data && !!readiness.connection.data, detail: readiness.github.detail, href: '/settings?node=server' },
    { id: 'project', action: catalog.data?.length ? 'Manage projects' : 'Create project', title: 'Create a project', complete: !!catalog.data?.length, known: !!catalog.data, detail: catalog.data ? `${catalog.data.length} central project definitions` : 'Central definitions unavailable', href: '/projects' },
    { id: 'worker', action: prepared ? 'Inspect prepared Worker' : resume ? 'Resume Worker preparation' : 'Add Worker', title: 'Prepare a Worker', complete: !!prepared, known: !!readiness.inventory.data, detail: prepared ? `${prepared.displayName ?? 'Worker'} has current execution prerequisites` : 'Enroll or resume tools, authentication and configuration', href: prepared || resume ? `/workers/${encodeURIComponent((prepared ?? resume)?.id ?? '')}?step=preparation` : '/workers?prepare=1' }
  ];
  const renderMilestone = (item: typeof milestones[number]) => <div key={item.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-secondary py-4 last:border-0">
    <div className="min-w-0"><h3 className="font-medium text-primary">{item.title}</h3><p className="text-sm text-tertiary">{item.detail}</p></div>
    <div className="flex flex-wrap items-center gap-3"><StatusBadge tone={item.complete ? 'success' : item.known ? 'warning' : 'gray'}>{item.complete ? 'Complete' : item.known ? 'Needs attention' : 'Unavailable'}</StatusBadge><Button href={item.href} color="secondary" size="sm">{item.action}</Button></div>
  </div>;
  const renderExecution = (item: ExecutionSummary) => <div key={item.id} className="space-y-2 border-b border-secondary py-4 last:border-0">
    <div className="flex flex-wrap items-center justify-between gap-3"><Button href={`/executions/${encodeURIComponent(item.id)}`} color="link-color">{catalog.data?.find(project => project.id === item.projectId)?.name ?? 'Project'} · {item.workReference ? `${item.workReference.type} ${item.workReference.id}` : 'Execution'}</Button><StatusBadge tone={item.recoveryState || item.state === 'Failed' ? 'error' : item.state === 'Completed' ? 'success' : 'gray'}>{item.state}{item.currentStage ? ` · ${item.currentStage}` : ''}</StatusBadge></div>
    {item.assignedWorkerId && <a className="text-sm text-secondary underline" href={`/workers/${encodeURIComponent(item.assignedWorkerId)}`}>{registry.data?.find(worker => worker.workerId === item.assignedWorkerId)?.displayName ?? 'Assigned Worker'}</a>}
    {executionNeedsAttention(item) && <p className="text-sm text-warning-primary">{item.recoveryState ? `Recovery: ${item.recoveryState}` : item.pendingReason ? 'Pending work' : 'Eligibility blocked'}</p>}
    <AdvancedDisclosure title="Execution diagnostics"><ResourceIdentity name="Execution" id={item.id} /><ResourceIdentity name="Project" id={item.projectId} />{item.assignedWorkerId && <ResourceIdentity name="Assigned Worker" id={item.assignedWorkerId} />}{item.pendingReason && <p>Pending: {item.pendingReason}</p>}{item.recoveryReason && <p>Recovery: {item.recoveryReason}</p>}{item.completionSummary && <p>{item.completionSummary}</p>}{item.managedEligibilityReasons?.map(reason => <p key={reason}>{reason}</p>)}<a href={`/projects/${encodeURIComponent(item.projectId)}?issues=1`}>Inspect project Issues and eligibility</a></AdvancedDisclosure>
  </div>;
  const current = activity.data?.filter(item => ['Assigned', 'Running'].includes(item.state)) ?? [];
  const attention = activity.data?.filter(executionNeedsAttention) ?? [];
  async function refresh() {
    await Promise.all([registry.refetch(), catalog.refetch(), activity.refetch(), status.refetch(), readiness.inventory.refetch(), readiness.connection.refetch()]);
  }
  return <div className="space-y-6 text-secondary">
    <PageHeading title="Home" actions={<Button color="secondary" onPress={() => { void refresh(); }}>Refresh system state</Button>} />
    <TableCard.Root><TableCard.Header title="Current work" description="Reported activity in the latest 50 execution requests. Older active details may be outside this snapshot." /><div className="px-5 pb-5">{!activity.data ? <p>{activity.loading ? 'Loading execution activity…' : 'Execution activity unavailable'}</p> : current.length ? current.map(renderExecution) : <p>No active work in this bounded snapshot.</p>}<Button href="/executions" color="link-color">Review queue and outcomes</Button></div></TableCard.Root>
    <TableCard.Root><TableCard.Header title="Needs attention" /><div className="px-5 pb-5 space-y-4">
      {!readiness.github.complete && <div><StatusBadge tone={readiness.github.tone}>{readiness.github.detail}</StatusBadge> <Button href="/settings?node=server" color="link-color">Inspect Server GitHub connection</Button></div>}
      {!registry.data && <p>Worker connectivity unavailable</p>}
      {registry.data?.map(worker => {
        const reasons = workerAttention(worker, readiness.inventory.data);
        return reasons.length ? <div key={worker.workerId}><Button href={`/workers/${encodeURIComponent(worker.workerId)}?step=preparation`} color="link-color">{worker.displayName ?? 'Worker'}</Button><StatusBadge tone={worker.availability === 'offline' ? 'error' : 'warning'}>{reasons.join(' · ')}</StatusBadge></div> : null;
      })}
      {readiness.node?.health && readiness.node.health !== 'healthy' && <p>Server health: {readiness.node.health} · <a className="underline" href="/settings?node=server">Inspect Server diagnostics</a>.</p>}
      {attention.map(renderExecution)}
      {registry.data && activity.data && readiness.inventory.data && readiness.node?.health !== 'unhealthy' && readiness.github.complete && !registry.data.some(worker => workerAttention(worker, readiness.inventory.data).length) && !attention.length && <p>No blockers reported in the available observations.</p>}
    </div></TableCard.Root>
    <TableCard.Root><TableCard.Header title="Resumable setup" description="Start with a project or a Worker. Preparation does not enable scheduling or grant repository access." /><div className="px-5 pb-5">
      {milestones.filter(item => !item.complete).map(renderMilestone)}
      <AdvancedDisclosure title="Completed setup actions">{milestones.filter(item => item.complete).map(renderMilestone)}{!milestones.some(item => item.complete) && <p>No completed actions reported.</p>}</AdvancedDisclosure>
    </div></TableCard.Root>
    <TableCard.Root><TableCard.Header title="Latest activity" description="A bounded snapshot, not total execution history." /><div className="px-5 pb-5">{activity.data ? activity.data.slice(0, 5).map(renderExecution) : <p>Execution activity unavailable</p>}{activity.data?.length === 0 && <p>No execution requests yet.</p>}</div></TableCard.Root>
    <AdvancedDisclosure title="System diagnostics and capacity">
      <p>Server: {status.data?.state ?? 'unavailable'} · version {status.data?.version ?? 'unavailable'}</p>
      <p>Server-wide aggregate Worker slots: {aggregateCapacity(registry.data, 'maximumCapacity')} · in use {aggregateCapacity(registry.data, 'activeExecutions')} · reported available {aggregateCapacity(registry.data, 'availableCapacity')}. This is not a Server scheduling limit.</p>
      <p>{catalog.data?.length ?? 'Unavailable'} central project definitions · {registry.data?.length ?? 'Unavailable'} registered Workers · {activity.data?.length ?? 'Unavailable'} requests in the latest bounded snapshot. Total history metrics are not supplied by these APIs.</p>
      {registry.data?.map(worker => <p key={worker.workerId}><a className="underline" href={`/workers/${encodeURIComponent(worker.workerId)}`}>{worker.displayName ?? 'Worker'}</a>: {worker.activeExecutions ?? 'Unavailable'} / {worker.maximumCapacity ?? worker.capacity ?? 'Unavailable'} slots · available {worker.availableCapacity ?? 'Unavailable'}</p>)}
      {[registry, catalog, activity, status, readiness.inventory, readiness.connection].map((read, index) => read.error && <p key={index}>{read.error}</p>)}
    </AdvancedDisclosure>
  </div>;
}
