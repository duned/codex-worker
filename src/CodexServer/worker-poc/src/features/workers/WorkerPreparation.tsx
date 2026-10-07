import { useLocation } from 'react-router-dom';
import type { NodeSummary, ProjectSummary, WorkerObservation, WorkerReadiness } from '../../shared/api/contracts';
import { TableCard } from '../../untitled/components/application/table/table';
import { Button } from '../../untitled/components/base/buttons/button';
import { Notice } from '../../shared/Presentation';
const steps = ['registration', 'preparation', 'project', 'activation'];
const titles = ['Registration', 'Preparation', 'Project context', 'Activation'];
export function WorkerPreparation({ worker, diagnostics, node, projects }: { worker: WorkerObservation; diagnostics?: WorkerReadiness; node?: NodeSummary; projects?: ProjectSummary[] }) {
  const location = useLocation(), params = new URLSearchParams(location.search);
  const requested = params.get('step'), projectId = params.get('project');
  const step = requested && steps.includes(requested) ? requested : projectId ? 'project' : 'registration';
  const href = (value: string) => { const next = new URLSearchParams(params); next.set('step', value); return `${location.pathname}?${next}`; };
  const current = worker.availability === 'online' && diagnostics?.configurationSynchronization === 'synchronized' && diagnostics.capabilityObservationsCurrent === true && node && !node.observationsStale;
  const evidence = (value?: boolean) => current && value ? 'Worker-reported evidence present' : 'Unavailable or unverified';
  return <TableCard.Root><TableCard.Header title="Worker preparation" description="Resume from authoritative observations. Preparation never enables scheduling." /><div className="space-y-4 p-5">
    <nav aria-label="Worker preparation steps" className="flex flex-wrap gap-2">{steps.map((value, index) => <Button key={value} href={href(value)} color="secondary" size="sm" aria-current={step === value ? 'step' : undefined}>{index + 1} · {titles[index]}</Button>)}</nav>
    {step === 'registration' && <p>Registered · {worker.availability} · {worker.lifecycleState ?? 'Lifecycle unreported'}. Connectivity does not establish execution readiness. Leave and return at any time; navigation never installs tools or starts login.</p>}
    {step === 'preparation' && <><p>Worker version: {worker.workerVersion ?? diagnostics?.workerVersion ?? 'Unreported'}.</p><p>Managed configuration: {diagnostics?.configurationSynchronization ?? 'Unavailable'}. Repair invalid runtime defaults on the node, then refresh configuration and re-detect.</p>
      <p>Codex service-account authentication: {current ? node.capabilities.find(item => item.definition.id === 'codex-cli')?.state.authentication ?? 'Unknown' : 'Unavailable or unverified'}. Execution preflight: {evidence(diagnostics?.aiAgentReady)}. CLI login alone does not establish successful execution preflight.</p>
      <p>Worker GitHub authentication: {current ? node.capabilities.find(item => item.definition.id === 'github-cli')?.state.authentication ?? 'Unknown' : 'Unavailable or unverified'}. Server GitHub login, Worker login and scoped repository permissions are independent. Inspect capability actions below with explicit local consent.</p></>}
    {step === 'project' && <><p>Authorized managed Workers receive central projects and revisions through snapshots. No separate binding or local project YAML is required. Preparation does not enqueue work or change Issue labels. Missing checkouts are materialized lazily on assignment; no test push is performed.</p>
      {!projects ? <Notice>Central projects unavailable.</Notice> : !projects.length ? <p>No central project exists. <a href="/projects">Create a project</a> and return.</p> : <>
        {projectId && !projects.some(project => project.id === projectId) && <Notice error>The selected project is unavailable or deleted.</Notice>}
        {[...projects].sort((a, b) => Number(b.id === projectId) - Number(a.id === projectId)).map(project => {
          const report = diagnostics?.projects?.find(item => item.projectId === project.id);
          const scoped = (name: string) => worker.capabilities?.some(item => item.type === 'authentication' && item.name === name && item.scope?.toLowerCase() === project.repository.toLowerCase());
          return <article key={project.id} className="space-y-2 rounded-lg border border-secondary p-4"><h3 className="font-semibold text-primary"><a href={`/projects/${encodeURIComponent(project.id)}`}>{project.name}</a></h3><p>{project.repository} · central revision {project.revision ?? 'Unknown'} · {project.enabled == null ? 'Enablement unknown' : project.enabled ? 'Enabled' : 'Disabled'}</p>
            <p>Worker revision: {report?.workerReportedRevision ?? 'Not reported'} · {report?.observationStatus ?? 'not-reported'}. Checkout: {report?.materializationState ?? 'unverified'}{report?.diagnosticCode && ` · ${report.diagnosticCode}`}.</p>
            <p>Scoped GitHub API access: {evidence(scoped('github-api'))}. Git checkout/push permission: {evidence(scoped('git-repository'))}.</p>
            <p>Scoped project eligibility: {report ? report.isEligible ? 'Requirements match; current revision and freshness still govern activation.' : 'Missing requirements' : 'Not reported'}. {report?.missingRequirements.join('; ')}</p>
            <p>Requirements: {project.requirements?.map(item => `${item.type} · ${item.name}${item.version ? ` ${item.version}` : ''}`).join('; ') || 'None reported'}. Worker scoped GitHub read/write and Git checkout/push access remain separate from Server repository readability and checkout existence.</p>
            <Button href={`${location.pathname}?${new URLSearchParams({ step: 'preparation', project: project.id })}`} color="secondary" size="sm">Prepare for this project</Button></article>;
        })}</>}
    </>}
    {step === 'activation' && <><p>{diagnostics?.canActivate ? 'Current Server evidence permits an explicit activation request in Worker controls.' : 'Activation blocked until current Server evidence is reported.'} Existing assignments retain leases when draining or disabling scheduling.</p>{(diagnostics?.activationBlockingReasons ?? ['Readiness evidence unavailable.']).map(reason => <p key={reason}>{reason}</p>)}</>}
  </div></TableCard.Root>;
}
