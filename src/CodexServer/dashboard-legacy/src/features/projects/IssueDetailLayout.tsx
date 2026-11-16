import type { ReactNode } from 'react';

/** Keeps the Issue description in the main column and stacks status/details in the sidebar. */
export function IssueDetailLayout({ description, status, details }: {
  description: ReactNode;
  status: ReactNode;
  details: ReactNode;
}) {
  return <div className="mt-6 grid min-w-0 items-start gap-4 lg:grid-cols-[minmax(0,1.7fr)_minmax(18rem,1fr)]">
    {description}
    <div className="flex min-w-0 flex-col gap-4">
      {status}
      {details}
    </div>
  </div>;
}
