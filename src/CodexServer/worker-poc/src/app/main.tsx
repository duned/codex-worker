import { useLanguage } from '../shared/i18n';
import { useEffect } from 'react';
import { RouterProvider } from 'react-aria-components';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, useLocation, useNavigate } from 'react-router-dom';
import { HomeLine, Folder, Server01, Activity, Settings01 } from '@untitledui/icons';
import { Application } from '../shared/Shell';
import { ThemeControl } from '../shared/ThemeControl';
import { SessionProvider, useSession } from '../shared/api/session';
import { DashboardRoutes } from './Router';
import { ErrorBoundary } from './ErrorBoundary';
import { normalizeBookmark, sections } from './routes';
import '../poc.css';
const normalized = normalizeBookmark(window.location.pathname, window.location.search, window.location.hash);
if (normalized) window.history.replaceState(window.history.state, '', normalized);
const icons = [HomeLine, Folder, Server01, Activity, Settings01];
function Dashboard() {
  useLanguage();
  const session = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  useEffect(() => { document.querySelector<HTMLElement>('#poc-content h1')?.focus({ preventScroll: true }); }, [location.pathname]);
  const active = location.pathname.split('/')[1] || 'home';
  const items = sections.map((section, index) => ({ label: section.charAt(0).toUpperCase() + section.slice(1), href: `/${section}`, icon: icons[index] }));
  return <RouterProvider navigate={href => {
    if (/^\/(home|projects|workers|executions|settings)(?:\/|\?|$)/.test(href)) navigate(href);
    else window.location.assign(href);
  }}><Application session={session} navigationItems={items} activeUrl={`/${active}`} themeControl={<ThemeControl />}>
    <DashboardRoutes />
  </Application></RouterProvider>;
}
const element = document.getElementById('worker-poc');
if (!element) throw new Error('Dashboard mount missing.');
createRoot(element).render(<ErrorBoundary><BrowserRouter><SessionProvider><Dashboard /></SessionProvider></BrowserRouter></ErrorBoundary>);
