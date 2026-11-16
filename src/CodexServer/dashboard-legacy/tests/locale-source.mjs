import { readdirSync, readFileSync } from 'node:fs';
/** Concatenated source of every locale file for one language. */
export function localeSource(language) {
  const directory = new URL(`../src/shared/locales/${language}/`, import.meta.url);
  return readdirSync(directory).sort().map(name => readFileSync(new URL(name, directory), 'utf8')).join('\n');
}
