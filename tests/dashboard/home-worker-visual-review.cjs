// Capture the production routes with deterministic contracts, without live services.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || 'docs/images/home-worker-alignment');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    for (const theme of ['dark', 'light']) for (const width of [1280, 375]) {
      const context = await browser.newContext({ viewport: { width, height: 1000 } });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.clock.setFixedTime(props.now);
      await page.addInitScript(theme => localStorage.setItem('codex-dashboard-preferences', JSON.stringify({ version: 1, state: { theme } })), theme);
      await page.route('https://dashboard.test/**', async route => {
        const request = route.request(), endpoint = new URL(request.url()).pathname;
        assert.equal(request.method(), 'GET');
        if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
        if (endpoint === '/home' || endpoint === '/workers/worker-a') return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
        if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
        const fixtures = {
          '/api/v1/administration/session': { csrfToken: 'fixture', expiresAtUtc: '2026-01-01T01:00:00Z' },
          '/api/status': { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' },
          '/api/v1/workers': props.observations,
          '/api/v1/workers/worker-a': props.observations[0],
          '/api/v1/workers/worker-a/credential-access': { status: 'active' },
          '/api/v1/workers/worker-a/diagnostics': props.diagnostics,
          '/api/v1/nodes': props.nodes,
          '/api/v1/projects': props.projects,
          '/api/v1/executions': props.executions,
          '/api/v1/nodes/server/github-connection': { commands: [], provisioningEnabled: false, elevationAllowed: false }
        };
        return route.fulfill({ json: fixtures[endpoint] ?? [] });
      });
      for (const [route, heading] of [['home', 'Home'], ['workers/worker-a', 'Build Worker North']]) {
        await page.goto(`https://dashboard.test/${route}`);
        await page.getByRole('heading', { name: heading, exact: true }).waitFor();
        await page.getByRole('link', { name: 'Issue #27', exact: true }).first().waitFor();
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), theme === 'dark');
        assert.equal(await page.getByText(/\d{4}-\d{2}-\d{2}T\d{2}:/).count(), 0);
        if (route === 'home') assert.equal(await page.locator('#home-projects').locator('..').evaluate(element => element.tagName), 'SECTION');
        await page.screenshot({ path: path.join(output, `${route === 'home' ? 'home' : 'worker'}-${theme}-${width}.png`), fullPage: true });
      }
      assert.deepEqual(errors, []);
      await context.close();
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
