import { useState } from 'react';
import { Button } from '../untitled/components/base/buttons/button';
import { toast } from '../shared/actions';
import { useRuntime } from '../shared/api/session';
import type { Validator } from '../shared/api/client';
import type { ExecutionSummary, NodeSummary, WorkerAdministration, WorkerDiagnostics } from '../shared/api/contracts';
import { record } from '../shared/api/validation';
import { t } from '../shared/i18n';
import { workerHealth } from '../shared/model';

interface Log { state: string; lines: string[] }
const log: Validator<Log> = value => {
  const item = record(value);
  if (typeof item.state !== 'string' || !Array.isArray(item.lines) || item.lines.some(line => typeof line !== 'string')) throw new Error('Invalid log.');
  return item as unknown as Log;
};
const pause = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

/** One JSON file with what a support Issue needs about a Worker: health, diagnostics, tools, recent executions,
 *  runtime events and journal. Everything comes from Server reads that are already secret-free. */
export function WorkerExport({ base, worker, diagnostics, node, executions, online }: {
  base: string; worker: WorkerAdministration; diagnostics?: WorkerDiagnostics; node?: NodeSummary; executions: ExecutionSummary[]; online: boolean;
}) {
  const runtime = useRuntime();
  const [busy, setBusy] = useState(false);
  async function readLog(source: 'events' | 'journal', signal: AbortSignal): Promise<Log> {
    if (!online) return { state: 'unavailable', lines: [] };
    try {
      let result = await runtime.read(`${base}/${source}?lines=100&refresh=true`, signal, log);
      // The Worker answers on its next heartbeat; wait a bounded time for it.
      for (let attempt = 0; result.state === 'pending' && attempt < 15; attempt++) {
        await pause(2000);
        result = await runtime.read(`${base}/${source}?lines=100`, signal, log);
      }
      return result;
    } catch (error) { return { state: error instanceof Error ? error.message : 'error', lines: [] }; }
  }
  async function download() {
    setBusy(true);
    try {
      const signal = new AbortController().signal;
      const [events, journal] = await Promise.all([readLog('events', signal), readLog('journal', signal)]);
      const health = workerHealth(worker);
      const report = {
        exportedAtUtc: new Date().toISOString(),
        worker, health: { state: health.state, cause: t(health.cause.key, health.cause.values) }, diagnostics, tools: node?.capabilities ?? [],
        executions: executions.slice(0, 50), events, journal
      };
      const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = `worker-${(worker.displayName || worker.workerId.slice(0, 8)).replace(/[^\w.-]+/g, '-')}-${report.exportedAtUtc.slice(0, 19).replace(/:/g, '')}.json`;
      link.click();
      URL.revokeObjectURL(url);
      toast(t('workers.exportDone'));
    } finally { setBusy(false); }
  }
  return <Button size="sm" color="secondary" isDisabled={busy} onClick={() => void download()}>{t(busy ? 'workers.exporting' : 'workers.export')}</Button>;
}
