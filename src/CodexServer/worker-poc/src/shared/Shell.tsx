import { type ReactNode } from 'react';
import { HomeLine, Folder, Server01, Activity, Settings01 } from '@untitledui/icons';
import { SidebarNavigationSimple } from '../untitled/components/application/app-navigation/sidebar-navigation/sidebar-simple';
import { Button } from '../untitled/components/base/buttons/button';
import { Input } from '../untitled/components/base/input/input';
import { TableCard } from '../untitled/components/application/table/table';

const items = [
  { label: 'Home', href: '/', icon: HomeLine },
  { label: 'Projects', href: '/projects', icon: Folder },
  { label: 'Workers', href: '/workers', icon: Server01 },
  { label: 'Executions', href: '/executions', icon: Activity },
  { label: 'Settings', href: '/settings', icon: Settings01 }
];
interface ShellSession {
  authenticated: boolean; pending?: boolean; message?: string;
  onSignOut?: () => void; onSignIn?: (token: string) => void; onRestore?: () => void;
}
export function Application({ session, children, navigationItems = items, activeUrl = '/workers', themeControl }: {
  session: ShellSession; children: ReactNode; navigationItems?: typeof items; activeUrl?: string; themeControl?: ReactNode;
}) {
  return <div className="poc-app">
    <a className="poc-skip" href="#poc-content">Skip to content</a>
    <SidebarNavigationSimple key={session.authenticated ? 'authenticated' : 'signed-out'} activeUrl={activeUrl} items={navigationItems} showAccountCard={false}
      featureCard={<div className="flex flex-col gap-3 text-sm text-tertiary"><p>Server administration</p>{themeControl}{session.authenticated && <Button color="secondary" size="sm" onPress={session.onSignOut}>Sign out</Button>}</div>} />
    <main id="poc-content" className="min-w-0 flex-1 px-4 py-8 md:px-8 lg:py-10" tabIndex={-1}>
      {session.authenticated ? children : <div className="mx-auto max-w-md py-8">
        <TableCard.Root><TableCard.Header title="Administration sign in" description="Use the Server management token. The existing session is restored on reload." />
          <form className="flex flex-col gap-5 p-6" onSubmit={event => {
            event.preventDefault(); const form = event.currentTarget;
            const token = new FormData(form).get('management-token'); form.reset();
            if (typeof token === 'string') session.onSignIn?.(token);
          }}>
            <Input name="management-token" label="Server management token" type="password" autoComplete="off" isRequired />
            <Button type="submit" color="primary" isDisabled={session.pending || !session.onSignIn}>Sign in</Button>
            <Button color="secondary" onPress={session.onRestore} isDisabled={session.pending || !session.onRestore}>Check session</Button>
            <p role="status" className="text-sm text-tertiary">{session.message ?? 'Checking administration session…'}</p>
            <p className="text-sm text-tertiary">Retrieve the token privately on the Server. Browser login requires the configured HTTPS administration origin and management network access.</p>
            {session.onSignOut && <Button color="tertiary" isDisabled={session.pending} onPress={session.onSignOut}>Retry sign out</Button>}
          </form>
        </TableCard.Root>
      </div>}
    </main>
  </div>;
}
