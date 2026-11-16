// Served by the Server under /old next to the current dashboard; router paths exclude this base.
export const basename = '/old';
const legacyPreviewBase = '/dashboard-preview';
const sectionPath = /^\/(home|projects|workers|executions|settings)(?:\/|\?|#|$)/;
// Raw anchors in this backup still use root-relative section paths; keep them inside /old.
export function legacyPath(href: string): string | null {
  return sectionPath.test(href) ? basename + href : null;
}
export const sections = ['home', 'projects', 'workers', 'executions', 'settings'] as const;
export type Section = typeof sections[number];
export const resourceSections = sections.filter(section => section !== 'home');
export function canonicalPath(pathname: string, search: string): string {
  const path = pathname.startsWith(legacyPreviewBase) ? pathname.slice(legacyPreviewBase.length) : pathname;
  return (path === '' || path === '/' ? '/home' : path) + search;
}

export function normalizeBookmark(fullPathname: string, search: string, hash: string): string | null {
  const pathname = fullPathname === basename ? '/' : fullPathname.startsWith(basename + '/') ? fullPathname.slice(basename.length) : fullPathname;
  const result = normalizeBookmarkPath(pathname, search, hash);
  return result === null ? null : basename + result;
}

function normalizeBookmarkPath(pathname: string, search: string, hash: string): string | null {
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
