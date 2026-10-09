import { t, useLanguage } from './i18n';
import { Sun, Moon01 } from '@untitledui/icons';
import { useEffect } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { usePreferences } from './preferences';
export function ThemeControl() {
  useLanguage();
  const theme = usePreferences(state => state.theme), setTheme = usePreferences(state => state.setTheme);
  useEffect(() => { document.documentElement.classList.toggle('dark-mode', theme === 'dark'); document.documentElement.style.colorScheme = theme; }, [theme]);
  const Icon = theme === 'dark' ? Sun : Moon01;
  return <Button color="secondary" size="sm" className="!h-[42px] !w-[42px] !shrink-0 !rounded-full !p-0" iconLeading={<Icon aria-hidden="true" className="!size-5 text-warning-primary" />} aria-label={t(theme === 'dark' ? 'shared.lightMode' : 'shared.darkMode')} onPress={() => setTheme(theme === 'dark' ? 'light' : 'dark')} />;
}
