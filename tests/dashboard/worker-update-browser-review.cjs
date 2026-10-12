// Production dashboard with deterministic Server responses; no real nodes or updates.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve('src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[2] || '/tmp/worker-update-review');
const workerId = 'a'.repeat(32);
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage(), writes = [], errors = [];
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.clock.install({ time: props.now });
    page.on('pageerror', error => errors.push(error.message));
    let operation = null, loseResponse = false;
    let worker = { ...props.observations[0], workerId, workerVersion: '0.14.0', lastHeartbeatAtUtc: new Date(props.now).toISOString(), configurationRevision: 1, availability: 'online', lifecycleState: 'not-ready', configurationSynchronization: 'error', managedDiagnostics: { retrieval: 'retrieved', synchronization: 'error', source: 'server-retrieved', failureStage: 'contract-validation', diagnosticCode: 'managed-contract-invalid', projects: [] }, capabilities: [{ type: 'protocol', name: 'worker-self-update-v1' }], telegram: { enabled: false, chatId: '12345', tokenConfigured: true } };
    const endpoint = `/api/v1/workers/${workerId}`;
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), pathname = url.pathname;
      if (pathname.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, pathname.slice('/dashboard-assets/preview/'.length)), contentType: pathname.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (pathname === `/workers/${workerId}`) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (pathname === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (pathname === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (request.method() !== 'GET') {
        assert.equal(pathname, `${endpoint}/update`);
        assert.equal(request.headers()['x-codex-csrf'], 'fixture-csrf');
        const body = request.postDataJSON();
        assert.deepEqual(Object.keys(body), ['operationId']);
        writes.push(body);
        operation ??= { operationId: body.operationId, workerId, state: 'waiting-for-idle' };
        return route.fulfill({ status: loseResponse ? 503 : 200, json: operation });
      }
      const fixtures = {
        '/api/status': { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' },
        '/api/version': { version: '0.15.0' }, '/api/v1/workers': [worker], [endpoint]: worker,
        [`${endpoint}/update`]: operation,
        [`${endpoint}/diagnostics`]: { ...props.diagnostics, canActivate: true, activationBlockingReasons: [], capabilityObservationsCurrent: true },
        [`${endpoint}/credential-access`]: { status: 'active' },
        '/api/v1/nodes': [{ ...props.nodes[0], id: workerId }], [`/api/v1/nodes/${workerId}/commands`]: [],
        '/api/v1/projects': props.projects, '/api/v1/executions': [], '/api/v1/provisioning': []
      };
      assert.ok(Object.hasOwn(fixtures, pathname), `Unexpected request ${pathname}`);
      return route.fulfill({ json: fixtures[pathname] });
    });

    async function verifyRefreshStability(theme, width, refreshedCapacity) {
      await page.setViewportSize({ width, height: 900 });
      const dark = await page.locator('html').evaluate(element => element.classList.contains('dark-mode'));
      if (dark !== (theme === 'dark')) await page.getByRole('button', { name: `Use ${theme} mode` }).click();
      await page.getByRole('button', { name: 'Edit worker', exact: true }).click();
      const dialog = page.getByRole('dialog', { name: 'Edit worker' });
      const name = dialog.getByRole('textbox', { name: 'Worker name' });
      await dialog.getByRole('checkbox', { name: 'Enable Telegram notifications' }).waitFor();
      await dialog.getByRole('checkbox', { name: /Clear saved token/ }).waitFor();
      const token = dialog.getByRole('textbox', { name: 'Replace bot token' });
      assert.equal(await token.getAttribute('type'), 'password');
      assert.equal(await token.inputValue(), '', 'Saved Telegram tokens are never populated in the editor.');
      await dialog.getByText('A bot token is configured. Its value cannot be displayed.', { exact: true }).waitFor();
      await name.fill(`Unsaved draft ${theme} ${width}`);
      const slots = dialog.getByRole('spinbutton', { name: 'Server assignment limit' });
      await slots.focus();
      const body = dialog.locator('.worker-editor-body');
      await body.evaluate(element => { element.scrollTop = Math.min(120, element.scrollHeight - element.clientHeight); });
      const beforeScroll = await body.evaluate(element => element.scrollTop);
      assert.ok(beforeScroll > 0, 'The editor has its own scrollable body.');
      await page.evaluate(() => {
        window.__editorDialog = document.querySelector('[role="dialog"]');
        window.__editorNameInput = window.__editorDialog.querySelector('input');
        window.__editorFocusedInput = document.activeElement;
      });
      const beforeBounds = await dialog.boundingBox();
      worker = { ...worker, displayName: `Refreshed ${theme} ${width}`, maximumCapacity: refreshedCapacity, activeExecutions: 0 };
      await page.clock.runFor(5001);
      await dialog.getByText(`${refreshedCapacity} total slots`, { exact: true }).waitFor();
      assert.equal(await name.inputValue(), `Unsaved draft ${theme} ${width}`);
      assert.equal(await slots.isEnabled(), true);
      assert.equal(await dialog.getByRole('button', { name: 'Save changes', exact: true }).isEnabled(), true, 'The dirty draft remains saveable.');
      assert.equal(await page.evaluate(() => window.__editorDialog === document.querySelector('[role="dialog"]')), true, 'The dialog element is retained through a Worker query refresh.');
      assert.equal(await page.evaluate(() => window.__editorNameInput.isConnected), true, 'The edited input remains mounted.');
      assert.equal(await page.evaluate(() => window.__editorFocusedInput === document.activeElement), true, 'Keyboard focus remains on the active field.');
      assert.equal(await body.evaluate(element => element.scrollTop), beforeScroll, 'The editor scroll position is retained.');
      const afterBounds = await dialog.boundingBox();
      assert.deepEqual(afterBounds, beforeBounds, 'The panel stays anchored at the same size while data refreshes.');
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'The editor does not cause page-wide horizontal overflow.');
      await page.screenshot({ path: path.join(output, `editor-refresh-${theme}-${width}.png`) });
      await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    }

    await page.goto(`https://dashboard.test/workers/${workerId}`);
    await verifyRefreshStability('dark', 1440, 3);
    await verifyRefreshStability('light', 375, 4);
    await page.setViewportSize({ width: 1440, height: 900 });
    const light = await page.locator('html').evaluate(element => !element.classList.contains('dark-mode'));
    if (light) await page.getByRole('button', { name: 'Use dark mode' }).click();
    await page.getByRole('button', { name: 'Edit worker', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Edit worker' });
    await dialog.getByText(/controlled update can proceed even when managed configuration is invalid/).waitFor();
    await dialog.getByRole('button', { name: /^Update worker/ }).click();
    await dialog.getByText('Update requested. Waiting for active executions to finish.', { exact: true }).waitFor();
    await dialog.locator('#worker-update-help').getByText(/An update is already requested/).waitFor();
    assert.equal(writes.length, 1);
    assert.equal(await dialog.getByRole('button', { name: /^Update worker/ }).isDisabled(), true);
    await page.screenshot({ path: path.join(output, 'waiting-dark.png') });
    operation.state = 'updating';
    await page.clock.runFor(6000);
    await dialog.getByText('Updating; waiting for restart, reconnect and readiness checks.', { exact: true }).waitFor();
    operation.state = 'failed';
    await page.clock.runFor(6000);
    await dialog.getByText(/Scheduling remains blocked/).waitFor();
    await page.screenshot({ path: path.join(output, 'failure-dark.png') });
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Use light mode' }).click();
    await page.getByRole('button', { name: 'Edit worker', exact: true }).click();
    await dialog.getByText(/Scheduling remains blocked/).waitFor();
    await page.screenshot({ path: path.join(output, 'failure-light.png') });
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByLabel('Language', { exact: true }).filter({ visible: true }).selectOption('es');
    await page.getByRole('button', { name: 'Editar agente', exact: true }).click();
    await page.getByRole('dialog').getByText(/La programación sigue bloqueada/).waitFor();
    assert.equal(writes.length, 1);
    // An uncertain response is recovered by reading durable Server state, without another write.
    operation = null; loseResponse = true;
    await page.getByRole('dialog').getByRole('button', { name: 'Cancelar', exact: true }).click();
    await page.getByLabel('Language', { exact: true }).filter({ visible: true }).selectOption('en');
    await page.getByRole('button', { name: 'Edit worker', exact: true }).click();
    await page.clock.runFor(6000);
    await dialog.getByRole('button', { name: /^Update worker/ }).click();
    await dialog.getByText(/request could not be confirmed/).waitFor();
    await dialog.getByRole('button', { name: 'Refresh current settings', exact: true }).click();
    await dialog.getByText(/Waiting for active executions/).waitFor();
    assert.equal(writes.length, 2);
    assert.deepEqual(errors, []);
    console.log('Worker update editor: drain, progress, failure, localization, themes and lost-response reads passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
