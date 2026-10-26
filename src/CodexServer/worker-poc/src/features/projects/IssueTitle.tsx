import { t, useLanguage } from '../../shared/i18n';
import { Link } from 'react-router-dom';
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
  if (!issueLink(workReference, projectId)) {
    return <span className={className}>#{workReference.id} · {t('projects.issueTitleUnavailable')}</span>;
  }
  return <IssueTitleRead projectId={projectId} repository={repository} workReference={workReference} className={className} />;
}

function IssueTitleRead({ projectId, workReference, className }: {
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
  const title = read.data?.title.trim();
  const text = title ? `#${workReference.id} · ${title}` : read.error || read.data
    ? `#${workReference.id} · ${t('projects.issueTitleUnavailable')}`
    : `#${workReference.id} · ${t('projects.loadingIssueTitle')}`;
  const href = issueLink(workReference, projectId);
  return href ? <Link to={href} className={className}>{text}</Link> : <span className={className}>{text}</span>;
}

export function projectRepositoryUrl(project: { repository: string }) {
  return /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(project.repository)
    && !project.repository.split('/').some(part => part === '.' || part === '..')
    ? `https://github.com/${project.repository}`
    : undefined;
}
