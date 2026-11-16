import { createRoot } from 'react-dom/client';
import { RouterProvider } from 'react-aria-components';
import { BrowserRouter, Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { SessionProvider } from '../shared/api/session';
import { Shell } from '../shared/Shell';
import { HomePage } from '../pages/HomePage';
import { ProjectsPage } from '../pages/ProjectsPage';
import { ProjectPage } from '../pages/ProjectPage';
import { IssuePage } from '../pages/IssuePage';
import { WorkersPage } from '../pages/WorkersPage';
import { WorkerPage } from '../pages/WorkerPage';
import { ExecutionsPage } from '../pages/ExecutionsPage';
import { ExecutionPage } from '../pages/ExecutionPage';
import { SettingsPage } from '../pages/SettingsPage';
import { ErrorBoundary } from './ErrorBoundary';
import '../styles.css';

function Dashboard() {
  const navigate = useNavigate();
  // React Aria links and buttons navigate inside the application without reloading.
  return <RouterProvider navigate={href => navigate(href)}>
    <Shell>
      <Routes>
        <Route path="/" element={<Navigate to="/home" replace />} />
        <Route path="/home" element={<HomePage />} />
        <Route path="/projects" element={<ProjectsPage />} />
        <Route path="/projects/:projectId" element={<ProjectPage />} />
        <Route path="/projects/:projectId/issues/:issueNumber" element={<IssuePage />} />
        <Route path="/workers" element={<WorkersPage />} />
        <Route path="/workers/:workerId" element={<WorkerPage />} />
        <Route path="/executions" element={<ExecutionsPage />} />
        <Route path="/executions/:executionId" element={<ExecutionPage />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="/settings/:resourceId" element={<SettingsPage />} />
        <Route path="*" element={<Navigate to="/home" replace />} />
      </Routes>
    </Shell>
  </RouterProvider>;
}

const element = document.getElementById('dashboard');
if (!element) throw new Error('Dashboard mount missing.');
createRoot(element).render(<ErrorBoundary><BrowserRouter><SessionProvider><Dashboard /></SessionProvider></BrowserRouter></ErrorBoundary>);
