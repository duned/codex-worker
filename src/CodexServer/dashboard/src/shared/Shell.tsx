import { type ReactNode, useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Activity, Folder, HomeLine, LogOut01, Moon01, Server01, Settings01, Sun } from '@untitledui/icons';
import { Button } from '../untitled/components/base/buttons/button';
import { cx } from '../untitled/utils/cx';
import { t, useLanguage, type TranslationKey } from './i18n';
import { usePreferences } from './preferences';
import { useApiRead, useSession } from './api/session';
import { executions as validateExecutions, serverStatus, workers as validateWorkers } from './api/validation';
import { attentionCount, attentionInbox, dateTime } from './model';
import { inputClass, Notice } from './ui';
import { Toasts } from './actions';

const sections: { path: string; label: TranslationKey; icon: typeof HomeLine }[] = [
  { path: '/home', label: 'nav.home', icon: HomeLine },
  { path: '/projects', label: 'nav.projects', icon: Folder },
  { path: '/workers', label: 'nav.workers', icon: Server01 },
  { path: '/executions', label: 'nav.executions', icon: Activity },
  { path: '/settings', label: 'nav.settings', icon: Settings01 }
];

export function Shell({ children }: { children: ReactNode }) {
  const language = useLanguage(), theme = usePreferences(state => state.theme), session = useSession();
  useEffect(() => {
    document.documentElement.classList.toggle('dark-mode', theme === 'dark');
    document.documentElement.style.colorScheme = theme;
    document.documentElement.lang = language;
  }, [theme, language]);
  return <div className="grid min-h-dvh grid-cols-1 bg-primary lg:grid-cols-[272px_minmax(0,1fr)]">
    <Sidebar authenticated={session.authenticated} />
    <main className="min-w-0 px-4 py-6 md:px-8 lg:py-8">
      <div className="mx-auto flex max-w-[1120px] flex-col gap-6">{session.authenticated ? children : <SignIn />}</div>
    </main>
    <Toasts />
  </div>;
}

function Sidebar({ authenticated }: { authenticated: boolean }) {
  const location = useLocation(), section = '/' + (location.pathname.split('/')[1] || 'home');
  return <aside className="flex flex-col gap-1 border-b border-secondary bg-primary px-3 py-4 lg:sticky lg:top-0 lg:h-dvh lg:border-r lg:border-b-0 lg:py-5">
    <Link to="/home" className="flex items-center gap-2.5 px-3 pb-3 text-lg font-semibold text-primary lg:pb-5">
      <Server01 className="size-6 text-fg-brand-primary" aria-hidden="true" />Codex Server
    </Link>
    <nav aria-label={t('nav.main')} className="flex flex-wrap gap-1 lg:flex-col">
      {sections.map(item => <Link key={item.path} to={item.path} aria-current={section === item.path ? 'page' : undefined}
        className={cx('flex items-center justify-between gap-3 rounded-md px-3 py-2 text-md font-semibold text-secondary hover:bg-primary_hover',
          section === item.path && 'bg-secondary text-primary')}>
        <span className="flex items-center gap-3"><item.icon className="size-5 text-fg-quaternary" aria-hidden="true" />{t(item.label)}</span>
        {item.path === '/home' && authenticated && <AttentionCount />}
      </Link>)}
    </nav>
    <SidebarFooter />
  </aside>;
}

function AttentionCount() {
  const executions = useApiRead('/api/v1/executions?limit=100&offset=0', validateExecutions), workers = useApiRead('/api/v1/workers', validateWorkers);
  // The same inbox Home shows, so the badge and the list always agree.
  const count = attentionCount(attentionInbox(executions.data ?? [], workers.data ?? []));
  return count ? <span className="rounded-full bg-utility-red-50 px-2 text-xs font-semibold text-utility-red-700 ring-1 ring-utility-red-200 ring-inset tabular-nums">{count}</span> : null;
}

function SidebarFooter() {
  const language = useLanguage(), session = useSession();
  const { theme, setTheme, setLanguage } = usePreferences();
  return <div className="mt-4 flex flex-col gap-3 px-1 lg:mt-auto">
    <span className="hidden px-2 text-xs font-semibold tracking-wide text-tertiary uppercase lg:block">{t('nav.administration')}</span>
    {session.authenticated && <ServerCard />}
    <div className="flex items-center gap-2">
      <select aria-label={t('nav.language')} value={language} onChange={event => setLanguage(event.target.value === 'en' ? 'en' : 'es')} className={cx(inputClass, 'w-28 shrink-0 py-1.5 text-sm')}>
        <option value="es">Español</option><option value="en">English</option>
      </select>
      <button type="button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label={t(theme === 'dark' ? 'nav.lightTheme' : 'nav.darkTheme')}
        className="grid size-9 shrink-0 place-items-center rounded-full text-fg-warning-secondary ring-1 ring-primary hover:bg-primary_hover">
        {theme === 'dark' ? <Sun className="size-5" aria-hidden="true" /> : <Moon01 className="size-5 text-fg-quaternary" aria-hidden="true" />}
      </button>
      {session.onSignOut && <Button className="shrink-0" size="sm" color="tertiary" iconLeading={LogOut01} onClick={session.onSignOut}>{t('nav.signOut')}</Button>}
    </div>
    <div className="hidden flex-col gap-1 border-t border-secondary px-2 pt-3 text-xs text-tertiary lg:flex">
      <span className="font-semibold tracking-wide uppercase">Codex Server</span><span>{t('nav.controlPlane')}</span>
    </div>
  </div>;
}

function ServerCard() {
  const language = useLanguage(), status = useApiRead('/api/status', serverStatus);
  const ready = !!status.data && status.data.persistenceAvailable !== false && !status.hasError;
  return <div className={cx('grid grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2 gap-y-0.5 rounded-xl bg-primary px-3 py-2.5 ring-1 ring-secondary', ready ? 'tone-ok' : 'tone-bad')}>
    <span className={cx('size-2.5 rounded-full', ready ? 'bg-utility-green-500' : 'bg-utility-red-500')} aria-hidden="true" />
    <strong className="truncate text-sm font-semibold text-primary">{t(status.loading ? 'nav.serverChecking' : ready ? 'nav.serverReady' : 'nav.serverUnavailable')}</strong>
    {status.data?.version && <span className="rounded-md px-1.5 font-mono text-xs font-semibold text-secondary ring-1 ring-primary">v{status.data.version}</span>}
    <small className="col-start-2 col-end-4 text-xs text-tertiary">{t('nav.updated', { time: status.updatedAt ? dateTime(new Date(status.updatedAt).toISOString(), language) : '—' })}</small>
  </div>;
}

function SignIn() {
  const session = useSession();
  return <div className="mx-auto w-full max-w-md py-10">
    <section className="flex flex-col gap-5 rounded-xl bg-primary p-6 shadow-xs ring-1 ring-secondary">
      <div><h1 className="text-xl font-semibold text-primary">{t('signIn.title')}</h1><p className="mt-1 text-sm text-tertiary">{t('signIn.description')}</p></div>
      <form className="flex flex-col gap-4" onSubmit={event => {
        event.preventDefault(); if (session.pending) return;
        const form = event.currentTarget, data = new FormData(form), token = data.get('management-token'), name = String(data.get('operator') ?? '').trim();
        rememberOperator(name); form.reset();
        if (typeof token === 'string' && token) void session.onSignIn(token, name || undefined);
      }}>
        <label className="flex flex-col gap-1.5 text-sm font-medium text-secondary">{t('signIn.operator')}
          <input name="operator" maxLength={64} autoComplete="name" defaultValue={rememberedOperator()} className={inputClass} />
          <span className="text-xs font-normal text-tertiary">{t('signIn.operatorHint')}</span></label>
        <label className="flex flex-col gap-1.5 text-sm font-medium text-secondary">{t('signIn.token')}
          <input name="management-token" type="password" autoComplete="off" required className={inputClass} /></label>
        <Button type="submit" color="primary" isDisabled={session.pending}>{t('signIn.submit')}</Button>
      </form>
      {session.message && <Notice>{session.message}</Notice>}
    </section>
  </div>;
}

// Per-browser convenience only: the name is attribution the operator declares, not an identity the Server verifies.
const operatorKey = 'codex-dashboard-operator';
function rememberedOperator() { try { return localStorage.getItem(operatorKey) ?? ''; } catch { return ''; } }
function rememberOperator(name: string) { try { if (name) localStorage.setItem(operatorKey, name.slice(0, 64)); else localStorage.removeItem(operatorKey); } catch { /* storage unavailable */ } }
