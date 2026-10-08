import { t, useLanguage, localizeText } from './i18n';
import { type ReactNode } from 'react';
import { Link } from 'react-aria-components';
import { Badge } from '../untitled/components/base/badges/badges';
import { TableCard } from '../untitled/components/application/table/table';

/** Callers choose tone from reported evidence; this component never decides readiness. */
export function StatusBadge({ children, tone = 'gray' }: { children: ReactNode; tone?: 'gray' | 'success' | 'warning' | 'error' }) {
  useLanguage();
  return <Badge className="inline-flex max-w-full whitespace-normal align-middle" type="pill-color" size="lg" color={tone}>{typeof children === 'string' ? localizeText(children) : children}</Badge>;
}
export function ResourceIdentity({ name, id }: { name: string; id: string }) {
  useLanguage();
  return <><span className="font-medium text-primary">{name}</span><span className="block break-all text-xs text-tertiary">{t("shared.iD")}{' '}{id}</span></>;
}
export function PageHeading({ title, resourceId, breadcrumbs = [], actions }: {
  title: string; resourceId?: string; breadcrumbs?: { label: string; href?: string }[]; actions?: ReactNode;
}) {
  useLanguage();
  return <header className="mb-8 flex flex-wrap items-start justify-between gap-4">
    <div className="min-w-0">
      {breadcrumbs.length > 0 && <nav aria-label={t("shared.breadcrumb")} className="mb-2 text-sm text-tertiary"><ol className="flex flex-wrap gap-2">
        {breadcrumbs.map((item, index) => <li key={`${item.label}-${index}`}>{index > 0 && <span aria-hidden="true">/ </span>}{item.href ? <Link href={item.href}>{item.label}</Link> : <span aria-current="page">{item.label}</span>}</li>)}
      </ol></nav>}
      <h1 tabIndex={-1} className="text-display-sm font-semibold text-primary">{title}</h1>
      {resourceId && <p className="mt-2 break-all text-sm text-tertiary">{t("shared.iD")}{' '}{resourceId}</p>}
    </div>{actions}
  </header>;
}
export function Notice({ children, error = false }: { children: ReactNode; error?: boolean }) {
  useLanguage();
  return <div role={error ? 'alert' : 'status'} className={`rounded-lg border border-secondary bg-secondary p-4 text-sm ${error ? 'text-error-primary' : 'text-secondary'}`}>{typeof children === 'string' ? localizeText(children) : children}</div>;
}
export function ViewState({ title, children, error = false }: { title: string; children?: ReactNode; error?: boolean }) {
  useLanguage();
  return <TableCard.Root><div className="space-y-2 p-5"><Notice error={error}>{title}</Notice>{children}</div></TableCard.Root>;
}
export function AdvancedDisclosure({ title = t("shared.advancedDetails"), children }: { title?: string; children: ReactNode }) {
  useLanguage();
  return <details className="mt-4 text-sm text-tertiary"><summary className="font-medium">{title}</summary><div className="mt-3 space-y-2">{children}</div></details>;
}
