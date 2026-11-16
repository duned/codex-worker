import { LanguageControl } from './LanguageControl';
import { t, useLanguage, localizeText } from './i18n';
import { ServerStatus } from './ServerStatus';
import { type ReactNode } from 'react';
import { HomeLine, Folder, Server01, Activity, Settings01, LogOut01 } from '@untitledui/icons';
import { SidebarNavigationSimple } from '../untitled/components/application/app-navigation/sidebar-navigation/sidebar-simple';
import { Button } from '../untitled/components/base/buttons/button';
import { Input } from './Input';
import { AdvancedDisclosure, Notice } from './Presentation';
import { TableCard } from '../untitled/components/application/table/table';

const items = [
  { label: t("shared.home"), href: '/home', icon: HomeLine },
  { label: t("shared.projects"), href: '/projects', icon: Folder },
  { label: t("shared.workers"), href: '/workers', icon: Server01 },
  { label: t("shared.executions"), href: '/executions', icon: Activity },
  { label: t("shared.settings"), href: '/settings', icon: Settings01 }
];
interface ShellSession {
  authenticated: boolean; pending?: boolean; message?: string;
  onSignOut?: () => void; onSignIn?: (token: string) => void; onRestore?: () => void;
}
export function Application({ session, children, navigationItems = items, activeUrl = '/workers', themeControl }: {
  session: ShellSession; children: ReactNode; navigationItems?: typeof items; activeUrl?: string; themeControl?: ReactNode;
}) {
  useLanguage();
  return <div className="poc-app">
    <a className="poc-skip" href="#poc-content">{t("shared.skipToContent")}</a>
    <SidebarNavigationSimple key={session.authenticated ? 'authenticated' : 'signed-out'} activeUrl={activeUrl} items={navigationItems.map(item => ({ ...item, label: localizeText(item.label) }))} showAccountCard={false} navigationLabels={{ expand: t('shared.expandNavigation'), close: t('shared.closeNavigation'), navigation: t('shared.mainNavigation') }}
      featureCard={<section aria-label={t('shared.serverAdministration')} className="flex min-w-0 flex-col gap-3 text-sm text-tertiary">
        <h2 className="text-[10px] font-bold uppercase tracking-[0.11em] text-tertiary">{t('shared.serverAdministration')}</h2>
        {session.authenticated && <ServerStatus />}
        <div className="flex min-w-0 items-center gap-1.5">
          <LanguageControl />
          {themeControl}
          {session.authenticated && <Button color="tertiary" size="sm" className="!h-[42px] !min-w-0 !shrink !gap-1 !px-1.5 !text-[11px]" iconLeading={<LogOut01 className="!size-4" />} onPress={session.onSignOut}>{t('shared.signOut')}</Button>}
        </div>
        <div className="border-t border-secondary pt-3">
          <p className="text-[10px] font-bold uppercase tracking-[0.08em] text-quaternary">Codex Server</p>
          <p className="mt-1 text-[10px] text-quaternary">{t('shared.controlPlane')}</p>
        </div>
      </section>} />
    <main id="poc-content" className="min-w-0 flex-1 px-4 py-8 md:px-8 lg:py-10" tabIndex={-1}>
      {session.authenticated ? children : <div className="mx-auto max-w-md py-8">
        <TableCard.Root><TableCard.Header title={t("shared.administrationSignIn")} description={t("shared.useTheServerManagementTokenTheExistingSessionIsRestoredOnReload")} />
          <form className="flex flex-col gap-5 p-6" onSubmit={event => {
            event.preventDefault(); if (session.pending) return; const form = event.currentTarget;
            const token = new FormData(form).get('management-token'); form.reset();
            if (typeof token === 'string') session.onSignIn?.(token);
          }}>
            <Input name="management-token" label={t("shared.serverManagementToken")} type="password" autoComplete="off" isRequired />
            <Button type="submit" color="primary" isDisabled={session.pending || !session.onSignIn}>{t("shared.signIn")}</Button>
            <Button color="secondary" onPress={session.onRestore} isDisabled={session.pending || !session.onRestore}>{t("shared.checkSession")}</Button>
            <Notice>{session.message ?? t("shared.checkingAdministrationSession")}</Notice>
            <AdvancedDisclosure title={t("shared.signInHelp")}><p>{t("shared.retrieveTheTokenPrivatelyOnTheServerBrowserLoginRequiresTheConfigured")}</p></AdvancedDisclosure>
            {session.onSignOut && <Button color="tertiary" isDisabled={session.pending} onPress={session.onSignOut}>{t("shared.retrySignOut")}</Button>}
          </form>
        </TableCard.Root>
      </div>}
    </main>
  </div>;
}
