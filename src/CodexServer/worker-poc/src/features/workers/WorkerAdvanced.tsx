import { AdvancedDisclosure } from '../../shared/Presentation';
import { useApiRead } from '../../shared/api/session';
import { provisioningPlans } from '../../shared/api/validation';
import type { WorkerObservation, WorkerReadiness } from '../../shared/api/contracts';
import { timestamp } from '../../model.js';
export function WorkerAdvanced({ worker, diagnostics }: { worker: WorkerObservation; diagnostics?: WorkerReadiness }) {
  const plans = useApiRead('/api/v1/provisioning', provisioningPlans);
  const matching = plans.data?.filter(plan => plan.workerId === worker.workerId);
  return <AdvancedDisclosure title="Advanced Worker diagnostics and legacy provisioning history">
    <p>Worker version: {worker.workerVersion ?? diagnostics?.workerVersion ?? 'Unreported'} · configuration synchronization: {diagnostics?.configurationSynchronization ?? 'Unavailable'}</p>
    <p>Platform: {worker.platform ?? 'Unreported'} · registered: {timestamp(worker.firstRegisteredAtUtc)}</p>
    <p>Active Worker projects: {worker.activeProjects?.join('; ') || 'None reported'}. Execution-time names do not establish central project association.</p>
    <p>Capability observations: {diagnostics?.capabilityObservationsCurrent == null ? 'Unreported' : diagnostics.capabilityObservationsCurrent ? 'Current' : 'Stale or unavailable'}</p>
    {diagnostics?.reasons?.map(reason => <p key={reason}>{reason}</p>)}
    {diagnostics?.recentOperationalError && <p>{diagnostics.recentOperationalError}</p>}
    <a href="/executions">Inspect uncertain execution recovery</a>
    <p>Worker-reported scoped capabilities</p>
    {worker.capabilities?.map((item, index) => <p key={index}>{item.type} · {item.name} · {item.scope ?? 'Global'} · {item.version ?? 'Version unreported'}</p>)}
    <p>Legacy plan history</p>
    {!matching ? <p>{plans.error ? 'Legacy provisioning history unavailable.' : 'Loading history…'}</p> : !matching.length ? <p>No legacy plans in the available history.</p> : matching.map(plan => <div key={plan.id}><p>{plan.state} · {timestamp(plan.createdAtUtc)} · ID {plan.id}</p><p>{plan.actions.map(action => `${action.type} · ${action.name}${action.version ? ` ${action.version}` : ''}`).join('; ') || 'No actions required'}{plan.currentActionId && ` · current action ${plan.currentActionId}`}</p></div>)}
  </AdvancedDisclosure>;
}
