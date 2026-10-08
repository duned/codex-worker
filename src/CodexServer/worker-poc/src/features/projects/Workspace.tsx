import { t, useLanguage, statusLabel } from '../../shared/i18n';
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
    if (current.id !== before.id) throw Error(t("projects.unexpectedProjectIdentity"));
    if (current.revision !== before.revision) { if (draft?.before?.id === before.id) setConflict(current); throw Error(t("projects.revisionChangedLoadTheCurrentDefinitionAndVerifyItAgain")); }
    return current;
  }
  async function save(d: ProjectDraft) {
    if (!d.reviewed || !sameDefinition(d.reviewed, d.definition)) throw Error(t("projects.verifyThisDefinitionBeforeSaving"));
    const key = fenceKey(d.before?.id), attempt: Attempt = { kind: 'save', before: d.before, definition: d.reviewed };
    if (runtime.locked(key)) throw Error(t("projects.reconcileTheRetainedAttemptBeforeAnotherWrite"));
    remember(key, attempt);
    try {
      const saved = await runtime.mutate(key, d.before ? projectPath(d.before.id) : '/api/v1/projects', d.before ? 'PUT' : 'POST', d.before ? { definition: d.reviewed, expectedRevision: d.before.revision } : d.reviewed, value => {
        const saved = project(value);
        if (!d.reviewed || !sameDefinition(saved, d.reviewed) || d.before && (saved.id !== d.before.id || saved.revision !== d.before.revision + 1)) throw Error(t("projects.unexpectedSavedDefinition"));
        return saved;
      }, async signal => {
        if (d.before) await freshProject(d.before, signal);
        else {
          const current = await runtime.read('/api/v1/projects', signal, projectList);
          if (current.some(p => p.name.toLowerCase() === d.definition.name.toLowerCase() || p.repository.toLowerCase() === d.definition.repository.toLowerCase())) throw Error(t("projects.aMatchingProjectExistsRefreshProjectsBeforeSaving"));
        }
        const checked = await runtime.preview('/api/v1/projects/verify', 'POST', { ...d.reviewed }, signal, verification);
        if (!checked.repositoryReadable || !checked.branchExists || checked.repository.toLowerCase() !== d.definition.repository.trim().toLowerCase() || checked.defaultBranch !== d.definition.defaultBranch.trim()) throw Error(t("projects.repositoryOrBranchUnavailableCheckServerGitHubAccess"));
      });
      remember(key); setDraft(undefined); setMessage(t('projects.saved', { name: saved.name }));
      return saved;
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage(runtime.locked(key) ? t("projects.saveCouldNotBeConfirmedCheckSavedDefinitionBeforeAnotherWrite") : error instanceof Error ? error.message : t("projects.saveUnavailable")); throw error; }
  }
  async function lifecycle(p: Project, enabled?: boolean) {
    const key = fenceKey(p.id), deleting = enabled === undefined;
    if (runtime.locked(key)) throw Error(t("projects.reconcileTheRetainedAttemptBeforeAnotherWrite"));
    remember(key, { kind: deleting ? 'delete' : 'lifecycle', before: p, enabled });
    try {
      await runtime.mutate(key, deleting ? `${projectPath(p.id)}?expectedRevision=${p.revision}` : projectPath(p.id) + '/lifecycle', deleting ? 'DELETE' : 'PUT', deleting ? undefined : { enabled, expectedRevision: p.revision }, deleting ? empty : project, signal => freshProject(p, signal).then(() => {}));
      remember(key); setMessage(deleting ? t("projects.projectDeletedExecutionHistoryIsRetained") : t('projects.lifecycleChanged', { state: statusLabel(enabled ? 'Enabled' : 'Disabled') }));
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage(t("projects.actionUnavailableCheckAuthoritativeStateTheServerEnforcesRevisionAndInUse")); throw error; }
  }
  async function submitIssue(d: IssueDraft) {
    if (d.change.kind !== 'enqueue' && d.change.kind !== 'refresh' && !d.preview) throw Error(t("projects.previewTheCurrentChangesBeforeApplying"));
    const key = fenceKey(d.project.id), request = changeRequest(d.project.id, d.before?.number, d.change);
    const attempt: Attempt = { kind: 'issue', project: d.project, before: d.before, change: d.change };
    if (runtime.locked(key)) throw Error(t("projects.reconcileTheRetainedAttemptBeforeAnotherWrite"));
    remember(key, attempt);
    try {
      const result = await runtime.mutate<IssueResult | ExecutionSummary | ExecutionSummary[]>(key, request.path, request.method, request.body ? { ...request.body, previewOnly: false } : undefined,
        value => {
          if (d.change.kind === 'enqueue') { const e = execution(value); if (e.projectId !== d.project.id || e.workReference?.type !== 'github-issue' || e.workReference.id !== String(d.before?.number)) throw Error(t("projects.unexpectedExecutionIdentity")); return e; }
          if (d.change.kind === 'refresh') return refreshResult(value);
          const response = issueResult(value);
          if (response.previewOnly || response.repository.toLowerCase() !== d.project.repository.toLowerCase() || !response.issueNumber || d.before && response.issueNumber !== d.before.number) throw Error(t("projects.unexpectedIssueConfirmation"));
          return response;
        }, async signal => {
          const current = await freshProject(d.project, signal);
          if (d.before) {
            const observed = await runtime.read(issuePath(current.id, d.before.number), signal, issue);
            if (observed.number !== d.before.number) throw Error(t("projects.unexpectedIssueIdentity"));
            if (d.change.kind === 'enqueue' && (!current.enabled || !observed.isEligible)) throw Error(t("projects.issueAdmissionIsBlockedRefreshEligibilityAndProjectPolicy"));
            if (d.change.kind !== 'enqueue' && d.change.kind !== 'refresh' && JSON.stringify(observed) !== JSON.stringify(d.before)) throw Error(t("projects.issueChangedReopenItAndPreviewTheCurrentState"));
            if (d.change.kind === 'enqueue' || d.change.kind === 'refresh') {
              const items = await runtime.read(queuePath(current.id, d.before.number), signal, executionList);
              attempt.executionIds = items.map(e => e.id);
              attempt.eligibilityChecks = Object.fromEntries(items.filter(e => e.state === 'Queued').map(e => [e.id, e.managedEligibilityCheckedAtUtc]));
            }
          }
        });
      remember(key); setIssueDraft(undefined);
      setMessage(d.change.kind === 'refresh' ? Array.isArray(result) && result.length ? result.map(e => `${e.id}: ${e.managedEligibilityState ?? 'unavailable'}${e.managedEligibilityReasons?.length ? ' · ' + e.managedEligibilityReasons.join('; ') : ''}`).join(' · ') : t("projects.noQueuedRequestExistsForThisIssue") : t("projects.issueActionConfirmedByTheServer"));
      return result;
    } catch (error) { if (!runtime.locked(key)) remember(key); setMessage(runtime.locked(key) ? t("projects.issueActionCouldNotBeConfirmedReconcileExplicitlyBeforeAnotherWriteNo") : error instanceof Error ? error.message : t("projects.issueActionUnavailable")); throw error; }
  }
  async function reconcile(key: string, createdNumber?: number) {
    const a = attempts[key]; if (!a) throw Error(t("projects.noRetainedAttempt"));
    let outcome = '';
    try {
      await runtime.reconcile(key, async signal => {
        if (a.kind === 'save') {
          const state = savedDefinition(await runtime.read('/api/v1/projects', signal, projectList), a.before, a.definition);
          if (state.state === 'applied') { setDraft(undefined); outcome = t("projects.savedConfirmed"); }
          else if (state.state === 'not-applied') { setDraft(d => d ? { ...d, reviewed: undefined, step: 2 } : d); outcome = t("projects.saveNotApplied"); }
          else { setConflict(state.project); setDraft(d => d ? { ...d, reviewed: undefined } : d); outcome = t("projects.persistedDiffers"); }
        } else if (a.kind === 'delete' || a.kind === 'lifecycle') {
          const current = (await runtime.read('/api/v1/projects', signal, projectList)).find(p => p.id === a.before.id);
          if (a.kind === 'delete' && !current) outcome = t("projects.deletionConfirmed");
          else if (current && current.revision === a.before.revision) outcome = t("projects.actionNotApplied");
          else if (a.kind === 'lifecycle' && current?.revision === a.before.revision + 1 && current.enabled === a.enabled) outcome = t("projects.lifecycleConfirmed");
          else throw Error(t("projects.revisionChangedOperationOutcomeRemainsUncertainInspectCurrentProjectAndExecutions"));
        } else if (a.kind === 'issue') {
          if (a.change.kind === 'enqueue' || a.change.kind === 'refresh') {
            if (!a.before) throw Error(t("projects.issueIdentityUnavailable"));
            const items = await runtime.read(queuePath(a.project.id, a.before.number), signal, executionList);
            if (a.change.kind === 'enqueue' && !items.some(e => e.projectId === a.project.id && e.workReference?.type === 'github-issue' && e.workReference.id === String(a.before?.number) && !a.executionIds?.includes(e.id))) throw Error(t("projects.noNewMatchingExecutionIsVisibleInThisBoundedQueryOutcomeRemains"));
            if (a.change.kind === 'refresh') {
              const queued = items.filter(e => e.state === 'Queued'), baseline = a.eligibilityChecks ?? {};
              if (queued.some(e => !e.managedEligibilityState || !e.managedEligibilityCheckedAtUtc || !Number.isFinite(Date.parse(e.managedEligibilityCheckedAtUtc)) || e.managedEligibilityCheckedAtUtc === baseline[e.id]) || Object.keys(baseline).some(id => !items.some(e => e.id === id))) throw Error(t("projects.freshEligibilityEvidenceDoesNotEstablishTheRefreshOutcomeInspectExecutionsLock"));
              outcome = queued.length ? queued.map(e => `${e.id}: ${e.managedEligibilityState}${e.managedEligibilityReasons?.length ? ' · ' + e.managedEligibilityReasons.join('; ') : ''}`).join(' · ') : t("projects.noQueuedRequestRemainsForThisIssue");
            } else outcome = t("projects.executionConfirmed");
            setIssueDraft(undefined);
          } else {
            const number = a.before?.number ?? createdNumber;
            if (!number || !Number.isSafeInteger(number) || number < 1) throw Error(t("projects.enterTheCreatedIssueNumberFromGitHubABoundedListCannotProve"));
            const observed = await runtime.read(issuePath(a.project.id, number), signal, issue);
            if (observed.number !== number) throw Error(t("projects.unexpectedIssueIdentity"));
            if (matchesIssue(observed, a.change)) { setIssueDraft(undefined); outcome = t("projects.issueStateConfirmed"); }
            else if (a.before && JSON.stringify(observed) === JSON.stringify(a.before)) { setIssueDraft(d => d ? { ...d, preview: undefined } : d); outcome = t("projects.issueNotApplied"); }
            else throw Error(t("projects.issueStateDiffersOutcomeRemainsUncertainInspectGitHubBeforeContinuing"));
          }
        }
      });
      remember(key); setMessage(outcome);
      await runtime.queries.invalidateQueries({ queryKey: queryKeys.session(runtime.snapshot().generation) });
    } catch (error) { setMessage(error instanceof Error ? error.message : t("projects.authoritativeCheckUnavailableLockRetained")); }
  }
  return { draft, setDraft, issueDraft, setIssueDraft, attempts, message, setMessage, conflict, setConflict, save, lifecycle, submitIssue, reconcile, locked: (id?: string) => runtime.locked(fenceKey(id)) };
}
type Workspace = ReturnType<typeof useWorkspaceOwner>;
const Context = createContext<Workspace | null>(null);
export function ProjectsWorkspace({ children }: { children: ReactNode }) {
  useLanguage();
  const owner = useWorkspaceOwner(); return <Context.Provider value={owner}>{children}</Context.Provider>;
}
export function useProjects() { const context = useContext(Context); if (!context) throw Error(t("projects.projectsOwnerMissing")); return context; }
