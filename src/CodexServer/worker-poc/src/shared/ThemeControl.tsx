import { useEffect } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { usePreferences } from './preferences';
export function ThemeControl() {
  const theme = usePreferences(state => state.theme), setTheme = usePreferences(state => state.setTheme);
  useEffect(() => { document.documentElement.classList.toggle('dark-mode', theme === 'dark'); document.documentElement.style.colorScheme = theme; }, [theme]);
  return <Button color="secondary" size="sm" onPress={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>Use {theme === 'dark' ? 'light' : 'dark'} mode</Button>;
}
