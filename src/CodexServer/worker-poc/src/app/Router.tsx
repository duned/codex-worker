import { Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { lazy, Suspense } from 'react';
import { ViewState } from '../shared/Presentation';
import { SettingsWorkspace } from '../features/settings/Workspace';
import { ExecutionsPage } from '../features/executions/ExecutionsPage';
import { HomePage } from '../features/home/HomePage';
import { WorkerPocPage } from '../features/workers/WorkerPocPage';
import { WorkersPage } from '../features/workers/WorkersPage';
import { ProjectsPage } from '../features/projects/ProjectsPage';
import { ProjectsWorkspace } from '../features/projects/Workspace';
import { sections, resourceSections } from './routes';

const SettingsPage = lazy(() => import('../features/settings/SettingsPage').then(module => ({ default: module.SettingsPage })));
function SettingsRoute() { return <Suspense fallback={<ViewState title="Loading Settings…" />}><SettingsPage /></Suspense>; }

function WorkerPage() {
  const { resourceId } = useParams();
  if (!resourceId) throw new Error('Worker route ID missing.');
  return <WorkerPocPage key={resourceId} id={resourceId} />;
}
// Route boundary: browser context is the source of resource/filter identity.
// It never selects a different resource when a read fails or submits a mutation.
export function DashboardRoutes() {
  const location = useLocation();
  return <SettingsWorkspace><ProjectsWorkspace><Routes>
    <Route path="/" element={<Navigate to={{ pathname: '/home', search: location.search }} replace />} />
    {sections.map(section => <Route key={section} path={`/${section}`} element={section === 'home' ? <HomePage /> : section === 'workers' ? <WorkersPage /> : section === 'projects' ? <ProjectsPage /> : section === 'executions' ? <ExecutionsPage /> : <SettingsRoute />} />)}
    {resourceSections.map(section => <Route key={section} path={`/${section}/:resourceId`} element={section === 'workers' ? <WorkerPage /> : section === 'projects' ? <ProjectsPage /> : section === 'executions' ? <ExecutionsPage /> : <SettingsRoute />} />)}
    <Route path="*" element={<p role="alert">Unknown dashboard route.</p>} />
  </Routes></ProjectsWorkspace></SettingsWorkspace>;
}
