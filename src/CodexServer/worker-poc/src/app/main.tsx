import { RouterProvider } from 'react-aria-components';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, useLocation, useNavigate } from 'react-router-dom';
import { HomeLine, Folder, Server01, Activity, Settings01 } from '@untitledui/icons';
import { Application } from '../shared/Shell';
import { ThemeControl } from '../shared/ThemeControl';
import { SessionProvider, useSession } from '../shared/api/session';
import { DashboardRoutes } from './Router';
import { ErrorBoundary } from './ErrorBoundary';
import { migrationBase, sections } from './routes';
import '../poc.css';
const icons = [HomeLine, Folder, Server01, Activity, Settings01];
function Dashboard() {
  const session = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const active = location.pathname.split('/')[1] || 'home';
  const items = sections.map((section, index) => ({ label: section.charAt(0).toUpperCase() + section.slice(1), href: `${migrationBase}/${section}`, icon: icons[index] }));
  return <RouterProvider navigate={href => {
    if (href.startsWith(migrationBase + '/') || href === migrationBase) navigate(href.slice(migrationBase.length) || '/');
    else window.location.assign(href);
  }}><Application session={session} navigationItems={items} activeUrl={`${migrationBase}/${active}`} themeControl={<ThemeControl />}>
    <DashboardRoutes />
  </Application></RouterProvider>;
}
const element = document.getElementById('worker-poc');
if (!element) throw new Error('Dashboard mount missing.');
createRoot(element).render(<ErrorBoundary><BrowserRouter basename={migrationBase}><SessionProvider><Dashboard /></SessionProvider></BrowserRouter></ErrorBoundary>);
