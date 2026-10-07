import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { useRuntime, useSession } from '../../shared/api/session';
import { executions } from '../../shared/api/validation';
import { projectList, project, issue, issueResult, empty, verification, type Project, type Definition, type Issue, type IssueResult } from './contracts';
import { projectPath, issuePath, savedDefinition, sameDefinition, matchesIssue, changeRequest, type IssueChange } from './model';
import { queryKeys } from '../../shared/api/runtime';
import type { ExecutionSummary } from '../../shared/api/contracts';
export interface ProjectDraft { before?: Project; definition: Definition; step: number; reviewed?: Definition; open: boolean }
export interface IssueDraft { project: Project; before?: Issue; change: IssueChange; preview?: IssueResult; open: boolean }
type Attempt = { kind: 'save'; before?: Project; definition: Definition } | { kind: 'lifecycle' | 'delete'; before: Project; enabled?: boolean } | { kind: 'issue'; project: Project; before?: Issue; change: IssueChange; executionIds?: string[]; eligibilityChecks?: Record<string, string | undefined> };
export const fenceKey = (id?: string) => `project:${id ?? 'new'}`;
function executionList(value: unknown) { return executions(value); }
function execution(value: unknown) { return executions([value])[0]; }
const queuePath = (id: string, number: number) => `/api/v1/executions?projectId=${encodeURIComponent(id)}&workType=github-issue&workId=${number}&limit=50&offset=0`;
function refreshResult(value: unknown): ExecutionSummary[] {
  const items = executions(value);
  // Eligibility refresh returns execution projections; no history or totals inferred.
  return items;
}
function useWorkspaceOwner() {
  const runtime = useRuntime(); useSession();
  const [draft, setDraft] = useState<ProjectDraft>();
  const [issueDraft, setIssueDraft] = useState<IssueDraft>();
  const [attempts, setAttempts] = useState<Record<string, Attempt>>({});
  const [message, setMessage] = useState('');
  const [conflict, setConflict] = useState<Project>();
  useEffect(() => {
    if (!draft && !issueDraft && !Object.keys(attempts).length) return;
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn);
  }, [draft, issueDraft, attempts]);
  function remember(key: string, attempt?: Attempt) { setAttempts(old => { const next = { ...old }; if (attempt) next[key] = attempt; else delete next[key]; return next; }); }
  async function freshProject(before: Project, signal: AbortSignal) {
    const current = await runtime.read(projectPath(before.id), signal, project);
    if (current.id !== before.id) throw Error('Unexpected project identity.');
    if (current.revision !== before.revision) { if (draft?.before?.id === before.id) setConflict(current); throw Error('Revision changed. Load the current definition and verify it again.'); }
    return current;
  }
  async function save(d: ProjectDraft) {
    if (!d.reviewed || !sameDefinition(d.reviewed, d.definition)) throw Error('Verify this definition before saving.');
    const key = fenceKey(d.before?.id), attempt: Attempt = { kind: 'save', before: d.before, definition: d.reviewed };
    if (runtime.locked(key)) throw Error('Reconcile the retained attempt before another write.');
    remember(key, attempt);
    try {
      const saved = await runtime.mutate(key, d.before ? projectPath(d.before.id) : '/api/v1/projects', d.before ? 'PUT' : 'POST', d.before ? { definition: d.reviewed, expectedRevision: d.before.revision } : d.reviewed, value => {
        const saved = project(value);
        if (!d.reviewed || !sameDefinition(saved, d.reviewed) || d.before && (saved.id !== d.before.id || saved.revision !== d.before.revision + 1)) throw Error('Unexpected saved definition.');
        return saved;
      }, async signal => {
        if (d.before) await freshProject(d.before, signal);
        else {
          const current = await runtime.read('/api/v1/projects', signal, projectList);
          if (current.some(p => p.name.toLowerCase() === d.definition.name.toLowerCase() || p.repository.toLowerCase() === d.definition.repository.toLowerCase())) throw Error('A matching project exists. Refresh Projects before saving.');
        }
        const checked = await runtime.preview('/api/v1/projects/verify', 'POST', { ...d.reviewed }, signal, verification);
        if (!checked.repositoryReadable || !checked.branchExists || checked.repository.toLowerCase() !== d.definition.repository.trim().toLowerCase() || checked.defaultBranch !== d.definition.defaultBranch.trim()) throw Error('Repository or branch unavailable. Check Server GitHub access.');
      });
      remember(key); setDraft(undefined); setMessage(`Project “${saved.name}” saved. Inspect Worker preparation; Server verification does not establish execution readiness. No Issues were marked ready or enqueued.`);
      return saved;
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage(runtime.locked(key) ? 'Save could not be confirmed. Check saved definition before another write.' : error instanceof Error ? error.message : 'Save unavailable.'); throw error; }
  }
  async function lifecycle(p: Project, enabled?: boolean) {
    const key = fenceKey(p.id), deleting = enabled === undefined;
    if (runtime.locked(key)) throw Error('Reconcile the retained attempt before another write.');
    remember(key, { kind: deleting ? 'delete' : 'lifecycle', before: p, enabled });
    try {
      await runtime.mutate(key, deleting ? `${projectPath(p.id)}?expectedRevision=${p.revision}` : projectPath(p.id) + '/lifecycle', deleting ? 'DELETE' : 'PUT', deleting ? undefined : { enabled, expectedRevision: p.revision }, deleting ? empty : project, signal => freshProject(p, signal).then(() => {}));
      remember(key); setMessage(deleting ? 'Project deleted. Execution history is retained.' : `Project ${enabled ? 'enabled' : 'disabled'}. Existing queued work and leases are not cancelled.`);
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage('Action unavailable. Check authoritative state; the Server enforces revision and in-use restrictions.'); throw error; }
  }
  async function submitIssue(d: IssueDraft) {
    if (d.change.kind !== 'enqueue' && d.change.kind !== 'refresh' && !d.preview) throw Error('Preview the current changes before applying.');
    const key = fenceKey(d.project.id), request = changeRequest(d.project.id, d.before?.number, d.change);
    const attempt: Attempt = { kind: 'issue', project: d.project, before: d.before, change: d.change };
    if (runtime.locked(key)) throw Error('Reconcile the retained attempt before another write.');
    remember(key, attempt);
    try {
      const result = await runtime.mutate<IssueResult | ExecutionSummary | ExecutionSummary[]>(key, request.path, request.method, request.body ? { ...request.body, previewOnly: false } : undefined,
        value => {
          if (d.change.kind === 'enqueue') { const e = execution(value); if (e.projectId !== d.project.id || e.workReference?.type !== 'github-issue' || e.workReference.id !== String(d.before?.number)) throw Error('Unexpected execution identity.'); return e; }
          if (d.change.kind === 'refresh') return refreshResult(value);
          const response = issueResult(value);
          if (response.previewOnly || response.repository.toLowerCase() !== d.project.repository.toLowerCase() || !response.issueNumber || d.before && response.issueNumber !== d.before.number) throw Error('Unexpected Issue confirmation.');
          return response;
        }, async signal => {
          const current = await freshProject(d.project, signal);
          if (d.before) {
            const observed = await runtime.read(issuePath(current.id, d.before.number), signal, issue);
            if (observed.number !== d.before.number) throw Error('Unexpected Issue identity.');
            if (d.change.kind === 'enqueue' && (!current.enabled || !observed.isEligible)) throw Error('Issue admission is blocked. Refresh eligibility and project policy.');
            if (d.change.kind !== 'enqueue' && d.change.kind !== 'refresh' && JSON.stringify(observed) !== JSON.stringify(d.before)) throw Error('Issue changed. Reopen it and preview the current state.');
            if (d.change.kind === 'enqueue' || d.change.kind === 'refresh') {
              const items = await runtime.read(queuePath(current.id, d.before.number), signal, executionList);
              attempt.executionIds = items.map(e => e.id);
              attempt.eligibilityChecks = Object.fromEntries(items.filter(e => e.state === 'Queued').map(e => [e.id, e.managedEligibilityCheckedAtUtc]));
            }
          }
        });
      remember(key); setIssueDraft(undefined);
      setMessage(d.change.kind === 'refresh' ? Array.isArray(result) && result.length ? result.map(e => `${e.id}: ${e.managedEligibilityState ?? 'unavailable'}${e.managedEligibilityReasons?.length ? ' · ' + e.managedEligibilityReasons.join('; ') : ''}`).join(' · ') : 'No queued request exists for this Issue.' : 'Issue action confirmed by the Server.');
      return result;
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage(runtime.locked(key) ? 'Issue action could not be confirmed. Reconcile explicitly before another write. No automatic retry will occur.' : error instanceof Error ? error.message : 'Issue action unavailable.'); throw error; }
  }
  async function reconcile(key: string, createdNumber?: number) {
    const a = attempts[key]; if (!a) throw Error('No retained attempt.');
    let outcome = '';
    try {
      await runtime.reconcile(key, async signal => {
        if (a.kind === 'save') {
          const state = savedDefinition(await runtime.read('/api/v1/projects', signal, projectList), a.before, a.definition);
          if (state.state === 'applied') { setDraft(undefined); outcome = 'Saved definition confirmed.'; }
          else if (state.state === 'not-applied') { setDraft(d => d ? { ...d, reviewed: undefined, step: 2 } : d); outcome = 'Save was not applied. Draft retained; verify and review before retrying.'; }
          else { setConflict(state.project); setDraft(d => d ? { ...d, reviewed: undefined } : d); outcome = 'Persisted revision differs or project was deleted. Draft retained; explicitly load the current definition or discard it.'; }
        } else if (a.kind === 'delete' || a.kind === 'lifecycle') {
          const current = (await runtime.read('/api/v1/projects', signal, projectList)).find(p => p.id === a.before.id);
          if (a.kind === 'delete' && !current) outcome = 'Deletion confirmed.';
          else if (current && current.revision === a.before.revision) outcome = 'Action not applied. Review current policy before retrying.';
          else if (a.kind === 'lifecycle' && current?.revision === a.before.revision + 1 && current.enabled === a.enabled) outcome = 'Lifecycle change confirmed.';
          else throw Error('Revision changed; operation outcome remains uncertain. Inspect current project and executions.');
        } else if (a.kind === 'issue') {
          if (a.change.kind === 'enqueue' || a.change.kind === 'refresh') {
            if (!a.before) throw Error('Issue identity unavailable.');
            const items = await runtime.read(queuePath(a.project.id, a.before.number), signal, executionList);
            if (a.change.kind === 'enqueue' && !items.some(e => e.projectId === a.project.id && e.workReference?.type === 'github-issue' && e.workReference.id === String(a.before?.number) && !a.executionIds?.includes(e.id))) throw Error('No new matching execution is visible in this bounded query. Outcome remains uncertain; inspect executions.');
            if (a.change.kind === 'refresh') {
              const queued = items.filter(e => e.state === 'Queued'), baseline = a.eligibilityChecks ?? {};
              if (queued.some(e => !e.managedEligibilityState || !e.managedEligibilityCheckedAtUtc || !Number.isFinite(Date.parse(e.managedEligibilityCheckedAtUtc)) || e.managedEligibilityCheckedAtUtc === baseline[e.id]) || Object.keys(baseline).some(id => !items.some(e => e.id === id))) throw Error('Fresh eligibility evidence does not establish the refresh outcome. Inspect executions; lock retained.');
              outcome = queued.length ? queued.map(e => `${e.id}: ${e.managedEligibilityState}${e.managedEligibilityReasons?.length ? ' · ' + e.managedEligibilityReasons.join('; ') : ''}`).join(' · ') : 'No queued request remains for this Issue.';
            } else outcome = 'Matching execution confirmed. No enqueue repeated.';
            setIssueDraft(undefined);
          } else {
            const number = a.before?.number ?? createdNumber;
            if (!number || !Number.isSafeInteger(number) || number < 1) throw Error('Enter the created Issue number from GitHub. A bounded list cannot prove creation was absent.');
            const observed = await runtime.read(issuePath(a.project.id, number), signal, issue);
            if (observed.number !== number) throw Error('Unexpected Issue identity.');
            if (matchesIssue(observed, a.change)) { setIssueDraft(undefined); outcome = 'Desired Issue state confirmed.'; }
            else if (a.before && JSON.stringify(observed) === JSON.stringify(a.before)) { setIssueDraft(d => d ? { ...d, preview: undefined } : d); outcome = 'Issue action not applied. Draft retained; preview again before retrying.'; }
            else throw Error('Issue state differs; outcome remains uncertain. Inspect GitHub before continuing.');
          }
        }
      });
      remember(key); setMessage(outcome);
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(runtime.snapshot().generation) });
    } catch (error) { setMessage(error instanceof Error ? error.message : 'Authoritative check unavailable. Lock retained.'); }
  }
  return { draft, setDraft, issueDraft, setIssueDraft, attempts, message, setMessage, conflict, setConflict, save, lifecycle, submitIssue, reconcile, locked: (id?: string) => runtime.locked(fenceKey(id)) };
}
type Workspace = ReturnType<typeof useWorkspaceOwner>;
const Context = createContext<Workspace | null>(null);
export function ProjectsWorkspace({ children }: { children: ReactNode }) {
  const owner = useWorkspaceOwner(); return <Context.Provider value={owner}>{children}</Context.Provider>;
}
export function useProjects() { const context = useContext(Context); if (!context) throw Error('Projects owner missing.'); return context; }
