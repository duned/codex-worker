import { useEffect, useId } from 'react';
import { usePreferences } from './preferences';
import { t } from './i18n';
export function LanguageControl() {
  const id = useId();
  const language = usePreferences(state => state.language), setLanguage = usePreferences(state => state.setLanguage);
  useEffect(() => { document.documentElement.lang = language; }, [language]);
  return <div className="flex flex-col gap-1 text-sm text-secondary"><label htmlFor={id}>{t('shared.language')}</label>
    <select id={id} className="rounded-lg border border-secondary bg-primary p-2" value={language} onChange={event => setLanguage(event.target.value === 'es' ? 'es' : 'en')}>
      <option value="en" lang="en">English</option><option value="es" lang="es">Español</option>
    </select>
  </div>;
}
