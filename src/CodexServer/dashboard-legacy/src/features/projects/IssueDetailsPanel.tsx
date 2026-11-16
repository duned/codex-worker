import { useLanguage, t } from '../../shared/i18n';
import { Notice, StatusBadge } from '../../shared/Presentation';
import { Button } from '../../untitled/components/base/buttons/button';
import { timestamp } from '../../model';
import type { Issue, Project } from './contracts';

export function IssueDetailsPanel({ issue, project, projectId, issueError, projectError, onRetryProject }: {
  issue?: Issue;
  project?: Project;
  projectId: string;
  issueError: unknown;
  projectError: boolean;
  onRetryProject: () => void;
}) {
  useLanguage();
  return <section aria-labelledby="issue-details-heading" className="min-w-0 rounded-xl border border-secondary bg-primary p-4 shadow-xs">
    <h2 id="issue-details-heading" className="text-sm font-semibold text-primary">{t('issues.issueDetails')}</h2>
    {issue ? <dl className="mt-3 grid min-w-0 grid-cols-1 gap-y-3 text-sm">
      <MetadataItem label={t('issues.author')} value={issue.author || t('issues.notReported')} />
      <MetadataItem label={t('issues.assignees')} value={issue.assignees.length ? issue.assignees.join(', ') : t('issues.unassigned')} />
      <MetadataItem label={t('issues.milestone')} value={issue.milestone || t('issues.none')} />
      <MetadataItem label={t('issues.project')} value={project?.name ?? projectId} />
      <MetadataItem label={t('issues.created')} value={timestamp(issue.createdAtUtc)} />
      <MetadataItem label={t('issues.updated')} value={timestamp(issue.updatedAtUtc)} />
      <div className="min-w-0">
        <dt className="break-words text-xs text-tertiary [overflow-wrap:anywhere]">{t('issues.labels')}</dt>
        <dd className="mt-1 flex min-w-0 flex-wrap gap-1.5">
          {issue.labels.length ? issue.labels.map(label => <StatusBadge key={label} tone="purple" compact><span className="whitespace-normal break-words [overflow-wrap:anywhere]">{label}</span></StatusBadge>) : <span className="text-sm text-secondary">{t('issues.none')}</span>}
        </dd>
      </div>
    </dl> : <p className="mt-2 break-words text-sm text-tertiary [overflow-wrap:anywhere]">{issueError ? t('issues.metadataUnavailable') : t('issues.loadingGitHub')}</p>}
    {projectError && <div className="mt-3 flex min-w-0 flex-wrap items-center gap-2"><Notice error>{t('issues.projectUnavailable')}</Notice><Button color="secondary" onPress={onRetryProject}>{t('issues.retryProject')}</Button></div>}
  </section>;
}

function MetadataItem({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0">
    <dt className="break-words text-xs text-tertiary [overflow-wrap:anywhere]">{label}</dt>
    <dd className="mt-1 break-words font-medium text-secondary [overflow-wrap:anywhere]">{value}</dd>
  </div>;
}
