import { t, useLanguage } from './i18n';
import type { ComponentProps, ReactNode } from 'react';
import { Trash01, LinkExternal01 } from '@untitledui/icons';
import { Button } from '../untitled/components/base/buttons/button';
export function DeleteAction(props: Omit<ComponentProps<typeof Button>, 'iconLeading' | 'color'>) {
  useLanguage();
  return <Button {...props} color="primary-destructive" iconLeading={Trash01} />;
}
export function ExternalLink({ children, ...props }: { href?: string; children: ReactNode; className?: string }) {
  useLanguage();
  return <a {...props} target="_blank" rel="noopener noreferrer" className={`inline-flex max-w-full items-center gap-1 rounded focus-visible:outline-2 focus-visible:outline-offset-2 outline-brand ${props.className ?? ''}`}>
    {children}<LinkExternal01 aria-hidden="true" className="size-4 shrink-0" /><span className="sr-only">{' '}{t("shared.opensInNewWindow")}</span>
  </a>;
}
