import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { Application } from './shared/Shell';
import { ThemeControl } from './shared/ThemeControl';
import { SessionProvider, useSession } from './shared/api/session';
import { WorkerPocPage } from './features/workers/WorkerPocPage';
import { ErrorBoundary } from './app/ErrorBoundary';
function Poc() {
  const session = useSession();
  const id = decodeURIComponent(window.location.pathname.split('/')[2]);
  return <Application session={session} themeControl={<ThemeControl />}><WorkerPocPage id={id} /></Application>;
}
createRoot(document.getElementById('worker-poc')).render(<ErrorBoundary><BrowserRouter><SessionProvider><Poc /></SessionProvider></BrowserRouter></ErrorBoundary>);
