import { useEffect, useId } from 'react';
import { usePreferences } from './preferences';
import { t } from './i18n';
import { ChevronDown, Globe01 } from '@untitledui/icons';
export function LanguageControl() {
  const id = useId();
  const language = usePreferences(state => state.language), setLanguage = usePreferences(state => state.setLanguage);
  useEffect(() => { document.documentElement.lang = language; }, [language]);
  return <div className="relative flex h-[42px] min-w-0 flex-1 items-center gap-1 rounded-lg border border-secondary bg-secondary px-2 text-xs text-secondary focus-within:ring-2 focus-within:ring-focus-ring">
    <label htmlFor={id} className="sr-only">{t('shared.language')}</label>
    <Globe01 aria-hidden="true" className="size-4 shrink-0 text-fg-quaternary" />
    <span aria-hidden="true" className="min-w-0 flex-1 truncate">{language === 'es' ? 'Español' : 'English'}</span>
    <ChevronDown aria-hidden="true" className="size-3.5 shrink-0 text-fg-quaternary" />
    <select id={id} aria-label={t('shared.language')} className="absolute inset-0 size-full cursor-pointer appearance-none opacity-0" value={language} onChange={event => setLanguage(event.target.value === 'es' ? 'es' : 'en')}>
      <option value="en" lang="en">English</option><option value="es" lang="es">Español</option>
    </select>
  </div>;
}
