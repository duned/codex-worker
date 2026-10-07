// Optional production-bundle review with deterministic Server-shaped fixtures.
// NODE_PATH=/path/to/playwright/node_modules node ... [ASSET_DIRECTORY] [SCREENSHOT_DIRECTORY]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || '/tmp/dashboard-preview-review');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.clock.setFixedTime(props.now);
    const errors = [], mutations = [];
    let signedIn = true, workers = props.observations, failWorkers = false;
    let blockedRead = null;
    const readStarted = new Promise(resolve => { blockedRead = { started: resolve, release: null, enabled: false }; });
    page.on('pageerror', error => { errors.push(error.message); });
    page.setDefaultTimeout(10000);
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url());
      const endpoint = url.pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) {
        const relative = endpoint.slice('/dashboard-assets/preview/'.length);
        assert.ok(!relative.includes('..'));
        return route.fulfill({ path: path.join(assets, relative), contentType: relative.endsWith('.css') ? 'text/css' : 'text/javascript' });
      }
      if (endpoint.startsWith('/dashboard-preview')) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (request.method() !== 'GET') mutations.push({ path: endpoint, method: request.method(), csrf: request.headers()['x-codex-csrf'] });
      if (endpoint === '/api/v1/administration/session') {
        if (request.method() === 'DELETE') { signedIn = false; return route.fulfill({ status: 204 }); }
        if (request.method() === 'POST') signedIn = true;
        return route.fulfill({ status: signedIn ? 200 : 401, json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      }
      assert.equal(request.method(), 'GET', 'Preview never exposes resource mutations.');
      const fixtures = {
        '/api/v1/workers': workers, '/api/v1/nodes': props.nodes, '/api/v1/projects': props.projects,
        '/api/v1/executions': props.executions, '/api/v1/workers/worker-a/diagnostics': props.diagnostics
      };
      if (endpoint === '/api/v1/workers' && blockedRead.enabled) {
        await new Promise(resolve => { blockedRead.release = resolve; blockedRead.started(); });
      }
      return route.fulfill({ status: failWorkers && endpoint === '/api/v1/workers' ? 503 : 200, json: fixtures[endpoint] ?? [] });
    });
    const deepLink = 'https://dashboard.test/dashboard-preview/workers/worker-a?step=preparation&project=project-a';
    for (const width of [1280, 375]) {
      await page.setViewportSize({ width, height: 1000 });
      await page.goto(deepLink);
      await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
      assert.equal(new URL(page.url()).search, '?step=preparation&project=project-a');
      assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), true);
      assert.equal(await page.getByRole('button', { name: 'Activate scheduling' }).count(), 0);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `No horizontal overflow at ${width}px.`);
      const legacy = page.getByRole('link', { name: 'Open Worker detail and administration' });
      assert.equal(await legacy.getAttribute('href'), '/workers/worker-a?step=preparation&project=project-a');
      await page.screenshot({ path: path.join(output, `worker-${width}.png`), fullPage: true });
      await page.reload();
      await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    }
    await page.setViewportSize({ width: 1280, height: 1000 });
    await page.evaluate(() => { window.navigationFixture = 'retained'; });
    await page.getByRole('link', { name: 'Home', exact: true }).click();
    await page.getByRole('link', { name: 'Open home in current dashboard' }).waitFor();
    assert.equal(new URL(page.url()).pathname, '/dashboard-preview/home');
    assert.equal(await page.evaluate(() => window.navigationFixture), 'retained', 'Router navigation must keep one session owner.');
    await page.goBack();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    await page.getByRole('button', { name: 'Use light mode' }).click();
    await page.reload();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), false);
    await page.goto('https://dashboard.test/dashboard-preview/projects/project-a?issue=27&label=review');
    assert.equal(await page.getByRole('link', { name: 'Open projects in current dashboard' }).getAttribute('href'), '/projects/project-a?issue=27&label=review');
    await page.goBack();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    failWorkers = true; await page.reload();
    await page.getByRole('alert').filter({ hasText: 'HTTP 503' }).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Build Worker North' }).count(), 0);
    failWorkers = false; workers = []; await page.reload();
    await page.getByText('Worker unavailable or deleted.', { exact: false }).waitFor();
    workers = props.observations; await page.reload();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    // Explicit coordination: logout while the Worker read is in flight.
    blockedRead.enabled = true;
    await page.reload();
    await readStarted;
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    blockedRead.release();
    await page.getByText('Administration sign in', { exact: true }).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Build Worker North' }).count(), 0);
    assert.deepEqual(mutations, [{ path: '/api/v1/administration/session', method: 'DELETE', csrf: 'fixture-csrf' }]);
    assert.deepEqual(errors, []);
    console.log('Preview browser review passed: desktop/mobile, direct query context, reload/back, theme, failed/deleted resources, logout and no resource mutations.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
