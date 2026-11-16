// Production dashboard review with deterministic Server and Worker protocol fixtures.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const serverExecutionId = '11111111111111111111111111111111';
const workerId = '22222222222222222222222222222222';
const assignmentId = '33333333333333333333333333333333';
const workerExecutionId = '44444444444444444444444444444444';
const createdAtUtc = '2026-07-01T00:00:00Z';
const item = {
  id: serverExecutionId, projectId: 'project', state: 'Failed', createdAtUtc,
  assignedAtUtc: createdAtUtc, startedAtUtc: createdAtUtc, completedAtUtc: '2026-08-01T00:00:00Z',
  assignedWorkerId: workerId, assignmentId, workerExecutionId, recoverable: true,
  recoveryState: 'recoverable', recoveryReason: 'Retained recovery resources',
  workReference: { type: 'github-issue', id: '7' },
  lease: { executionId: serverExecutionId, workerId, generation: 3, acquiredAtUtc: createdAtUtc,
    expiresAtUtc: '2026-08-02T00:00:00Z', state: 'Released' }
};
const observation = (request, override = {}) => ({ executionId: workerExecutionId,
  serverExecutionId: request.serverExecutionId ?? serverExecutionId, assignmentId: request.assignmentId ?? assignmentId,
  generation: request.generation ?? 3, state: 'Failed', recoveryState: 'recoverable', reportingStatus: 'none',
  project: 'Example project', issueNumber: 7, archived: false, ...override });
const baseWorker = () => ({ workerId, displayName: 'Build machine', availability: 'online', schedulingPolicy: 'Active', activeAssignments: 0,
  lastHeartbeatAtUtc: '2026-10-09T12:00:00Z', capabilities: [{ type: 'protocol', name: 'execution-maintenance-v1' }] });

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    await page.clock.setFixedTime('2026-10-09T12:00:00Z');
    let current = structuredClone(item), worker = baseWorker(), nodeStale = false, mode = 'normal', localRecovery = 'recoverable';
    let operation, writes = [], holdApply, releaseApply;
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), endpoint = url.pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (/^\/(home|projects|workers|executions|settings)(?:\/|$)/.test(endpoint)) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture', expiresAtUtc: '2026-10-09T13:00:00Z' } });
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (endpoint === '/api/v1/maintenance/executions' && request.method() === 'POST') {
        const body = request.postDataJSON(); writes.push(body);
        if (mode === 'unauthorized') return route.fulfill({ status: 401, headers: { 'X-Codex-Administration-Error': 'administration-session-invalid' } });
        if (body.apply && holdApply) await holdApply;
        let report;
        if (mode === 'partial-failure') report = { outcome: 'failed', reason: 'inspection-unavailable', observations: [observation(body)] };
        else if (body.action === 'inventory') report = { outcome: 'succeeded', reason: 'inventory-observed', observations: [
          observation(body, { serverExecutionId: null, assignmentId: null, generation: null, recoveryState: 'recoverable' }),
          observation(body, { executionId: '55555555555555555555555555555555', assignmentId: '66666666666666666666666666666666', generation: 2,
            state: 'Failed', recoveryState: 'uncertain', reportingStatus: 'pending', issueNumber: 8 })
        ] };
        else if (body.action === 'cleanup' && !body.apply) report = { outcome: 'succeeded', reason: 'integrated', observations: [observation(body)] };
        else if (body.action === 'cleanup') { localRecovery = 'operator-cleaned'; report = { outcome: 'succeeded', reason: 'integrated', observations: [observation(body, { recoveryState: localRecovery })] }; }
        else if (body.action === 'archive' && localRecovery !== 'operator-cleaned') report = { outcome: 'refused', reason: 'archive-retention-or-review', observations: [observation(body)] };
        else if (body.action === 'archive') report = { outcome: 'succeeded', reason: 'already-clean', observations: [observation(body, { archived: body.apply })] };
        else if (body.action === 'retry-report') report = { outcome: 'succeeded', reason: body.apply ? 'completion-report-acknowledged' : 'completion-report-preview', observations: [observation(body)] };
        else report = { outcome: 'succeeded', reason: 'already-clean', observations: [observation(body)] };
        operation = { request: body, status: report.outcome === 'failed' ? 'failed' : 'succeeded', createdAtUtc: '2026-10-09T12:00:00Z',
          completedAtUtc: '2026-10-09T12:00:01Z', authorizedBy: 'server-management:fixture', report };
        return route.fulfill({ status: 201, json: operation });
      }
      if (endpoint === '/api/v1/maintenance/executions' && request.method() === 'GET') return route.fulfill({ json: operation ? [operation] : [] });
      if (/^\/api\/v1\/maintenance\/executions\/[a-f0-9]{32}\/cancel$/.test(endpoint) && request.method() === 'POST') {
        operation = { ...operation, status: 'cancelled', report: { outcome: 'refused', reason: 'operator-cancelled-before-dispatch', observations: [] } };
        return route.fulfill({ json: operation });
      }
      if (/^\/api\/v1\/maintenance\/executions\/[a-f0-9]{32}$/.test(endpoint)) return operation
        ? route.fulfill({ json: { operation, execution: structuredClone(current), workerStatus: worker.availability, status: operation.status,
          observations: operation.report?.observations.map(value => ({ observation: value, execution: structuredClone(current),
            status: !value.serverExecutionId ? 'orphan-worker-record' : value.assignmentId !== current.assignmentId || value.generation !== current.lease.generation ? 'stale-ownership' : 'potentially-recoverable' })) ?? [] } })
        : route.fulfill({ status: 404 });
      if (endpoint === '/api/v1/projects') return route.fulfill({ json: [{ id: 'project', name: 'Example project', repository: 'example/repository' }] });
      if (endpoint === '/api/v1/workers') return route.fulfill({ json: [worker] });
      if (endpoint === `/api/v1/workers/${workerId}`) return route.fulfill({ json: worker });
      if (endpoint === '/api/v1/nodes') return route.fulfill({ json: [{ id: workerId, kind: 'worker', connectivity: 'connected', executionReadiness: 'ready', observationsStale: nodeStale, capabilities: [] }] });
      if (endpoint === '/api/v1/executions') return route.fulfill({ json: url.searchParams.get('offset') === '50' ? [] : [current] });
      if (endpoint === `/api/v1/executions/${serverExecutionId}`) return route.fulfill({ json: current });
      return route.fulfill({ json: [] });
    });

    await page.goto('https://dashboard.test/executions?attention=needed&project=project&offset=0');
    await page.getByRole('heading', { name: 'Executions' }).waitFor();
    await page.getByText('Maintenance review', { exact: true }).waitFor();
    await page.getByText('#7', { exact: true }).waitFor();
    assert.ok(await page.getByText('Last recorded activity', { exact: true }).count());
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.getByRole('button', { name: 'Next page' }).click();
    await page.getByText('No matching execution requests.', { exact: true }).waitFor();
    assert.equal(new URL(page.url()).searchParams.get('offset'), '50');
    assert.equal(new URL(page.url()).searchParams.get('attention'), 'needed');
    assert.equal(new URL(page.url()).searchParams.get('project'), 'project');
    await page.getByRole('button', { name: 'Previous page' }).click();
    await page.getByRole('link', { name: 'Review exact execution' }).click();
    await page.getByRole('heading', { name: 'Managed execution maintenance' }).waitFor();
    await page.getByRole('button', { name: 'Inspect Worker record' }).waitFor();
    await page.getByRole('button', { name: 'Preview archive' }).click();
    await page.getByText('Archive retention or review requirements are not met.').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Archive execution' }).count(), 0, 'archive apply remains unavailable after a refused preview');
    assert.match(await page.getByText(/Permanent purge is unavailable/).textContent(), /retains execution/);

    await page.getByRole('button', { name: 'Inspect Worker record' }).click();
    await page.getByText('Read-only inspection submitted to the Worker through the Server.').waitFor();
    assert.equal(writes.at(-1).action, 'inspect');
    assert.equal(writes.at(-1).apply, false);
    await page.getByRole('button', { name: 'Preview resource cleanup' }).click();
    await page.getByText('Worker inspection confirms retained commits are integrated and resources can be reviewed for cleanup.').waitFor();
    await page.getByRole('button', { name: 'Clean up resources' }).waitFor();

    worker = { ...worker, schedulingPolicy: 'Draining' };
    holdApply = new Promise(resolve => { releaseApply = resolve; });
    await page.getByRole('button', { name: 'Clean up resources' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByText(/repeat ownership and Git checks under its repository gate/).waitFor();
    await dialog.getByRole('button', { name: 'Clean up resources' }).click();
    await dialog.getByText('Submitting…').waitFor();
    await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('dialog').count(), 1, 'pending confirmation remains open');
    assert.equal(await dialog.getByRole('button', { name: 'Clean up resources' }).isDisabled(), true);
    releaseApply(); holdApply = undefined;
    await page.getByText('Apply submitted to the Worker through the Server. Review its result before another operation.').waitFor();
    await page.getByText('After: execution Failed · recovery Operator cleaned').waitFor();
    assert.equal(writes.filter(value => value.action === 'cleanup' && value.apply).length, 1, 'apply is submitted once');

    await page.getByRole('button', { name: 'Preview archive' }).click();
    await page.getByText('Worker inspection confirms no managed workspace or feature branch remains.').waitFor();
    await page.getByRole('button', { name: 'Archive execution' }).click();
    await page.getByRole('dialog').getByText(/recheck retention and clean-resource requirements/).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Archive execution' }).click();
    await page.getByText('Apply submitted to the Worker through the Server. Review its result before another operation.').waitFor();
    assert.equal(writes.filter(value => value.action === 'archive' && value.apply).length, 1, 'archive apply follows a clean preview and confirmation');

    await page.getByLabel('Language').selectOption('es');
    await page.getByRole('heading', { name: 'Mantenimiento de ejecuciones administradas' }).waitFor();
    await page.setViewportSize({ width: 375, height: 850 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    assert.ok(await page.getByRole('button', { name: 'Actualizar estado de operación' }).count());

    nodeStale = true;
    await page.reload();
    await page.getByText(/Desactualizado/).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Inspeccionar registro del agente' }).count(), 0);
    nodeStale = false; worker = { ...worker, availability: 'offline', schedulingPolicy: 'Active' };
    await page.reload();
    await page.getByText('Agente desconectado', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Inspeccionar inventario del agente' }).count(), 0);

    worker = baseWorker(); mode = 'partial-failure';
    await page.goto(`https://dashboard.test/executions/${serverExecutionId}`);
    await page.getByRole('button', { name: 'Inspeccionar registro del agente' }).click();
    await page.getByText('El agente no pudo demostrar la propiedad de Git ni el estado de integración actuales.').waitFor();
    assert.equal(await page.getByRole('button', { name: 'Limpiar recursos' }).count(), 0, 'failed preview does not offer apply');

    current = { ...current, workerExecutionId: undefined };
    mode = 'normal';
    await page.goto(`https://dashboard.test/executions/${serverExecutionId}`);
    await page.getByRole('button', { name: 'Inspeccionar inventario del agente' }).click();
    await page.getByText('La instantánea del inventario del agente se recibió mediante el servidor.').waitFor();
    await page.getByText('El registro del agente no tiene ejecución del servidor', { exact: true }).waitFor();
    await page.getByText('Propiedad desactualizada', { exact: true }).waitFor();

    mode = 'unauthorized';
    await page.goto(`https://dashboard.test/executions/${serverExecutionId}`);
    await page.getByRole('button', { name: 'Inspeccionar inventario del agente' }).click();
    await page.getByRole('heading', { name: 'Inicio de sesión de administración' }).waitFor();
    assert.equal(writes.filter(value => value.action === 'inventory').length, 2, 'unauthorized retry did not duplicate the accepted inventory request');
    assert.deepEqual(errors, []);
    console.log('Managed execution maintenance browser review passed: bounded filters/pagination, action matrix, preview/apply confirmation, offline/stale/unauthorized, inventory provenance, partial failure, accessibility, responsive layout and EN/ES.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
