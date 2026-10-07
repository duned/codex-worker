import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { MigrationPage } from '../pages/MigrationPage';
import { WorkerDetailPage } from '../features/workers/WorkerDetailPage';
import { sections, resourceSections } from './routes';
// Route boundary: browser context is the source of resource/filter identity.
// It never selects a different resource when a read fails or submits a mutation.
export function DashboardRoutes() {
  const location = useLocation();
  return <Routes>
    <Route path="/" element={<Navigate to={{ pathname: '/home', search: location.search }} replace />} />
    {sections.map(section => <Route key={section} path={`/${section}`} element={<MigrationPage section={section} />} />)}
    {resourceSections.map(section => <Route key={section} path={`/${section}/:resourceId`} element={section === 'workers' ? <WorkerDetailPage /> : <MigrationPage section={section} />} />)}
    <Route path="*" element={<p role="alert">Unknown dashboard route.</p>} />
  </Routes>;
}
