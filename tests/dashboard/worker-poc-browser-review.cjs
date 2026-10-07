// Production PoC review with Server-shaped fixtures; no legacy request bridge.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { readDashboard } = require('./server-dashboard-source.cjs');
const { props } = require('./worker-poc-fixtures.cjs');
const output = path.resolve(process.argv[2] || '/tmp/codex-worker-poc-review');
const assets = path.resolve('src/CodexServer/obj/worker-poc');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage(), errors = [], writes = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.clock.install({ time: props.now });
    await page.addInitScript(() => {
      const fetch = window.fetch.bind(window);
      window.fetch = async (input, options) => {
        const response = await fetch(input, options);
        if (input !== '/api/v1/events/stream' || !response.ok) return response;
        const bytes = new Uint8Array(await response.arrayBuffer());
        return new Response(new ReadableStream({ start(controller) {
          controller.enqueue(bytes);
          options.signal.addEventListener('abort', () => controller.close(), { once: true });
        } }), { headers: { 'Content-Type': 'text/event-stream' } });
      };
    });
    let signedIn = true, canActivate = true, loseResponse = false, empty = false;
    let policy = 'Disabled', releaseWrite, writeStarted;
    const writing = new Promise(resolve => { writeStarted = resolve; });
    const commands = [{ id: 'fixture-command', createdAtUtc: '2026-01-01T00:02:00Z', status: 'Pending', request: { nodeId: 'worker-a', capabilityId: 'codex-cli', action: 'CheckAuthentication' } }];
    const registry = () => ({ ...props.observations[0], schedulingPolicy: policy, authenticationCredentialStatus: 'active' });
    await page.route('https://worker.test/**', async route => {
      const request = route.request(), endpoint = new URL(request.url()).pathname;
      if (endpoint === '/dashboard-assets/preview/assets/theme.js') return route.fulfill({ path: path.join(assets, 'preview/assets/theme.js'), contentType: 'text/javascript' });
      if (endpoint.startsWith('/dashboard-assets/')) return route.fulfill({ path: path.join(assets, endpoint.endsWith('.js') ? 'poc.js' : 'poc.css'), contentType: endpoint.endsWith('.js') ? 'text/javascript' : 'text/css' });
      if (endpoint === '/workers/worker-a/poc') return route.fulfill({ body: readDashboard({ workerPoc: true }), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') {
        if (request.method() === 'DELETE') { signedIn = false; return route.fulfill({ status: 204 }); }
        if (request.method() === 'POST') { assert.equal(request.headers().authorization, 'Bearer fixture-token'); signedIn = true; }
        return route.fulfill({ status: signedIn ? 200 : 401, json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      }
      if (!signedIn) return route.fulfill({ status: 401 });
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ contentType: 'text/event-stream', body: `event: workers\ndata: ${JSON.stringify(empty ? [] : [registry()])}\n\n` });
      if (request.method() !== 'GET') {
        writes.push({ endpoint, csrf: request.headers()['x-codex-csrf'], body: request.postDataJSON() });
        assert.equal(endpoint, '/api/v1/workers/worker-a/scheduling-policy');
        policy = request.postDataJSON().policy;
        if (loseResponse) { writeStarted(); await new Promise(resolve => { releaseWrite = resolve; }); }
        return route.fulfill({ status: loseResponse ? 503 : 200, json: registry() });
      }
      const fixtures = { '/api/v1/workers': empty ? [] : [registry()], '/api/v1/workers/worker-a': registry(),
        '/api/v1/workers/worker-a/diagnostics': { ...props.diagnostics, canActivate, activationBlockingReasons: canActivate ? [] : ['Current readiness unavailable'] },
        '/api/v1/nodes/worker-a/commands': commands, '/api/v1/nodes': props.nodes, '/api/v1/projects': props.projects, '/api/v1/executions': empty ? [] : props.executions };
      assert.ok(Object.hasOwn(fixtures, endpoint), `Unexpected API: ${endpoint}`);
      return route.fulfill({ json: fixtures[endpoint] });
    });
    const url = 'https://worker.test/workers/worker-a/poc?step=preparation&project=project-a';
    for (const theme of ['dark', 'light']) for (const width of [1280, 375, 640]) {
      await page.setViewportSize({ width, height: 900 }); await page.goto(url);
      await page.evaluate(theme => localStorage.setItem('codex-dashboard-preferences', JSON.stringify({ version: 1, state: { theme } })), theme);
      await page.reload();
      assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), theme === 'dark');
      await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
      await page.getByRole('link', { name: 'Issue #27' }).waitFor();
      await page.getByText('Pending', { exact: true }).waitFor();
      assert.equal(await page.getByRole('link', { name: 'Issue #27' }).getAttribute('href'), 'https://github.com/owner/repo/issues/27');
      assert.equal(await page.locator('#poc-legacy-owner,#view-title').count(), 0);
      assert.equal(await page.evaluate(() => !!window.codexWorkerPoc), false);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      const main = await page.locator('.poc-main').boundingBox(), rail = await page.locator('.poc-control-rail').boundingBox();
      assert.ok(width > 1000 ? rail.x >= main.x + main.width : rail.y >= main.y + main.height);
      await page.getByRole('button', { name: 'Activate scheduling' }).click();
      const dialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Activate scheduling' }) });
      await dialog.waitFor();
      assert.equal(await page.getByRole('button', { name: 'Cancel', exact: true }).evaluate(element => element === document.activeElement), true);
      for (let step = 0; step < 4; step++) {
        await page.keyboard.press('Tab');
        assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true, 'Modal contains keyboard focus.');
      }
      await page.screenshot({ path: path.join(output, `confirmation-${theme}-${width}.png`), fullPage: true });
      await page.keyboard.press('Escape'); await dialog.waitFor({ state: 'hidden' });
      await page.clock.runFor(20); // React Aria restores focus on the next animation frame.
      assert.equal(writes.length, 0, 'Dismissal never submits.');
      assert.equal(await page.getByRole('button', { name: 'Activate scheduling' }).evaluate(element => element === document.activeElement), true, 'Focus restores to trigger.');
      await page.screenshot({ path: path.join(output, `worker-detail-${theme}-${width}.png`), fullPage: true });
    }
    await page.evaluate(() => localStorage.removeItem('codex-dashboard-preferences')); await page.reload();
    await page.setViewportSize({ width: 1280, height: 900 });
    // Cache says activation is possible; the uncached current check refuses it.
    canActivate = false;
    await page.getByRole('button', { name: 'Activate scheduling' }).click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Activation blocked: Current readiness unavailable', { exact: true }).waitFor();
    assert.equal(writes.length, 0);
    canActivate = true; await page.reload();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    loseResponse = true;
    await page.getByRole('button', { name: 'Drain worker' }).click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await writing;
    assert.equal(await page.getByRole('button', { name: 'Confirm', exact: true }).isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Cancel', exact: true }).isDisabled(), true);
    await page.keyboard.press('Enter'); await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('dialog').count(), 1, 'Pending cannot dismiss.');
    assert.equal(writes.length, 1); releaseWrite();
    await page.getByText('Request failed (HTTP 503). State may be stale; refresh before retrying.', { exact: true }).waitFor();
    await page.clock.runFor(5001);
    assert.equal(await page.getByRole('button', { name: 'Deactivate', exact: true }).isDisabled(), true);
    assert.equal(writes.length, 1);
    await page.getByRole('button', { name: 'Refresh authoritative state' }).click();
    await page.getByText('Authoritative state refreshed.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Deactivate', exact: true }).isDisabled(), false);
    assert.equal(writes.length, 1); assert.equal(writes[0].csrf, 'fixture-csrf');
    await page.getByRole('button', { name: 'Use light mode' }).click(); await page.reload();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), false);
    // bfcache-like lifecycle restores via cookie checks, never a login/write.
    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })));
    await page.getByText('Administration sign in', { exact: true }).waitFor();
    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await page.getByText('Administration sign in', { exact: true }).waitFor();
    for (const theme of ['dark', 'light']) for (const width of [1280, 375, 640]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(theme => localStorage.setItem('codex-dashboard-preferences', JSON.stringify({ version: 1, state: { theme } })), theme);
      await page.reload(); await page.getByText('Administration sign in', { exact: true }).waitFor();
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `login-${theme}-${width}.png`), fullPage: true });
    }
    await page.getByLabel('Server management token').fill('fixture-token');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    empty = true; await page.reload();
    await page.getByText('Worker unavailable or deleted.', { exact: false }).waitFor();
    assert.deepEqual(errors, []);
    console.log('PoC browser review passed: desktop/mobile, managed dialogs, fresh checks, uncertain locks/reconciliation, theme, bfcache, logout/relogin and no legacy bridge.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
