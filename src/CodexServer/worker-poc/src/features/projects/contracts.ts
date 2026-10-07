import type { Validator } from '../../shared/api/client';
import { record } from '../../shared/api/validation';
export interface Requirement { type: string; name: string; version: string | null; scope: string | null }
export interface Discovery { enabled: boolean; intervalSeconds: number; pageSize: number; deadlineSeconds: number }
export interface Definition {
  name: string; repository: string; defaultBranch: string; description: string; requirements: Requirement[];
  issueReadyLabel: string | null; issueBlockedLabel: string | null; automaticDiscovery: Discovery | null;
}
export interface Project extends Definition { id: string; revision: number; enabled: boolean }
export interface Repository { repository: string; name: string; defaultBranch: string; description: string }
export interface BlockingIssue { number: number; title: string; state: string; url: string }
export interface Issue extends BlockingIssue { body: string; labels: string[]; blockedBy: BlockingIssue[]; isEligible: boolean; eligibilityReasons: string[] }
export interface IssueResult { operation: string; repository: string; previewOnly: boolean; changed: boolean; issueNumber: number | null; title: string | null; body: string | null; label: string | null; applied: boolean | null; relatedIssueNumber: number | null }
function strings(item: Record<string, unknown>, keys: string[]) {
  if (keys.some(key => typeof item[key] !== 'string')) throw Error('Invalid text.');
}
function bool(item: Record<string, unknown>, key: string) { if (typeof item[key] !== 'boolean') throw Error('Invalid state.'); }
function integer(item: Record<string, unknown>, key: string, min = 1, max = Number.MAX_SAFE_INTEGER) {
  const value = item[key]; if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < min || value > max) throw Error('Invalid number.');
}
function nullableText(item: Record<string, unknown>, keys: string[]) { for (const key of keys) if (item[key] != null && typeof item[key] !== 'string') throw Error('Invalid optional text.'); }
export function array<T>(value: unknown, validate: Validator<T>, max = 10000): T[] {
  if (!Array.isArray(value) || value.length > max) throw Error('Invalid list.'); return value.map(validate);
}
const stringList = (value: unknown) => array(value, item => { if (typeof item !== 'string') throw Error('Invalid text.'); return item; });
export const project: Validator<Project> = value => {
  const p = record(value); strings(p, ['id', 'name', 'repository', 'defaultBranch', 'description']); integer(p, 'revision'); bool(p, 'enabled');
  nullableText(p, ['issueReadyLabel', 'issueBlockedLabel']);
  array(p.requirements, value => { const r = record(value); strings(r, ['type', 'name']); nullableText(r, ['version', 'scope']); return r; }, 64);
  if (p.automaticDiscovery != null) {
    const d = record(p.automaticDiscovery); bool(d, 'enabled'); integer(d, 'intervalSeconds', 30, 86400); integer(d, 'pageSize', 1, 100); integer(d, 'deadlineSeconds', 10, 120);
  }
  return value as Project;
};
export const projectList: Validator<Project[]> = value => array(value, project);
export const repositoryPage = (value: unknown) => {
  const p = record(value); const repositories = array(p.repositories, value => { const r = record(value); strings(r, ['repository', 'name', 'defaultBranch', 'description']); return value as Repository; }, 50);
  if (p.nextPage != null) integer(p, 'nextPage', 1, 1000);
  return { repositories, nextPage: p.nextPage == null ? null : Number(p.nextPage) };
};
export const verification = (value: unknown) => {
  const p = record(value); strings(p, ['repository', 'defaultBranch', 'diagnostic']); bool(p, 'repositoryReadable'); bool(p, 'branchExists');
  return { repository: String(p.repository), defaultBranch: String(p.defaultBranch), repositoryReadable: p.repositoryReadable === true, branchExists: p.branchExists === true, diagnostic: String(p.diagnostic) };
};
export const access = (value: unknown) => {
  const p = record(value); strings(p, ['repository', 'checkedAtUtc']); nullableText(p, ['diagnostic']); bool(p, 'cliAuthenticated'); bool(p, 'repositoryReadable');
  return { cliAuthenticated: p.cliAuthenticated === true, repositoryReadable: p.repositoryReadable === true, checkedAtUtc: String(p.checkedAtUtc) };
};
export function issueUrl(repository: string, item: BlockingIssue) {
  // Use the canonical identity, never a provider-supplied navigation authority.
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) ? `https://github.com/${repository}/issues/${item.number}` : undefined;
}
export const blockingIssue: Validator<BlockingIssue> = value => {
  const i = record(value); strings(i, ['title', 'state', 'url']); integer(i, 'number', 1, 2147483647); return value as BlockingIssue;
};
export const issue: Validator<Issue> = value => {
  blockingIssue(value); const i = record(value); strings(i, ['body']); bool(i, 'isEligible'); stringList(i.labels); stringList(i.eligibilityReasons); array(i.blockedBy, blockingIssue); return value as Issue;
};
export const issueList: Validator<Issue[]> = value => array(value, issue, 100);
export const issueResult: Validator<IssueResult> = value => {
  const i = record(value); strings(i, ['operation', 'repository']); bool(i, 'previewOnly'); bool(i, 'changed');
  nullableText(i, ['title', 'body', 'label', 'url']);
  for (const key of ['issueNumber', 'relatedIssueNumber']) if (i[key] != null) integer(i, key, 1, 2147483647);
  if (i.applied != null) bool(i, 'applied');
  return value as IssueResult;
};
export const empty = (value: unknown) => { if (value !== null) throw Error('Invalid empty response.'); return null; };
