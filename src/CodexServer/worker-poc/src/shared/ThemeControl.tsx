import { t, useLanguage } from './i18n';
import { Sun, Moon01 } from '@untitledui/icons';
import { useEffect } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { usePreferences } from './preferences';
export function ThemeControl() {
  useLanguage();
  const theme = usePreferences(state => state.theme), setTheme = usePreferences(state => state.setTheme);
  useEffect(() => { document.documentElement.classList.toggle('dark-mode', theme === 'dark'); document.documentElement.style.colorScheme = theme; }, [theme]);
  return <Button color="secondary" size="sm" iconLeading={theme === 'dark' ? Moon01 : Sun} onPress={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>{t(theme === 'dark' ? 'shared.lightMode' : 'shared.darkMode')}</Button>;
}
