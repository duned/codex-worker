import { createRoot } from 'react-dom/client';
import { Badge } from './badge.jsx';
import './poc.css';

const host = document.getElementById('worker-poc');
const root = createRoot(host);
const id = decodeURIComponent(window.location.pathname.split('/')[2]);
function WorkerDetail({ observations, loading }) {
  const worker = observations?.find(item => item.workerId === id);
  const existing = '/workers/' + encodeURIComponent(id);
  return <section className="poc-card" aria-label="Worker detail proof of concept">
    <p className="poc-eyebrow">Worker detail · proof of concept</p>
    <h2>{worker?.displayName || 'Worker details'}</h2>
    <p><a href={existing}>Open existing Worker detail and administration</a></p>
    {worker ? <>
      <Badge color={worker.availability === 'online' ? 'success' : worker.availability === 'stale' ? 'warning' : 'error'}>{worker.availability}</Badge>
      <dl>
        <dt>Identity</dt><dd>{worker.workerId}</dd>
        <dt>Version / platform</dt><dd>{worker.workerVersion} · {worker.platform || 'Not reported'}</dd>
        <dt>Scheduling policy</dt><dd>{worker.schedulingPolicy}</dd>
        <dt>Capacity</dt><dd>{worker.activeExecutions} active · {worker.availableCapacity} available · {worker.maximumCapacity} maximum</dd>
        <dt>Last seen</dt><dd>{worker.lastSeenAtUtc || 'Not reported'}</dd>
      </dl>
      <p>Connectivity and capacity are reported observations. They do not establish execution readiness or grant scheduling permission.</p>
    </> : <p role="status">{loading ? 'Loading current Worker observations…' : observations ? 'Worker unavailable or deleted. Return to Workers to refresh the inventory.' : 'Current Worker observations unavailable. Sign in or refresh to recover current state.'}</p>}
  </section>;
}
// The existing administration/session/stream owner supplies snapshots; this
// island performs no requests, retains no tokens and registers no timers.
window.codexWorkerPoc = {
  render(observations, loading = false) { root.render(<WorkerDetail observations={observations} loading={loading} />); }
};
window.codexWorkerPoc.render(null, true);
