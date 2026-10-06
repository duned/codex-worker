import { createRoot } from 'react-dom/client';
import { WorkerDetail } from './detail.jsx';
import './poc.css';

const root = createRoot(document.getElementById('worker-poc'));
const id = decodeURIComponent(window.location.pathname.split('/')[2]);
let snapshot = { observations: null, loading: true };
function render() { root.render(<WorkerDetail id={id} {...snapshot} />); }
// The administration/session/stream owner supplies snapshots. This island
// performs no requests, retains no tokens and registers no timers. Stable keys
// let React retain focused links and open disclosures during refreshes.
window.codexWorkerPoc = {
  render(observations, loading = false) { snapshot = { ...snapshot, observations, loading }; render(); },
  update(data) { snapshot = { ...snapshot, ...data }; render(); },
  clear() { snapshot = { observations: null, loading: false }; render(); }
};
render();
