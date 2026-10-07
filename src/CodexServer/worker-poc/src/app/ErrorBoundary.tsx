import { Component, type ReactNode } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
export class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (this.state.failed) return <main className="p-8" role="alert"><h1>Dashboard unavailable</h1><p>Reload to restore the session and authoritative state.</p><Button href="/home" color="secondary">Open current dashboard</Button></main>;
    return this.props.children;
  }
}
