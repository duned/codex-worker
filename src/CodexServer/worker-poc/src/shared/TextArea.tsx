import { useLanguage } from './i18n';
import { useId } from 'react';
/** Multiline form composition with the same public surface/focus tokens as Input. */
export function TextArea({ label, value, onChange, maxLength }: { label: string; value: string; onChange(value: string): void; maxLength: number }) {
  useLanguage();
  const id = useId();
  return <div className="flex flex-col gap-1.5"><label htmlFor={id} className="text-sm font-medium text-secondary">{label}</label>
    <textarea id={id} className="min-h-32 w-full rounded-lg bg-primary px-3 py-2 text-md text-primary ring-1 ring-primary ring-inset outline-none focus:ring-2 focus:ring-brand" value={value} maxLength={maxLength} onChange={event => onChange(event.target.value)} />
  </div>;
}
