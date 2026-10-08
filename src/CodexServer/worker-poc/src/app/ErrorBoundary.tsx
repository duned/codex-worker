import { t, useLanguage } from '../shared/i18n';
import { Component, type ReactNode } from 'react';
import { LanguageControl } from '../shared/LanguageControl';
import { Button } from '../untitled/components/base/buttons/button';
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (this.state.failed) return <Unavailable />;
    return this.props.children;
  }
}
function Unavailable() {
  useLanguage();
  return <main className="p-8" role="alert"><LanguageControl /><h1>{t("shared.dashboardUnavailable")}</h1><p>{t("shared.reloadToRestoreTheSessionAndAuthoritativeState")}</p><Button href="/home" color="secondary">{t("shared.openCurrentDashboard")}</Button></main>;
}
