// Canonical paths remain legacy-owned until the final migration cutover.
export const migrationBase = '/dashboard-preview';
export const sections = ['home', 'projects', 'workers', 'executions', 'settings'] as const;
export type Section = typeof sections[number];
export const resourceSections = sections.filter(section => section !== 'home');
export function canonicalPath(pathname: string, search: string): string {
  const path = pathname.startsWith(migrationBase) ? pathname.slice(migrationBase.length) : pathname;
  return (path === '' || path === '/' ? '/home' : path) + search;
}
