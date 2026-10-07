// One canonical application; historical preview paths are redirected by the Server.
export const migrationBase = '/dashboard-preview';
export const sections = ['home', 'projects', 'workers', 'executions', 'settings'] as const;
export type Section = typeof sections[number];
export const resourceSections = sections.filter(section => section !== 'home');
export function canonicalPath(pathname: string, search: string): string {
  const path = pathname.startsWith(migrationBase) ? pathname.slice(migrationBase.length) : pathname;
  return (path === '' || path === '/' ? '/home' : path) + search;
}

export function normalizeBookmark(pathname: string, search: string, hash: string): string | null {
  if (hash.startsWith('#/')) {
    let url: URL;
    try { url = new URL(hash.slice(1), 'https://dashboard.invalid'); }
    catch { return pathname === '/' ? '/home' + search : null; }
    const parts = url.pathname.split('/').filter(Boolean);
    if (url.origin === 'https://dashboard.invalid' && !url.hash &&
        sections.some(section => section === parts[0]) && parts.length <= 2 &&
        (parts[0] !== 'home' || parts.length === 1)) return url.pathname + url.search;
  }
  return pathname === '/' ? '/home' + search : null;
}
