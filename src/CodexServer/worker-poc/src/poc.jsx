import { createRoot } from 'react-dom/client';
import { WorkerDetail } from './detail.jsx';
import { Application } from './shell.jsx';

const root = createRoot(document.getElementById('worker-poc'));
const id = decodeURIComponent(window.location.pathname.split('/')[2]);
let session = { authenticated: false };
let snapshot = { observations: null, loading: true };
function render() { root.render(<Application session={session}><WorkerDetail id={id} {...snapshot} /></Application>); }
// The administration/session/stream owner supplies snapshots. This island
// performs no requests, retains no tokens and registers no timers. Stable keys
// let React retain focused links and open disclosures during refreshes.
window.codexWorkerPoc = {
  session(data) { session = data; render(); },
  render(observations, loading = false) { snapshot = { ...snapshot, observations, loading }; render(); },
  update(data) { snapshot = { ...snapshot, ...data }; render(); },
  clear() { snapshot = { observations: null, loading: false }; render(); }
};
render();
