import { create } from 'zustand';
import { useState } from 'react';
import { useRuntime } from './api/session';
import { accepted, archiveResult } from './api/validation';
import { t, type TranslationKey } from './i18n';

interface Toast { id: number; text: string; failed: boolean }
const useToasts = create<{ items: Toast[]; push(text: string, failed?: boolean): void; drop(id: number): void }>(set => ({
  items: [],
  push: (text, failed = false) => { const id = Date.now() + Math.random(); set(state => ({ items: [...state.items, { id, text, failed }] })); setTimeout(() => set(state => ({ items: state.items.filter(item => item.id !== id) })), 4000); },
  drop: id => set(state => ({ items: state.items.filter(item => item.id !== id) }))
}));
export const toast = (text: string, failed = false) => useToasts.getState().push(text, failed);

export function Toasts() {
  const items = useToasts(state => state.items);
  return <div className="pointer-events-none fixed inset-x-0 bottom-4 z-[60] flex flex-col items-center gap-2 px-4" aria-live="polite">
    {items.map(item => <div key={item.id} role={item.failed ? 'alert' : 'status'}
      className={`pointer-events-auto max-w-lg rounded-lg px-4 py-2.5 text-sm font-medium shadow-lg ${item.failed ? 'bg-utility-red-700 text-white' : 'bg-primary-solid text-white'}`}>{item.text}</div>)}
  </div>;
}

/** Runs one Server mutation, reports the result and refreshes affected reads. Never retries automatically. */
export function useAction() {
  const runtime = useRuntime();
  const [busy, setBusy] = useState<string>();
  async function run(resource: string, path: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', body: unknown, done: TranslationKey) {
    if (busy) return false;
    setBusy(resource);
    try { await runtime.mutate(resource, path, method, body, accepted); toast(t(done)); return true; }
    catch (error) {
      toast(error instanceof Error ? error.message : t('common.actionFailed'), true);
      // The outcome may be unknown. Re-read Server state before allowing the same action again.
      try { await runtime.reconcile(resource, async () => { await runtime.queries.invalidateQueries(); }); } catch { /* Still pending; keep the lock. */ }
      return false;
    }
    finally { setBusy(undefined); }
  }
  return { run, busy };
}

/** Archives or restores finished executions. Archive only hides them from default lists. */
export function useArchive() {
  const runtime = useRuntime();
  const [busy, setBusy] = useState(false);
  async function run(ids: string[], archived: boolean) {
    if (busy || !ids.length) return false;
    setBusy(true);
    try {
      const result = await runtime.mutate('executions-archive', '/api/v1/executions/archive', 'POST', { executionIds: ids, archived }, archiveResult);
      await runtime.queries.invalidateQueries();
      if (result.changed.length) toast(t(archived ? 'actions.archived' : 'actions.restored', { n: result.changed.length }));
      if (result.refused.length) toast(t('actions.archiveRefused', { n: result.refused.length }), true);
      return result.changed.length > 0;
    }
    catch (error) {
      toast(error instanceof Error ? error.message : t('common.actionFailed'), true);
      try { await runtime.reconcile('executions-archive', async () => { await runtime.queries.invalidateQueries(); }); } catch { /* Still pending; keep the lock. */ }
      return false;
    }
    finally { setBusy(false); }
  }
  return { run, busy };
}
