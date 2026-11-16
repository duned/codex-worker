import { create } from 'zustand';
import { createJSONStorage, persist, type StateStorage } from 'zustand/middleware';
export type Language = 'en' | 'es';
interface Preferences { language: Language; setLanguage(language: Language): void; theme: 'dark' | 'light'; setTheme(theme: 'dark' | 'light'): void }
const browserStorage: StateStorage = {
  getItem: key => { try { return localStorage.getItem(key); } catch { return null; } },
  setItem: (key, value) => { try { localStorage.setItem(key, value); } catch { /* Visit preference still works. */ } },
  removeItem: key => { try { localStorage.removeItem(key); } catch { /* Storage unavailable. */ } }
};
function versionedPreference(value: string | null): string | null {
  try { return value && JSON.parse(value)?.version === 1 ? value : null; }
  catch { return null; }
}
export function createPreferences(storage: StateStorage = browserStorage) {
  // Allowlist on both write and rehydration, including untrusted/corrupt storage.
  const safe: StateStorage = {
    getItem: key => {
      try {
        const value = storage.getItem(key);
        return typeof value === 'string' || value === null ? versionedPreference(value) : value.then(versionedPreference, () => null);
      } catch { return null; }
    },
    setItem: (key, value) => { try { return storage.setItem(key, value); } catch { return; } },
    removeItem: key => { try { return storage.removeItem(key); } catch { return; } }
  };
  return create<Preferences>()(persist(set => ({ language: 'en', setLanguage: language => set({ language }), theme: 'dark', setTheme: theme => set({ theme }) }), {
    name: 'codex-dashboard-preferences', version: 1, storage: createJSONStorage(() => safe),
    migrate: () => ({ theme: 'dark' as const }),
    partialize: state => ({ theme: state.theme, language: state.language }),
    merge: (persisted, current) => ({ ...current, language: persisted && typeof persisted === 'object' && 'language' in persisted && persisted.language === 'es' ? 'es' : 'en', theme: persisted && typeof persisted === 'object' && 'theme' in persisted && persisted.theme === 'light' ? 'light' : 'dark' })
  }));
}
export const usePreferences = createPreferences();
