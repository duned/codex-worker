import { Component, type ReactNode } from 'react';
import { t } from '../shared/i18n';
/** Last-resort boundary: a rendering bug shows a reload hint instead of a blank page. */
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (!this.state.failed) return this.props.children;
    return <div role="alert" className="mx-auto max-w-md p-8 text-center text-primary">
      <h1 className="text-xl font-semibold">{t('common.crashTitle')}</h1><p className="mt-2 text-tertiary">{t('common.crashBody')}</p>
      <button className="mt-4 font-semibold underline" onClick={() => window.location.reload()}>{t('common.reload')}</button>
    </div>;
  }
}
