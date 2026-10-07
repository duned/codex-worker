// Pre-paint boundary for the preference owner. Keep its version/allowlist in sync
// with shared/preferences.ts. This classic same-origin script runs before CSS.
(() => {
  let theme = 'dark';
  try {
    const preference = JSON.parse(localStorage.getItem('codex-dashboard-preferences'));
    if (preference?.version === 1 && preference.state?.theme === 'light') theme = 'light';
  } catch { /* Missing, corrupt or unavailable storage defaults to dark. */ }
  document.documentElement.classList.toggle('dark-mode', theme === 'dark');
  document.documentElement.style.colorScheme = theme;
})();
