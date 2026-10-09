import { t, useLanguage } from './i18n';

export function formatGuid(value: string): string {
  return value.slice(0, 7);
}

/** Shows a concise GUID while keeping the full value available to pointer and assistive users. */
export function GuidDisplay({ value, className }: { value: string; className?: string }) {
  useLanguage();
  return <span className={className} title={value} aria-label={t('shared.shortGuidAccessible', { short: formatGuid(value), full: value })}>{formatGuid(value)}</span>;
}
