import { t, useLanguage } from '../../shared/i18n';
import { ExternalLink } from '../../shared/Actions';
import { useApiRead } from '../../shared/api/session';
import type { ExecutionSummary } from '../../shared/api/contracts';
import { issue } from './contracts';
import { issuePath } from './model';
import { issueLink } from '../../model';

export function IssueTitle({ projectId, repository, workReference, className }: {
  projectId: string;
  repository?: string;
  workReference?: ExecutionSummary['workReference'];
  className?: string;
}) {
  useLanguage();
  if (workReference?.type !== 'github-issue' || !/^[1-9]\d*$/.test(workReference.id) || Number(workReference.id) > 2147483647) {
    return <span className={className}>{workReference ? `${workReference.type} ${workReference.id}` : t('executions.workReferenceUnavailable')}</span>;
  }
  return <IssueTitleRead projectId={projectId} repository={repository} workReference={workReference} className={className} />;
}

function IssueTitleRead({ projectId, repository, workReference, className }: {
  projectId: string;
  repository?: string;
  workReference: NonNullable<ExecutionSummary['workReference']>;
  className?: string;
}) {
  useLanguage();
  const issueNumber = Number(workReference.id);
  const read = useApiRead(issuePath(projectId, issueNumber), value => {
    const observed = issue(value);
    if (observed.number !== issueNumber) throw Error('Issue identity mismatch.');
    return observed;
  });
  const text = read.data ? `#${workReference.id} · ${read.data.title}` : read.error
    ? `#${workReference.id} · ${t('projects.issueTitleUnavailable')}`
    : `#${workReference.id} · ${t('projects.loadingIssueTitle')}`;
  const href = issueLink(workReference, repository);
  return href ? <ExternalLink href={href} className={className}>{text}</ExternalLink> : <span className={className}>{text}</span>;
}

export function projectRepositoryUrl(project: { repository: string }) {
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(project.repository)
    && !project.repository.split('/').some(part => part === '.' || part === '..')
    ? `https://github.com/${project.repository}`
    : undefined;
}
