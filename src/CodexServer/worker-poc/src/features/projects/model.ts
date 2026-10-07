import type { Definition, Project, Issue } from './contracts';
export const newDefinition = (): Definition => ({ name: '', repository: '', defaultBranch: '', description: '', requirements: [], issueReadyLabel: null, issueBlockedLabel: null, automaticDiscovery: null });
export function definitionOf(p: Definition): Definition {
  return { name: p.name, repository: p.repository, defaultBranch: p.defaultBranch, description: p.description, requirements: p.requirements,
    issueReadyLabel: p.issueReadyLabel ?? null, issueBlockedLabel: p.issueBlockedLabel ?? null, automaticDiscovery: p.automaticDiscovery ?? null };
}
export function sameDefinition(a: Definition, b: Definition) {
  const normalized = (d: Definition) => ({ ...definitionOf(d), name: d.name.trim(), repository: d.repository.trim().toLowerCase(), defaultBranch: d.defaultBranch.trim(), description: d.description.trim(),
    requirements: d.requirements.map(r => ({ type: r.type.trim().toLowerCase(), name: r.name.trim().toLowerCase(), version: r.version?.trim().replace(/\d+/g, n => String(Number(n))) || null, scope: r.scope?.trim().toLowerCase() || null })),
    issueReadyLabel: d.issueReadyLabel?.trim() || null, issueBlockedLabel: d.issueBlockedLabel?.trim() || null });
  return JSON.stringify(normalized(a)) === JSON.stringify(normalized(b));
}
export function savedDefinition(current: Project[], before: Project | undefined, desired: Definition) {
  const matches = before ? current.filter(p => p.id === before.id) : current.filter(p => p.name.toLowerCase() === desired.name.toLowerCase() || p.repository.toLowerCase() === desired.repository.toLowerCase());
  const found = matches.length === 1 ? matches[0] : undefined;
  if (found && sameDefinition(found, desired) && (!before || found.revision === before.revision + 1)) return { state: 'applied' as const, project: found };
  if (!matches.length && !before || found && before && found.revision === before.revision && sameDefinition(found, before)) return { state: 'not-applied' as const, project: found };
  return { state: 'conflict' as const, project: found };
}
export type IssueChange = { kind: 'create' | 'edit'; title: string; body: string } | { kind: 'label'; label: string; applied: boolean } | { kind: 'dependency'; blockerIssueNumber: number; applied: boolean } | { kind: 'enqueue' | 'refresh' };
export function matchesIssue(i: Issue, change: IssueChange) {
  switch (change.kind) {
    case 'create': case 'edit': return i.title === change.title && i.body === change.body;
    case 'label': return i.labels.some(l => l.toLowerCase() === change.label.toLowerCase()) === change.applied;
    case 'dependency': return i.blockedBy.some(b => b.number === change.blockerIssueNumber) === change.applied;
    default: return false;
  }
}
export const projectPath = (id: string) => `/api/v1/projects/${encodeURIComponent(id)}`;
export const issuePath = (id: string, number?: number) => `${projectPath(id)}/github/issues${number ? `/${number}` : ''}`;
export function changeRequest(id: string, number: number | undefined, change: IssueChange) {
  const base = issuePath(id, number);
  switch (change.kind) {
    case 'create': return { path: base, method: 'POST' as const, body: { title: change.title, body: change.body } };
    case 'edit': return { path: base, method: 'PATCH' as const, body: { title: change.title, body: change.body } };
    case 'label': return { path: base + '/labels/configured', method: 'PUT' as const, body: { label: change.label, applied: change.applied } };
    case 'dependency': return { path: base + '/dependencies/blocked-by', method: 'PUT' as const, body: { blockerIssueNumber: change.blockerIssueNumber, applied: change.applied } };
    case 'enqueue': return { path: base + '/enqueue', method: 'POST' as const, body: undefined };
    case 'refresh': return { path: base + '/eligibility/refresh', method: 'POST' as const, body: undefined };
  }
}
