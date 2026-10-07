import { Link } from 'react-aria-components';
import { useSearchParams } from 'react-router-dom';
import { PageHeading, ResourceIdentity, StatusBadge, ViewState, Notice } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { TableCard } from '../../untitled/components/application/table/table';
import { useApiRead } from '../../shared/api/session';
import { workers, nodes } from '../../shared/api/validation';
import { statusColor } from '../../model.js';
import { EnrollmentDialog } from './EnrollmentDialog';
export function WorkersPage() {
  const inventory = useApiRead('/api/v1/workers', workers), capabilities = useApiRead('/api/v1/nodes', nodes);
  const [params, setParams] = useSearchParams();
  function enrollment(open: boolean) { const next = new URLSearchParams(params); if (open) next.set('enroll', '1'); else { next.delete('enroll'); next.delete('prepare'); } setParams(next); }
  return <section><PageHeading title="Workers" actions={<Button onPress={() => enrollment(true)}>Add Worker</Button>} />
    {inventory.error ? <ViewState error title="Worker inventory unavailable">{inventory.error}</ViewState> : !inventory.data ? <ViewState title="Loading Workers…" /> : !inventory.data.length ? <ViewState title="No Workers registered">Add a Worker to enroll a new machine or safely associate an existing Worker.</ViewState> : <TableCard.Root><div className="divide-y divide-secondary">{inventory.data.map(worker => {
      const node = capabilities.data?.find(item => item.id === worker.workerId && item.kind === 'worker');
      return <article key={worker.workerId} className="flex flex-wrap items-start justify-between gap-4 p-5"><div className="min-w-0 space-y-2"><Link href={`/workers/${encodeURIComponent(worker.workerId)}`}><ResourceIdentity name={worker.displayName || 'Worker'} id={worker.workerId} /></Link>
        <p>Connection <StatusBadge tone={statusColor(worker.availability)}>{worker.availability}</StatusBadge> · Working {worker.lifecycleState ?? 'Unknown'} · Prerequisites {node?.executionReadiness ?? 'Unavailable'} · {node ? node.observationsStale ? 'Stale' : 'Current' : 'Freshness unavailable'}</p>
        <p>{worker.activeExecutions ?? 'Unknown'} / {worker.maximumCapacity ?? worker.capacity ?? 'Unknown'} active slots · scheduling {worker.schedulingPolicy ?? 'Unknown'}</p></div>
        <Button href={`/workers/${encodeURIComponent(worker.workerId)}?step=preparation`} color="secondary" size="sm">Prepare Worker</Button></article>;
    })}</div></TableCard.Root>}
    {capabilities.error && <Notice error>Readiness observations unavailable.</Notice>}
    {(params.get('enroll') === '1' || params.get('prepare') === '1') && <EnrollmentDialog onClose={() => enrollment(false)} />}
  </section>;
}
