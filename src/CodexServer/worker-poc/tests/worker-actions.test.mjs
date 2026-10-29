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
  assert.match(source, /<Button color="primary" aria-describedby="poc-restart-state" isDisabled=\{actionDisabled\}/);
  assert.match(source, /statusCheckAvailable && <div/);
  assert.match(source, /onPress=\{\(\) => \{ void checkStatus\(\); \}\}/);
  assert.match(check, /mutation\.reconcile\(async signal/);
  assert.match(check, /runtime\.read\(paths\.operation, signal, workerUpdate\)/);
  assert.match(check, /runtime\.read\(paths\.worker, signal, validateWorker\)/);
  assert.match(check, /onStatusChecked\?\.\(\)/);
  assert.doesNotMatch(check, /mutateAsync|runtime\.mutate|method:\s*['"]POST/);
  const journal = await read(new URL('../src/features/workers/WorkerJournal.tsx', import.meta.url));
  assert.match(journal, /journalPath\(workerId, true\)/);
  assert.match(journal, /workers\.journalRefresh/);
  assert.match(journal, /aria-expanded=\{open\}/);
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

test('Worker controls use the shared side panel and lower information keeps its responsive grouping', async () => {
  const view = await read(detailView);
  const trigger = view.indexOf('aria-haspopup="dialog" aria-expanded={isOpen}');
  const panel = view.indexOf('<FormDialog isOpen={isOpen}', trigger);
  const schedule = view.indexOf('{administration && <SchedulingControls administration={administration} />}', panel);
  const restart = view.indexOf('{restart}', schedule);
  const refresh = view.indexOf('<WorkerStatusRefresh', restart);
  const additional = view.indexOf('{visibleActions.length > 0 && <section', refresh);
  assert.ok(trigger >= 0 && panel > trigger && schedule > panel && restart > schedule && refresh > restart && additional > refresh);
  assert.match(view, /variant="side-panel"/);
  assert.match(view, /workers\.currentExecutionsContinue/);
  assert.match(view, /onPress=\{\(\) => onOpenChange\(true\)\}/);
  assert.match(view, /onClose=\{\(\) => onOpenChange\(false\)\}/);

  const technical = view.indexOf('<div className="poc-worker-technical-column">', refresh);
  const capabilities = view.indexOf('<CapabilitiesPanel', technical);
  const information = view.indexOf('<WorkerInformationPanel', capabilities);
  assert.ok(technical >= 0 && capabilities > technical && information > capabilities);
  assert.doesNotMatch(view, /ReadinessPanel|shared\.capabilityDetails|shared\.operationHistory/);

  const css = await read(styles);
  assert.match(css, /\.poc-worker-layout\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /\.poc-service-grid\s*\{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/);
  assert.match(css, /@media \(max-width: 1023px\)[\s\S]*?\.poc-worker-lower-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(css, /@media \(max-width: 460px\)[\s\S]*?\.poc-service-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\)/);
});

test('More actions uses the same focus-managed side panel and responsive dimensions as Edit worker', async () => {
  const view = await read(detailView);
  const dialog = await read(new URL('../src/shared/Dialogs.tsx', import.meta.url));
  const editor = await read(new URL('../src/features/workers/WorkerEditor.tsx', import.meta.url));
  assert.match(view, /aria-haspopup="dialog" aria-expanded=\{isOpen\}/);
  assert.match(view, /<FormDialog isOpen=\{isOpen\}[\s\S]*?variant="side-panel"/);
  assert.match(editor, /pending=\{busy\} variant="side-panel"/);
  assert.match(dialog, /isDismissable=\{!pending\} isKeyboardDismissDisabled=\{pending\}/);
  assert.match(dialog, /fixed inset-0 z-50 flex justify-end bg-overlay\/70/);
  assert.match(dialog, /lg:w-\[60vw\] lg:max-w-5xl/);
  assert.match(dialog, /React Aria owns focus containment\/restoration, Escape and outside dismissal/);
  assert.match(view, /aria-label=\{t\('shared\.closeDialog'\)\}/);
});

/* Keep the approved page content ordering visible as a focused contract check. */
test('Worker detail content continues from operational cards into executions and lower information', async () => {
  const view = await read(detailView);
  assert.match(view, /<WorkerOperationalStatus worker=\{worker\} node=\{node\} diagnostics=\{diagnostics\} \/>[\s\S]*?<CurrentExecutions/);
  assert.match(view, /<RecentExecutions[\s\S]*?<div className="poc-worker-lower-grid">/);
});
