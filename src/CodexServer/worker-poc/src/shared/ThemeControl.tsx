import { useEffect, useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
export function ThemeControl() {
  const [theme, setTheme] = useState<'dark' | 'light'>(() => {
    try { return localStorage.getItem('codex-dashboard-theme') === 'light' ? 'light' : 'dark'; }
    catch { return 'dark'; }
  });
  useEffect(() => {
    document.documentElement.classList.toggle('dark-mode', theme === 'dark');
    try { localStorage.setItem('codex-dashboard-theme', theme); } catch { /* Storage may be disabled; preference still works for this visit. */ }
  }, [theme]);
  return <Button color="secondary" size="sm" onPress={() => setTheme(theme === 'dark' ? 'light' : 'dark')}>Use {theme === 'dark' ? 'light' : 'dark'} mode</Button>;
}
