import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { HomePage } from '../features/home/HomePage';
import { MigrationPage } from '../pages/MigrationPage';
import { WorkerDetailPage } from '../features/workers/WorkerDetailPage';
import { ProjectsPage } from '../features/projects/ProjectsPage';
import { ProjectsWorkspace } from '../features/projects/Workspace';
import { sections, resourceSections } from './routes';
// Route boundary: browser context is the source of resource/filter identity.
// It never selects a different resource when a read fails or submits a mutation.
export function DashboardRoutes() {
  const location = useLocation();
  return <ProjectsWorkspace><Routes>
    <Route path="/" element={<Navigate to={{ pathname: '/home', search: location.search }} replace />} />
    {sections.map(section => <Route key={section} path={`/${section}`} element={section === 'home' ? <HomePage /> : section === 'projects' ? <ProjectsPage /> : <MigrationPage section={section} />} />)}
    {resourceSections.map(section => <Route key={section} path={`/${section}/:resourceId`} element={section === 'workers' ? <WorkerDetailPage /> : section === 'projects' ? <ProjectsPage /> : <MigrationPage section={section} />} />)}
    <Route path="*" element={<p role="alert">Unknown dashboard route.</p>} />
  </Routes></ProjectsWorkspace>;
}
