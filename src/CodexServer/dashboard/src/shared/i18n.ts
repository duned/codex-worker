import { usePreferences, type Language } from './preferences';
import { en, type TranslationKey } from './locales/en';
import { es } from './locales/es';
export type { TranslationKey } from './locales/en';
const resources: Record<Language, Record<TranslationKey, string>> = { en, es };
/** Typed keys and a complete Spanish dictionary make missing text a build error. */
export function t(key: TranslationKey, values: Record<string, string | number> = {}, language: Language = usePreferences.getState().language): string {
  return resources[language][key].replace(/\{(\w+)\}/g, (match, name: string) => String(values[name] ?? match));
}
/** Optional lookup for keys built from Server values; unknown values fall back to the raw value. */
export function tryT(key: string, fallback: string): string {
  const language = usePreferences.getState().language;
  return Object.hasOwn(en, key) ? resources[language][key as TranslationKey] : fallback;
}
export function useLanguage() { return usePreferences(state => state.language); }
