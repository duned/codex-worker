import { usePreferences, type Language } from './preferences';
import { en, type TranslationKey } from './locales/en';
import { es } from './locales/es';
export type { TranslationKey } from './locales/en';
export const resources = { en, es };
/** Typed feature keys and complete Spanish resources make omissions build errors. */
export function t(key: TranslationKey, values: Record<string, string | number> = {}, language: Language = usePreferences.getState().language): string {
  return resources[language][key].replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match));
}
const textKeys = new Map<string, TranslationKey>();
for (const key of Object.keys(en) as TranslationKey[]) {
  textKeys.set(en[key], key);
  textKeys.set(es[key], key);
}
/** Translate retained dashboard messages without storing language-specific state.
 * Unknown external text is preserved verbatim; React escapes it on presentation. */
const templates = (Object.keys(en) as TranslationKey[]).flatMap(key => [en[key], es[key]].filter(text => text.includes('{')).map(text => {
  const names: string[] = [];
  const pattern = text.split(/(\{\w+\})/g).map(part => {
    if (/^\{\w+\}$/.test(part)) { names.push(part.slice(1, -1)); return '(.*?)'; }
    return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }).join('');
  return { key, names, pattern: new RegExp(`^${pattern}$`, 's') };
}));
export function localizeText(value: string): string {
  const key = textKeys.get(value);
  if (key) return t(key);
  for (const template of templates) {
    const match = template.pattern.exec(value);
    if (match) return t(template.key, Object.fromEntries(template.names.map((name, index) => [name, ['state', 'error', 'title'].includes(name) ? localizeText(match[index + 1]) : match[index + 1]])));
  }
  return statusLabel(value);
}
export function statusLabel(value: string | null | undefined): string {
  if (value == null || value === '') return t('shared.unknown');
  const key = `status.${value.toLowerCase()}`;
  return usePreferences.getState().language === 'es' && Object.hasOwn(en, key) ? t(key as TranslationKey) : value;
}
export function useLanguage() { return usePreferences(state => state.language); }
