// Built Home review: deterministic contracts only; no live providers or credentials.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props, capability, node, worker, current } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || '/tmp/home-browser-review');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    await page.clock.setFixedTime(props.now);
    const mutations = [], errors = [];
    let scenario = 'configured', stage = 'Validation';
    const server = { ...node, id: 'server', kind: 'server', health: 'healthy', capabilities: [{ ...capability, definition: { ...capability.definition, id: 'github-cli' } }] };
    page.on('pageerror', error => errors.push(error.message));
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), endpoint = new URL(request.url()).pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) {
        const relative = endpoint.slice('/dashboard-assets/preview/'.length);
        assert.ok(!relative.includes('..'));
        return route.fulfill({ path: path.join(assets, relative), contentType: relative.endsWith('.css') ? 'text/css' : 'text/javascript' });
      }
      if (endpoint.startsWith('/dashboard-preview')) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (request.method() !== 'GET') mutations.push(endpoint);
      if (endpoint === '/api/v1/administration/session') return route.fulfill({ json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (scenario === 'unavailable' && ['/api/v1/nodes', '/api/v1/nodes/server/github-connection'].includes(endpoint)) return route.fulfill({ status: 503 });
      const empty = scenario === 'empty';
      const fixtures = {
        '/api/status': { state: 'running', version: 'fixture', startedAtUtc: '2026-01-01T00:00:00Z' },
        '/api/v1/workers': empty || scenario === 'project-first' ? [] : [worker],
        '/api/v1/projects': empty || scenario === 'worker-first' ? [] : props.projects,
        '/api/v1/nodes': [ { ...server, capabilities: empty ? [] : server.capabilities, observationsStale: scenario === 'stale' }, ...(empty || scenario === 'project-first' ? [] : [node]) ],
        '/api/v1/nodes/server/github-connection': { commands: [], provisioningEnabled: false, elevationAllowed: false },
        '/api/v1/executions': empty ? [] : [{ ...current, currentStage: stage }, ...(scenario === 'recovery' ? [{ ...current, id: 'recover', state: 'Failed', recoveryState: 'IntegrationUncertain', recoveryReason: 'Verify retained integration evidence' }] : [])]
      };
      return route.fulfill({ json: fixtures[endpoint] ?? [] });
    });
    for (const width of [1280, 375]) {
      await page.setViewportSize({ width, height: 1000 });
      for (scenario of ['empty', 'configured', 'stale', 'unavailable', 'project-first', 'worker-first', 'recovery']) {
        await page.goto('https://dashboard.test/dashboard-preview/home');
        await page.getByRole('heading', { name: 'Home', exact: true }).waitFor();
        await page.getByRole('button', { name: 'Refresh system state' }).waitFor();
        await page.waitForFunction(() => !document.body.textContent.includes('Loading execution activity'));
        const setup = page.getByText('Completed setup actions', { exact: true });
        if (scenario === 'configured') {
          await page.getByText('No blockers reported in the available observations.', { exact: true }).waitFor();
          assert.equal(await page.getByRole('link', { name: 'Connect Server GitHub' }).count(), 0);
          await setup.click();
          const diagnostics = page.getByText('System diagnostics and capacity', { exact: true });
          await diagnostics.click();
          await page.getByText('Server: running · version fixture', { exact: true }).waitFor();
          await diagnostics.focus();
          stage = 'Integration';
          const refreshed = page.waitForResponse(response => response.url().includes('/api/v1/executions'));
          await page.getByRole('button', { name: 'Refresh system state' }).evaluate(element => element.click());
          await refreshed;
          await page.getByText('Running · Integration', { exact: true }).first().waitFor();
          await page.waitForFunction(() => document.querySelectorAll('details[open]').length >= 2);
          assert.equal(await diagnostics.evaluate(element => element === document.activeElement && element.parentElement.open), true);
          assert.equal(await setup.evaluate(element => element.parentElement.open), true);
        }
        if (scenario === 'stale') await page.getByText('Observations stale · check authentication').first().waitFor();
        if (scenario === 'unavailable') await page.getByText('Connection observations unavailable').first().waitFor();
        if (scenario === 'empty') await page.getByRole('link', { name: 'Add Worker' }).waitFor();
        if (scenario === 'project-first') await page.getByRole('link', { name: 'Add Worker' }).waitFor();
        if (scenario === 'worker-first') await page.getByRole('link', { name: 'Create project' }).waitFor();
        if (scenario === 'recovery') {
          // Exact resource evidence is preserved independently of its friendly label.
          assert.ok(await page.locator('a[href="/executions/recover"]').count() > 0);
          await page.getByText('Recovery: IntegrationUncertain').first().waitFor();
        }
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `No overflow: ${width}/${scenario}`);
        await page.screenshot({ path: path.join(output, `${scenario}-${width}.png`), fullPage: true });
      }
    }
    scenario = 'stale';
    await page.goto('https://dashboard.test/dashboard-preview/settings?node=server');
    await page.getByText('Observations stale · check authentication').waitFor();
    await page.reload();
    await page.getByText('Observations stale · check authentication').waitFor();
    assert.deepEqual(mutations, []);
    assert.deepEqual(errors, []);
    console.log('Home browser review passed: desktop/mobile, empty/active/stale/unavailable, independent setup, recovery, refresh disclosure/focus, shared Settings projection, no mutations.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
