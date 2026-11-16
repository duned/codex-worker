// Optional production-bundle review; all Server/node responses are deterministic fixtures.
// NODE_PATH=/path/to/playwright/node_modules node tests/dashboard/worker-recovery-browser-review.cjs [ASSETS] [SCREENSHOTS]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || '/tmp/worker-recovery-review');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage(), writes = [], errors = [];
    await page.clock.setFixedTime(props.now);
    page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(error.message));
    const workerId = 'a'.repeat(32), endpoint = `/api/v1/workers/${workerId}`;
    let blocked = true, recovered = false;
    const worker = () => ({ ...props.observations[0], workerId, schedulingPolicy: recovered ? 'Enabled' : 'Draining',
      schedulingBlocker: recovered ? null : 'worker-operation-unresolved', activeExecutions: 0, activeAssignments: 0,
      lifecycleState: 'stopped', availability: 'offline', availableCapacity: 0,
      workerVersion: '0.15.0', capabilities: [{ type: 'protocol', name: 'worker-restart-v1' }] });
    const operation = () => ({ operationId: 'b'.repeat(32), workerId, state: 'failed',
      drainReleasedAtUtc: recovered ? new Date(props.now).toISOString() : null,
      restart: true, originalVersion: '0.15.0', expectedVersion: '0.15.0', diagnostic: 'timeout-inspect-service-and-journal-before-retry', recovery: {
        reportedVersion: '0.15.1', checkedAtUtc: new Date(props.now).toISOString(), schedulingBlocked: !recovered, eligible: false, releaseDrainEligible: !blocked && !recovered,
        checks: [{ code: 'fresh-heartbeat', satisfied: true, description: 'Worker heartbeat is fresh.', nextStep: 'Restore the connection.' },
          { code: 'no-active-executions', satisfied: !blocked, description: 'Worker reports no active executions.', nextStep: 'Wait for an idle heartbeat.' },
          { code: 'running-lifecycle', satisfied: false, description: 'Worker lifecycle is running.', nextStep: 'Inspect the Worker service.' },
          ...[['failed-operation', 'Operation has failed or timed out.'],
            ['no-active-assignments', 'No active Server assignments.'], ['no-active-leases', 'No active Server leases.'],
            ['post-operation-heartbeat', 'Heartbeat was received after the operation started.']]
            .map(([code, description]) => ({ code, satisfied: true, description, nextStep: '' }))]
      } });
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), pathname = new URL(request.url()).pathname;
      if (pathname.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, pathname.slice('/dashboard-assets/preview/'.length)), contentType: pathname.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (pathname === `/workers/${workerId}`) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (pathname === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (pathname === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (request.method() !== 'GET') {
        writes.push(pathname);
        assert.equal(pathname, `${endpoint}/update/release-drain`, 'Recovery only releases the Server drain.');
        assert.deepEqual(request.postDataJSON(), { operationId: 'b'.repeat(32) });
        assert.equal(new URL(request.url()).searchParams.get('nodeQuiescent'), 'true');
        assert.equal(request.headers()['x-codex-csrf'], 'fixture-csrf');
        if (blocked) return route.fulfill({ status: 409, json: { reason: 'worker-update-recovery-readiness-required', recovery: operation().recovery } });
        recovered = true;
        return route.fulfill({ json: operation() });
      }
      const fixtures = {
        '/api/status': { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' }, '/api/version': { version: '0.15.0' },
        '/api/v1/workers': [worker()], [endpoint]: worker(), [`${endpoint}/update/recovery`]: operation(),
        [`${endpoint}/diagnostics`]: { ...props.diagnostics, canActivate: true, activationBlockingReasons: [], capabilityObservationsCurrent: true },
        [`${endpoint}/credential-access`]: { status: 'active' }, [`${endpoint}/journal`]: { state: 'ready', lines: [] },
        '/api/v1/nodes': [{ ...props.nodes[0], id: workerId, connectivity: 'disconnected', executionReadiness: 'not-ready' }], [`/api/v1/nodes/${workerId}/commands`]: [],
        '/api/v1/projects': props.projects, '/api/v1/executions': [], '/api/v1/provisioning': []
      };
      assert.ok(Object.hasOwn(fixtures, pathname), `Unexpected request ${pathname}`);
      return route.fulfill({ json: fixtures[pathname] });
    });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(`https://dashboard.test/workers/${workerId}`);
    const action = page.getByRole('button', { name: 'Restore Server scheduling policy' });
    await action.waitFor();
    await page.getByText('Latest heartbeat version', { exact: true }).waitFor();
    await page.getByText('0.15.1', { exact: true }).waitFor();
    assert.equal(await action.isDisabled(), true);
    await page.getByText('Paused by operation', { exact: true }).waitFor();
    await page.getByText('Wait for an idle heartbeat.', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Refresh recovery checks', exact: true }).click();
    assert.equal(writes.length, 0, 'Status refresh is read-only.');
    for (const width of [1440, 375]) {
      await page.setViewportSize({ width, height: 1000 });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `blocked-${width}.png`), fullPage: true });
    }
    blocked = false;
    await page.getByRole('button', { name: 'Refresh recovery checks', exact: true }).click();
    await page.getByText('Wait for an idle heartbeat.', { exact: true }).waitFor({ state: 'hidden' });
    assert.equal(await action.isDisabled(), true, 'Node quiescence still needs operator confirmation.');
    await page.getByRole('checkbox', { name: 'I confirm the node is quiescent and no restart is pending.' }).focus();
    await page.keyboard.press('Space');
    await action.click();
    await page.getByText('Server scheduling policy restored. The restart remains failed; Worker service recovery has not been confirmed.', { exact: true }).waitFor();
    await page.getByText('failed: timeout-inspect-service-and-journal-before-retry', { exact: true }).waitFor();
    await page.getByText('Disconnected', { exact: true }).waitFor();
    await page.getByText('not-ready', { exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Restore Server scheduling policy' }).count(), 0);
    assert.equal(writes.length, 1);
    assert.deepEqual(errors, []);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.screenshot({ path: path.join(output, 'recovered.png'), fullPage: true });
    const reference = await browser.newPage({ viewport: { width: 1600, height: 1180 } });
    await reference.setContent(fs.readFileSync('docs/mockups/worker-detail-approved.svg', 'utf8'));
    await reference.screenshot({ path: path.join(output, 'approved-worker.png'), fullPage: true });
    await reference.setContent(fs.readFileSync('docs/mockups/worker-detail-controls-refinement.svg', 'utf8'));
    await reference.screenshot({ path: path.join(output, 'approved-controls.png'), fullPage: true });
    console.log('Worker recovery browser checks passed; screenshots:', output);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
