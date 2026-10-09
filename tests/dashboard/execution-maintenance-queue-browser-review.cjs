// Deterministic browser review for the Server-managed execution maintenance queue.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright');

const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const workerId = '22222222222222222222222222222222';
const pendingWorkerId = '88888888888888888888888888888888';
const workerExecutionId = '44444444444444444444444444444444';
const assignmentId = '33333333333333333333333333333333';
const createdAtUtc = '2026-10-01T00:00:00Z';
const makeExecution = (id, state, extras = {}) => ({
  id, projectId: 'project-a', state, createdAtUtc, assignedAtUtc: createdAtUtc,
  startedAtUtc: createdAtUtc, assignedWorkerId: workerId, assignmentId, workerExecutionId,
  workReference: { type: 'github-issue', id: '27' }, ...extras
});
const noLease = makeExecution('11111111111111111111111111111111', 'Running');
const reconcile = makeExecution('12121212121212121212121212121212', 'Failed', {
  recoveryState: 'LeaseExpiredUncertain', lease: { executionId: '12121212121212121212121212121212', workerId, generation: 2,
    acquiredAtUtc: createdAtUtc, expiresAtUtc: '2026-10-02T00:00:00Z', state: 'Expired' }
});
const stale = makeExecution('13131313131313131313131313131313', 'Failed', {
  recoverable: true, recoveryState: 'recoverable', lease: { executionId: '13131313131313131313131313131313', workerId, generation: 3,
    acquiredAtUtc: createdAtUtc, expiresAtUtc: '2026-10-12T00:00:00Z', state: 'Released' }
});
const offline = makeExecution('14141414141414141414141414141414', 'Running', {
  lease: { executionId: '14141414141414141414141414141414', workerId: '55555555555555555555555555555555', generation: 1,
    acquiredAtUtc: createdAtUtc, expiresAtUtc: '2026-10-12T00:00:00Z', state: 'Active' },
  assignedWorkerId: '55555555555555555555555555555555'
});
const missingObservation = makeExecution('15151515151515151515151515151515', 'Running', {
  startedAtUtc: '2026-10-09T11:00:00Z', assignedAtUtc: '2026-10-09T11:00:00Z',
  lease: { executionId: '15151515151515151515151515151515', workerId, generation: 4,
    acquiredAtUtc: '2026-10-09T11:00:00Z', expiresAtUtc: '2026-10-09T13:00:00Z', state: 'Active' }
});
const worker = (id, availability = 'online') => ({ workerId: id, displayName: id === workerId ? 'Build Worker A' : 'Offline Worker',
  availability, schedulingPolicy: 'Active', activeAssignments: availability === 'offline' ? 0 : 1, lastHeartbeatAtUtc: createdAtUtc,
  capabilities: [{ type: 'protocol', name: 'execution-maintenance-v1', version: '1' }] });
const workerObservation = (executionId, serverExecutionId, values = {}) => ({ executionId, serverExecutionId,
  assignmentId: serverExecutionId ? assignmentId : null, generation: serverExecutionId ? 3 : null, state: 'Failed',
  recoveryState: 'recoverable', reportingStatus: serverExecutionId ? 'pending' : 'none', project: 'Example project', issueNumber: 27,
  archived: false, ...values });
const inventoryRequest = { operationId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', workerId, action: 'inventory', apply: false,
  timeoutSeconds: 60, limit: 50, offset: 0 };
function inventoryCommand(request = inventoryRequest, observations = [
  workerObservation(workerExecutionId, stale.id),
  workerObservation('66666666666666666666666666666666', null, { issueNumber: 28, reportingStatus: 'none' })
]) {
  return { request, status: 'succeeded', createdAtUtc: '2026-10-09T12:00:00Z', completedAtUtc: '2026-10-09T12:00:01Z',
    authorizedBy: 'server-management:fixture', report: { outcome: 'succeeded', reason: 'inventory-observed', observations } };
}
const pendingCommand = { request: { operationId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', workerId: pendingWorkerId,
  serverExecutionId: noLease.id, workerExecutionId, assignmentId, generation: 1, action: 'inspect', apply: false, timeoutSeconds: 60, limit: 1, offset: 0 },
status: 'pending', createdAtUtc: '2026-10-09T11:00:00Z', authorizedBy: 'server-management:fixture' };

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    await page.clock.setFixedTime('2026-10-09T12:00:00Z');
    let mode = 'normal', executions = [noLease, reconcile, stale, offline, missingObservation], operations = [pendingCommand, inventoryCommand()], writes = [];
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), endpoint = url.pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (/^\/(home|projects|workers|executions|settings)(\/|$)/.test(endpoint)) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture', expiresAtUtc: '2026-10-09T13:00:00Z' } });
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (endpoint === '/api/status') return route.fulfill({ json: { state: 'running', version: '0.15.0', startedAtUtc: createdAtUtc } });
      if (endpoint === '/api/v1/projects') return route.fulfill({ json: [{ id: 'project-a', name: 'Example project', repository: 'example/repository' }] });
      if (endpoint === '/api/v1/workers') return route.fulfill({ json: [worker(workerId), worker('55555555555555555555555555555555', 'offline'), worker(pendingWorkerId)] });
      if (endpoint === `/api/v1/workers/${workerId}`) return route.fulfill({ json: worker(workerId) });
      if (endpoint === '/api/v1/nodes') return route.fulfill({ json: [
        { id: workerId, kind: 'worker', connectivity: 'connected', executionReadiness: 'ready', observationsStale: false, capabilities: [] },
        { id: '55555555555555555555555555555555', kind: 'worker', connectivity: 'offline', executionReadiness: 'unknown', observationsStale: false, capabilities: [] },
        { id: pendingWorkerId, kind: 'worker', connectivity: 'connected', executionReadiness: 'ready', observationsStale: false, capabilities: [] }
      ] });
      if (endpoint === '/api/v1/executions' && request.method() === 'GET') return route.fulfill({ json: mode === 'empty' ? [] : url.searchParams.get('offset') === '50' ? [] : executions });
      if (endpoint === `/api/v1/executions/${stale.id}`) return route.fulfill({ json: stale });
      if (endpoint === `/api/v1/executions/${reconcile.id}`) return route.fulfill({ json: reconcile });
      if (endpoint === '/api/v1/maintenance/executions' && request.method() === 'GET') {
        if (mode === 'unsupported') return route.fulfill({ status: 404 });
        if (mode === 'forbidden') return route.fulfill({ status: 403 });
        const workerFilter = url.searchParams.get('workerId');
        return route.fulfill({ json: mode === 'empty' ? [] : operations.filter(item => !workerFilter || item.request.workerId === workerFilter) });
      }
      if (endpoint === '/api/v1/maintenance/executions' && request.method() === 'POST') {
        const body = request.postDataJSON(); writes.push(body);
        const command = inventoryCommand(body, [workerObservation('77777777777777777777777777777777', null, { issueNumber: 29, reportingStatus: 'none' })]);
        operations = [command, ...operations];
        return route.fulfill({ status: 201, json: command });
      }
      if (/^\/api\/v1\/maintenance\/executions\/[a-f0-9]{32}\/cancel$/.test(endpoint) && request.method() === 'POST') {
        const operationId = endpoint.split('/').at(-2), index = operations.findIndex(item => item.request.operationId === operationId);
        if (index < 0 || operations[index].status !== 'pending') return route.fulfill({ status: 409 });
        operations[index] = { ...operations[index], status: 'cancelled', completedAtUtc: '2026-10-09T12:00:02Z',
          report: { outcome: 'refused', reason: 'operator-cancelled-before-dispatch', observations: [] } };
        return route.fulfill({ json: operations[index] });
      }
      if (/^\/api\/v1\/maintenance\/executions\/[a-f0-9]{32}$/.test(endpoint)) {
        const command = operations.find(item => item.request.operationId === endpoint.split('/').at(-1));
        if (!command) return route.fulfill({ status: 404 });
        const observations = command.report?.observations.map(observation => ({ observation,
          execution: observation.serverExecutionId === stale.id ? stale : null,
          status: observation.serverExecutionId ? 'reporting-pending' : 'orphan-worker-record' })) ?? [];
        return route.fulfill({ json: { operation: command, execution: null, workerStatus: 'online', observations, status: command.status } });
      }
      return route.fulfill({ json: [] });
    });

    await page.goto('https://dashboard.test/executions?project=project-a');
    await page.getByRole('link', { name: 'Execution Maintenance' }).click();
    await page.getByRole('heading', { name: 'Execution Maintenance' }).waitFor();
    assert.equal(new URL(page.url()).searchParams.get('view'), 'maintenance', 'Maintenance opens from Executions navigation.');
    await page.getByText('No Server lease', { exact: true }).first().waitFor();
    await page.getByText('Needs reconciliation', { exact: true }).waitFor();
    await page.getByText('Worker report may be stale', { exact: true }).waitFor();
    await page.getByText('Worker offline', { exact: true }).waitFor();
    await page.getByText('Worker-only record (no Server execution identity)', { exact: true }).waitFor();
    await page.getByText('Worker observation missing', { exact: true }).waitFor();
    assert.equal(await page.locator('li').filter({ hasText: missingObservation.id }).getByText('Worker observation missing', { exact: true }).count(), 1,
      'A successful Worker inventory makes a missing observation visible for an otherwise current Server execution.');
    assert.ok(await page.getByText(/Lease|No Server lease/).count(), 'Lease state is visible with ownership/report state.');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'The queue fits its desktop viewport.');

    const inspectLinks = page.getByRole('link', { name: 'Inspect and preview this execution' });
    await inspectLinks.nth(2).click();
    await page.getByRole('heading', { name: 'Managed execution maintenance' }).waitFor();
    assert.equal(new URL(page.url()).pathname, `/executions/${stale.id}`, 'The selected Server execution is preserved.');
    await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Executions', exact: true }).click();
    await page.getByRole('heading', { name: 'Execution Maintenance' }).waitFor();

    await page.getByRole('button', { name: 'Refresh Worker inventory' }).first().click();
    await page.getByText('Worker inventory requested through the Server. Review its retained operation result before another request.').waitFor();
    assert.equal(writes.length, 1);
    assert.equal(writes[0].workerId, workerId);
    assert.equal(writes[0].action, 'inventory');
    assert.equal(writes[0].apply, false);
    await page.getByRole('heading', { name: 'Server maintenance operation' }).waitFor();
    await page.getByText('Worker-only record (no Server execution identity)').waitFor();
    await page.getByRole('button', { name: 'Refresh operation status' }).click();
    await page.getByText('Authoritative operation and execution status refreshed.').waitFor();
    assert.ok(await page.getByText(/77777777777777777777777777777777/).count(), 'A refreshed report is displayed.');
    await page.locator('li').filter({ hasText: pendingCommand.request.operationId }).getByRole('button', { name: 'Open status' }).click();
    await page.getByRole('button', { name: 'Cancel undispatched operation' }).click();
    await page.getByText('The undispatched operation was cancelled and retained in the Server audit.').waitFor();
    assert.equal(operations.find(item => item.request.operationId === pendingCommand.request.operationId).status, 'cancelled', 'Only the pending Server operation was cancelled.');

    await page.getByLabel('Language').selectOption('es');
    await page.getByRole('heading', { name: 'Mantenimiento de ejecuciones' }).waitFor();
    await page.setViewportSize({ width: 375, height: 850 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'The queue reflows at mobile width.');

    mode = 'empty'; operations = []; executions = [];
    await page.goto('https://dashboard.test/executions?view=maintenance'); await page.reload();
    await page.getByText('Ninguna ejecución de esta página requiere atención. Pruebe otra página o cambie los filtros.', { exact: true }).waitFor();

    mode = 'unsupported';
    await page.goto('https://dashboard.test/executions?view=maintenance'); await page.reload();
    await page.getByText(/API de mantenimiento de ejecuciones no está disponible en la versión 0.15.0/).waitFor();

    mode = 'forbidden';
    await page.goto('https://dashboard.test/executions?view=maintenance'); await page.reload();
    await page.getByRole('heading', { name: 'Inicio de sesión de administración' }).waitFor();
    assert.deepEqual(errors, []);
    console.log('Execution Maintenance queue browser review passed: deployed Executions entry point, bounded Server/Worker inventory, orphan/stale/offline/no-lease/reconciliation states, safe inventory refresh, operation refresh/audit, incompatibility, permission denial, EN/ES and mobile reflow.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
