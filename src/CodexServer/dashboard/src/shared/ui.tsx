import { type ReactNode, useEffect, useState } from 'react';
import { Dialog, Modal, ModalOverlay } from 'react-aria-components';
import { Link } from 'react-router-dom';
import { Button } from '../untitled/components/base/buttons/button';
import { cx } from '../untitled/utils/cx';
import { t, useLanguage, type TranslationKey } from './i18n';
import { duration, relative, stepProgress, type Status, type Tone } from './model';
import type { ExecutionSummary } from './api/contracts';

const toneClass: Record<Tone, string> = {
  ok: 'text-utility-green-700 bg-utility-green-50 ring-utility-green-200',
  bad: 'text-utility-red-700 bg-utility-red-50 ring-utility-red-200',
  warn: 'text-utility-yellow-700 bg-utility-yellow-50 ring-utility-yellow-200',
  run: 'text-utility-blue-700 bg-utility-blue-50 ring-utility-blue-200',
  idle: 'text-utility-neutral-700 bg-utility-neutral-50 ring-utility-neutral-200'
};
export const toneText: Record<Tone, string> = { ok: 'text-utility-green-500', bad: 'text-utility-red-500', warn: 'text-utility-yellow-500', run: 'text-utility-blue-500', idle: 'text-utility-neutral-500' };

/** Status pill. Running states pulse. */
export function Pill({ tone, children }: { tone: Tone; children: ReactNode }) {
  return <span className={cx('inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium ring-1 ring-inset', toneClass[tone], `tone-${tone}`)}>
    <span className="status-dot" aria-hidden="true" />{children}
  </span>;
}
export const StatusPill = ({ status }: { status: Status }) => <Pill tone={status.tone}>{t(status.label)}</Pill>;

/** A coloured dot followed by text, for facts such as connection state. */
export function DotText({ tone, children }: { tone: Tone; children: ReactNode }) {
  return <span className={cx('inline-flex items-center gap-2 font-medium text-primary', `tone-${tone}`)}>
    <span className={cx('status-dot size-2', toneText[tone])} aria-hidden="true" />{children}
  </span>;
}

export function Card({ title, actions, children, className, flush }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; flush?: boolean }) {
  return <section className={cx('min-w-0 rounded-xl bg-primary shadow-xs ring-1 ring-secondary', className)}>
    {(title || actions) && <header className="flex flex-wrap items-center justify-between gap-3 border-b border-secondary px-5 py-4">
      {typeof title === 'string' ? <h2 className="text-md font-semibold text-primary">{title}</h2> : title}
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>}
    {flush ? children : <div className="px-5 py-4">{children}</div>}
  </section>;
}

/** Rows inside a card, separated by lines. */
export function Rows({ children }: { children: ReactNode }) { return <div className="divide-y divide-secondary">{children}</div>; }
export function RowLink({ to, children, className }: { to: string; children: ReactNode; className?: string }) {
  return <Link to={to} className={cx('grid items-center gap-x-4 gap-y-1 px-5 py-3.5 outline-focus-ring hover:bg-primary_hover focus-visible:outline-2', className)}>{children}</Link>;
}
export function Row({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cx('grid items-center gap-x-4 gap-y-1 px-5 py-3.5', className)}>{children}</div>;
}
export function HeadRow({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cx('hidden items-center gap-x-4 bg-secondary px-5 py-2.5 text-xs font-semibold text-tertiary md:grid', className)}>{children}</div>;
}

export function PageHeader({ title, description, actions, crumbs, badge }: { title: ReactNode; description?: ReactNode; actions?: ReactNode; crumbs?: { label: ReactNode; to?: string }[]; badge?: ReactNode }) {
  return <div className="flex flex-col gap-3">
    {crumbs && <nav aria-label={t('common.breadcrumbs')} className="flex flex-wrap gap-1.5 text-sm text-tertiary">
      {crumbs.map((crumb, index) => <span key={index} className="flex gap-1.5">{index > 0 && <span aria-hidden="true">/</span>}
        {crumb.to ? <Link className="hover:text-secondary" to={crumb.to}>{crumb.label}</Link> : <span className="text-secondary">{crumb.label}</span>}</span>)}
    </nav>}
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-3"><h1 className="text-display-xs font-semibold text-balance text-primary" tabIndex={-1}>{title}</h1>{badge}</div>
        {description && <div className="mt-1 text-md text-tertiary">{description}</div>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  </div>;
}

/** Live duration since a timestamp, refreshed every second. */
export function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const id = setInterval(() => setNow(Date.now()), intervalMs); return () => clearInterval(id); }, [intervalMs]);
  return now;
}
export function Ago({ value }: { value?: string }) {
  useLanguage(); const now = useNow(15000); const ago = relative(value, now);
  return <>{ago ? t(ago.key, { n: ago.n }) : t('common.never')}</>;
}

/** The five lifecycle steps of an execution with per-step durations. */
export function Steps({ execution }: { execution: ExecutionSummary }) {
  useLanguage(); useNow();
  const items = stepProgress(execution);
  return <ol className="grid grid-cols-5 gap-1">
    {items.map(item => <li key={item.name} className={cx('flex min-w-0 flex-col gap-1.5', `step-${item.state}`)}>
      <span aria-hidden="true" className={cx('step-bar w-full', {
        done: 'bg-utility-green-500', now: 'bg-utility-blue-500', fail: 'bg-utility-red-500', todo: 'bg-quaternary'
      }[item.state])} />
      <span className="min-w-0">
        <span className={cx('block truncate text-xs', item.state === 'todo' ? 'text-quaternary' : item.state === 'fail' ? 'font-semibold text-error-primary' : item.state === 'now' ? 'font-semibold text-primary' : 'text-secondary')}>
          {t(`step.${item.name}` as TranslationKey)}
        </span>
        <span className="block text-xs text-quaternary tabular-nums">{item.state === 'todo' ? '—' : duration(item.durationMs)}</span>
      </span>
    </li>)}
  </ol>;
}

/** Work slots as small bars; occupied slots shimmer while work runs. */
export function Slots({ used, total }: { used: number; total: number }) {
  return <span className="flex gap-1" title={t('workers.slotsTitle', { used, total })}>
    {Array.from({ length: Math.min(total, 8) }, (_, index) => <i key={index} className={cx('slot h-2 w-5 rounded-sm', index < used ? 'slot-on bg-brand-solid' : 'bg-quaternary')} />)}
  </span>;
}

/** Search box plus status select shared by every list. */
export function Filters({ query, onQuery, status, onStatus, options, placeholder, children }: {
  query: string; onQuery(value: string): void; status: string; onStatus(value: string): void; options: { value: string; label: string }[]; placeholder: string; children?: ReactNode;
}) {
  return <div className="flex flex-wrap gap-2 border-b border-secondary px-5 py-3">
    <input type="search" aria-label={placeholder} placeholder={placeholder} value={query} onChange={event => onQuery(event.target.value)}
      className="min-w-0 flex-[1_1_220px] rounded-lg bg-primary px-3 py-2 text-md text-primary shadow-xs ring-1 ring-primary outline-none ring-inset placeholder:text-placeholder focus:ring-2 focus:ring-brand" />
    <Select label={t('common.status')} value={status} onChange={onStatus} options={[{ value: '', label: t('common.allStatuses') }, ...options]} hideLabel />
    {children}
  </div>;
}
export function matches(text: string, query: string) { return !query.trim() || text.toLowerCase().includes(query.trim().toLowerCase()); }

export function Select({ label, value, onChange, options, hideLabel, hint }: { label: string; value: string; onChange(value: string): void; options: { value: string; label: string }[]; hideLabel?: boolean; hint?: string }) {
  return <label className={cx('flex flex-col gap-1.5 text-sm font-medium text-secondary', hideLabel && 'flex-[0_1_200px]')}>
    <span className={hideLabel ? 'sr-only' : undefined}>{label}</span>
    <select value={value} onChange={event => onChange(event.target.value)}
      className="rounded-lg bg-primary px-3 py-2 text-md font-normal text-primary shadow-xs ring-1 ring-primary outline-none ring-inset focus:ring-2 focus:ring-brand">
      {options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>
    {hint && <span className="text-sm font-normal text-tertiary">{hint}</span>}
  </label>;
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <label className="flex flex-col gap-1.5 text-sm font-medium text-secondary">{label}{children}{hint && <span className="text-sm font-normal text-tertiary">{hint}</span>}</label>;
}
export const inputClass = 'w-full rounded-lg bg-primary px-3 py-2 text-md font-normal text-primary shadow-xs ring-1 ring-primary outline-none ring-inset placeholder:text-placeholder focus:ring-2 focus:ring-brand';

export function Check({ checked, onChange, children }: { checked: boolean; onChange(value: boolean): void; children: ReactNode }) {
  return <label className="flex items-start gap-3 text-sm text-secondary">
    <input type="checkbox" className="mt-0.5 size-4 accent-[var(--color-bg-brand-solid)]" checked={checked} onChange={event => onChange(event.target.checked)} />
    <span>{children}</span>
  </label>;
}

export function KeyValues({ items }: { items: [ReactNode, ReactNode][] }) {
  return <dl className="grid grid-cols-[minmax(0,38%)_minmax(0,1fr)] gap-x-5 gap-y-2.5 text-sm">
    {items.map(([key, value], index) => <div key={index} className="contents"><dt className="text-tertiary">{key}</dt><dd className="min-w-0 break-words text-primary">{value}</dd></div>)}
  </dl>;
}

export function Tile({ label, tone, value, detail }: { label: string; tone: Tone; value: ReactNode; detail?: ReactNode }) {
  return <div className={cx('flex flex-col gap-1 rounded-xl bg-primary px-4 py-3 shadow-xs ring-1 ring-secondary', `tone-${tone}`)}>
    <span className="text-xs font-semibold tracking-wide text-tertiary uppercase">{label}</span>
    <span className="flex items-center gap-2 text-lg font-semibold text-primary"><span className={cx('status-dot size-2', toneText[tone])} aria-hidden="true" />{value}</span>
    {detail && <span className="text-sm text-tertiary">{detail}</span>}
  </div>;
}

/** What happened / what you can do, for a finished execution that needs a decision. */
export function Explanation({ what, todo, children }: { what: string; todo: string; children?: ReactNode }) {
  return <div className="flex flex-col gap-3 border-l-[3px] border-utility-red-500 pl-4">
    <div><div className="text-xs font-semibold tracking-wide text-tertiary uppercase">{t('explain.whatHappened')}</div><p className="text-md text-primary">{what}</p></div>
    <div><div className="text-xs font-semibold tracking-wide text-tertiary uppercase">{t('explain.whatYouCanDo')}</div><p className="text-md text-primary">{todo}</p></div>
    {children}
  </div>;
}

export function Notice({ tone = 'idle', children }: { tone?: Tone; children: ReactNode }) {
  return <div role={tone === 'bad' ? 'alert' : 'status'} className={cx('flex items-start gap-3 rounded-xl px-4 py-3 text-sm ring-1 ring-inset', toneClass[tone], `tone-${tone}`)}>
    <span className="status-dot mt-1.5" aria-hidden="true" /><div className="min-w-0">{children}</div>
  </div>;
}
export function Empty({ children }: { children: ReactNode }) { return <p className="px-5 py-4 text-sm text-tertiary">{children}</p>; }
export function Loading() { return <p className="px-5 py-4 text-sm text-tertiary" role="status">{t('common.loading')}</p>; }

/** State of a read: loading, failed (with retry) or ready. */
export function ReadState({ read, children }: { read: { loading: boolean; hasError: boolean; error?: string; refetch(): unknown }; children: ReactNode }) {
  if (read.loading) return <Loading />;
  if (read.hasError) return <div className="px-5 py-4"><Notice tone="bad">{read.error ?? t('common.loadFailed')} <button className="font-semibold underline" onClick={() => void read.refetch()}>{t('common.retry')}</button></Notice></div>;
  return <>{children}</>;
}

/** Side panel for editing. Escape and the backdrop close it. */
export function SidePanel({ title, open, onClose, children, footer }: { title: string; open: boolean; onClose(): void; children: ReactNode; footer?: ReactNode }) {
  return <ModalOverlay isOpen={open} onOpenChange={value => { if (!value) onClose(); }} isDismissable className="fixed inset-0 z-50 bg-overlay/70 backdrop-blur-[2px]">
    <Modal className="side-panel fixed inset-y-0 right-0 flex w-full max-w-md flex-col bg-primary shadow-xl ring-1 ring-secondary">
      <Dialog aria-label={title} className="flex h-full flex-col outline-none">
        <header className="flex items-center justify-between gap-3 border-b border-secondary px-6 py-4">
          <h2 className="text-lg font-semibold text-primary">{title}</h2>
          <Button size="sm" color="tertiary" onClick={onClose}>{t('common.close')}</Button>
        </header>
        <div className="flex min-h-0 flex-1 flex-col gap-5 overflow-y-auto px-6 py-5">{children}</div>
        {footer && <footer className="flex justify-end gap-2 border-t border-secondary px-6 py-4">{footer}</footer>}
      </Dialog>
    </Modal>
  </ModalOverlay>;
}
