import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { restartStatusPaths } from '../src/features/workers/worker-restart-model.mjs';

const workerPoc = new URL('../src/features/workers/WorkerPocPage.tsx', import.meta.url);
const detailPage = new URL('../src/features/workers/WorkerDetailPage.tsx', import.meta.url);
const restartPage = new URL('../src/features/workers/WorkerRestart.tsx', import.meta.url);
const detailView = new URL('../src/detail.jsx', import.meta.url);
const styles = new URL('../src/poc.css', import.meta.url);
const read = path => readFile(path, 'utf8');

test('worker status reload uses authenticated reads and does not invoke a mutation or restart check', async () => {
  const source = await read(detailPage);
  const reloadStart = source.indexOf('async function reloadWorkerStatus()');
  const reloadEnd = source.indexOf('function refreshAfterRestartCheck()', reloadStart);
  const reload = source.slice(reloadStart, reloadEnd);
  assert.match(reload, /refreshVisibleStatus\(\)/);
  assert.match(reload, /administration\?\.onRefreshStatus\(\)/);
  assert.doesNotMatch(reload, /runtime\.mutate|\/update|checkStatus|restartStatusPaths/i);
  assert.match(source, /observations\.refetch\(\{ throwOnError: true \}\)/);
  assert.match(source, /nodes\.refetch\(\{ throwOnError: true \}\)/);
  assert.match(source, /diagnostics\.refetch\(\{ throwOnError: true \}\)/);
  assert.match(source, /setJournalOpenRequest\(value => value \+ 1\)/);
});

test('restart status checks read existing operation and service status without replaying restart', async () => {
  const paths = restartStatusPaths('worker id/with spaces');
  assert.deepEqual(paths, {
    operation: '/api/v1/workers/worker%20id%2Fwith%20spaces/update',
    worker: '/api/v1/workers/worker%20id%2Fwith%20spaces'
  });

  const source = await read(restartPage);
  const requestStart = source.indexOf('async function request()');
  const checkStart = source.indexOf('async function checkStatus()', requestStart);
  const request = source.slice(requestStart, checkStart);
  const checkEnd = source.indexOf('const current = operation.data;', checkStart);
  const check = source.slice(checkStart, checkEnd);
  assert.equal((request.match(/mutation\.mutateAsync\(/g) ?? []).length, 1);
  assert.match(request, /operationId: id\.current, restart: true/);
  assert.doesNotMatch(request, /runtime\.read|refetch\(/);
  assert.match(source, /const actionDisabled = mutation\.isPending \|\| mutation\.locked \|\| operation\.fetching \|\| \(operation\.hasError && current === undefined\) \|\| !restartAvailable\(worker, current\)/);
  assert.match(source, /const statusCheckAvailable = Boolean\(current\?\.restart \|\| operation\.hasError \|\| mutation\.isError \|\| mutation\.locked \|\| requestMessage\)/);
  assert.match(check, /mutation\.reconcile\(async signal/);
  assert.match(check, /runtime\.read\(paths\.operation, signal, workerUpdate\)/);
  assert.match(check, /runtime\.read\(paths\.worker, signal, validateWorker\)/);
  assert.match(check, /onStatusChecked\?\.\(\)/);
  assert.doesNotMatch(check, /mutateAsync|runtime\.mutate|method:\s*['"]POST/);
  assert.match(await read(new URL('../src/features/workers/WorkerJournal.tsx', import.meta.url)), /\/journal`/);
});

test('scheduling mutations retain confirmation and Server enforcement without duplicate client preflights', async () => {
  const source = await read(workerPoc);
  const mutationStart = source.indexOf('const operation = useMutation');
  const mutationEnd = source.indexOf('const submit =', mutationStart);
  const mutation = source.slice(mutationStart, mutationEnd);
  const schedulingStart = mutation.indexOf("if (key === 'Enabled' || key === 'Draining' || key === 'Disabled')");
  const schedulingEnd = mutation.indexOf('const check =', schedulingStart);
  const scheduling = mutation.slice(schedulingStart, schedulingEnd);
  assert.match(scheduling, /runtime\.mutate\(resource, `\$\{base\}\/scheduling-policy`, 'PUT'/);
  assert.doesNotMatch(scheduling, /runtime\.read\(/);
  assert.match(mutation, /const check = async \(signal: AbortSignal\)/);
  assert.match(mutation, /runtime\.mutate\(resource, `\$\{base\}\/authentication\/revoke`, 'POST'/);
  assert.match(source, /<ConfirmationDialog[^]*?isOpen=\{!!selected\}/);
  assert.match(source, /onSubmit=\{submit\}/);
  assert.doesNotMatch(await read(restartPage), /ConfirmationDialog/);

  const server = await read(new URL('../../WorkerUpdateApi.cs', import.meta.url));
  assert.match(server, /MapPost\("\/api\/v1\/workers\/\{workerId\}\/update"[\s\S]*?AuthorizedManagement\(context, settings\)/);
  const serverApplication = await read(new URL('../../ServerApplication.cs', import.meta.url));
  assert.match(serverApplication, /MapPut\("\/api\/v1\/workers\/\{workerId\}\/scheduling-policy"[\s\S]*?AuthorizedManagement\(context, settings\)/);
  assert.match(serverApplication, /if \(!diagnostics\.CanActivate\)/);
  const runtime = await read(new URL('../src/shared/api/runtime.ts', import.meta.url));
  assert.match(runtime, /csrf: this\.csrf/);
});

test('Worker controls and lower information follow the approved responsive ordering', async () => {
  const view = await read(detailView);
  const trigger = view.indexOf('<MenuTrigger isOpen={isOpen} onOpenChange={onOpenChange}>');
  const schedule = view.indexOf('{administration && <SchedulingControls administration={administration} />}', trigger);
  const restart = view.indexOf('{restart}', schedule);
  const refresh = view.indexOf('<WorkerStatusRefresh', restart);
  const additional = view.indexOf('{visibleActions.length > 0 && <MenuSection', refresh);
  assert.ok(trigger >= 0 && schedule > trigger && restart > schedule && refresh > restart && additional > refresh);
  const technical = view.indexOf('<div className="poc-worker-technical-column">', refresh);
  const readiness = view.indexOf('<ReadinessPanel', technical);
  assert.ok(technical >= 0 && readiness > technical);

  const css = await read(styles);
  assert.match(css, /\.poc-worker-layout\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /\.poc-service-grid\s*\{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(css, /@media \(max-width: 1023px\)[\s\S]*?\.poc-worker-lower-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /@media \(max-width: 460px\)[\s\S]*?\.poc-service-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
});
