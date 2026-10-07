// Production bundle with deterministic Server-shaped fixtures. Never capture private inputs/codes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { node, capability, worker } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve(process.argv[2] || 'src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[3] || '/tmp/settings-browser-review');
const epoch = Date.parse('2026-01-01T00:00:00Z');
const metadata = { id: 'credential-a', provider: 'Provider', type: 'API token', secretReference: 'credential:credential-a', status: 'Ready', version: 1, createdAtUtc: new Date(epoch).toISOString(), updatedAtUtc: new Date(epoch).toISOString() };
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    page.setDefaultTimeout(10000);
    page.setDefaultNavigationTimeout(15000);
    await page.clock.setFixedTime(epoch);
    const errors = [], mutations = [], reads = [];
    let items = [metadata], operations = [], authentication = 'Missing', installation = 'Installed', lost = false, deleted = false;
    page.on('pageerror', error => errors.push(error.message));
    const server = { ...node, id: 'server', kind: 'server', displayName: 'Control Server', capabilities: [{ ...capability, definition: { ...capability.definition, id: 'github-cli', displayName: 'GitHub CLI' }, availableActions: ['prepareauthentication', 'login', 'checkauthentication', 'install', 'refresh'] }] };
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), endpoint = new URL(request.url()).pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (/^\/(home|projects|workers|executions|settings)(?:\/|$)/.test(endpoint)) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') return route.fulfill(request.method() === 'DELETE' ? { status: 204 } : { json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      if (endpoint.endsWith('/stream')) return route.fulfill({ status: 503 });
      if (request.method() !== 'GET') {
        assert.equal(request.headers()['x-codex-csrf'], 'fixture-csrf');
        const body = request.postDataJSON(); mutations.push({ endpoint, body });
        if (endpoint === '/api/v1/provisioning/commands') {
          const result = { id: 'command-' + (operations.length + 1), createdAtUtc: new Date(epoch).toISOString(), status: body.action === 'PrepareAuthentication' ? 'Succeeded' : 'Pending', diagnostic: 'None', request: body, deadlineUtc: new Date(epoch + 20000).toISOString() };
          operations.unshift(result);
          if (lost) { lost = false; return route.abort(); }
          return route.fulfill({ json: result });
        }
        if (endpoint.endsWith('/cancel') || endpoint.endsWith('/reconcile')) {
          operations[0] = { ...operations[0], status: endpoint.endsWith('/cancel') ? 'Cancelled' : 'TimedOut' };
          return route.fulfill({ json: operations[0] });
        }
        if (endpoint === '/api/v1/credentials') { items.push({ ...metadata, id: 'credential-new', provider: body.provider, type: body.type }); return route.fulfill({ json: items.at(-1) }); }
        if (endpoint.endsWith('/secret')) items[0] = { ...items[0], version: items[0].version + 1 };
        if (endpoint.endsWith('/assignment')) items[0] = { ...items[0], assignedWorkerId: body.workerId, version: items[0].version + 1 };
        if (endpoint.endsWith('/revoke')) items[0] = { ...items[0], status: 'Revoked', assignedWorkerId: null, version: items[0].version + 1 };
        if (lost) { lost = false; return route.abort(); }
        return route.fulfill({ json: items[0] });
      }
      reads.push(endpoint);
      if (endpoint === '/api/v1/nodes') return route.fulfill({ json: [{ ...server, capabilities: server.capabilities.map(c => ({ ...c, state: { ...c.state, authentication, installation } })) }] });
      if (endpoint.endsWith('/github-connection')) return route.fulfill({ json: { commands: operations, provisioningEnabled: true, elevationAllowed: true } });
      if (endpoint === '/api/v1/nodes/server/commands') return route.fulfill({ json: operations });
      if (endpoint === '/api/v1/credentials') return route.fulfill({ json: items });
      if (endpoint.startsWith('/api/v1/credentials/')) return route.fulfill(deleted ? { status: 404 } : { json: items.find(c => endpoint.endsWith(c.id)) ?? metadata });
      if (endpoint === '/api/v1/workers') return route.fulfill({ json: [worker] });
      return route.fulfill({ json: [] });
    });
    async function settings() {
      await page.goto('https://dashboard.test/settings?node=server');
      await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).waitFor();
      await page.getByRole('link', { name: 'Show metadata', exact: true }).first().waitFor();
    }
    async function refresh() { await page.getByRole('button', { name: 'Refresh authoritative state', exact: true }).click(); }
    for (const width of [1280, 375]) {
      await page.setViewportSize({ width, height: 1000 });
      operations = []; authentication = 'Missing'; items = [metadata];
      await settings();
      console.log(`Settings overlays: ${width}px`);
      assert.equal(mutations.length, 0, 'Render/navigation must not submit');
      for (const theme of ['light', 'dark']) {
        console.log(`Theme: ${theme}`);
        if ((await page.locator('html').getAttribute('class') ?? '').includes('dark-mode') !== (theme === 'dark')) await page.getByRole('button', { name: `Use ${theme} mode` }).first().click();
        await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).click();
        const dialog = page.getByRole('dialog');
        await dialog.waitFor();
        await page.screenshot({ timeout: 10000, animations: 'disabled', path: path.join(output, `preparation-${theme}-${width}.png`), fullPage: false });
        assert.equal(await dialog.getByRole('button', { name: 'Confirm operation' }).isDisabled(), true);
        await dialog.getByRole('checkbox').press('Space');
        await page.keyboard.press('Escape');
        await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).click();
        assert.equal(await page.getByRole('dialog').getByRole('checkbox').isChecked(), false);
        await page.keyboard.press('Escape');
        const metadataLink = page.getByRole('link', { name: 'Show metadata', exact: true }).first();
        assert.equal(await metadataLink.getAttribute('href'), '/settings/credential-a?node=server');
        await page.evaluate(() => { window.settingsNavigationFixture = 'retained'; });
        await metadataLink.click();
        await page.getByRole('dialog').getByText('credential:credential-a', { exact: true }).waitFor();
        assert.equal(await page.evaluate(() => window.settingsNavigationFixture), 'retained', 'Metadata navigation retains the session and pending mutation owners.');
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.screenshot({ timeout: 10000, animations: 'disabled', path: path.join(output, `metadata-${theme}-${width}.png`), fullPage: false });
        await page.reload();
        await page.getByRole('dialog').getByText('credential:credential-a', { exact: true }).waitFor();
        await page.keyboard.press('Escape');
        assert.equal(new URL(page.url()).pathname, '/settings');
        // Secret fields are never populated during screenshot review.
        await page.getByRole('button', { name: 'Add credential', exact: true }).click();
        await page.getByRole('dialog').waitFor();
        await page.waitForFunction(() => document.querySelector('[role=dialog]')?.contains(document.activeElement));
        for (let i = 0; i < 7; i++) await page.keyboard.press('Tab');
        assert.equal(await page.getByRole('dialog').evaluate(element => element.contains(document.activeElement)), true);
        await page.screenshot({ timeout: 10000, animations: 'disabled', path: path.join(output, `form-${theme}-${width}.png`), fullPage: false });
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => document.activeElement?.textContent === 'Add credential');
        await page.getByRole('button', { name: 'Revoke', exact: true }).click();
        await page.getByRole('dialog').waitFor();
        await page.screenshot({ timeout: 10000, animations: 'disabled', path: path.join(output, `confirmation-${theme}-${width}.png`), fullPage: false });
        await page.keyboard.press('Escape');
        installation = 'Missing'; await refresh();
        await page.getByRole('button', { name: 'Install GitHub CLI', exact: true }).click();
        assert.equal(await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).isDisabled(), true);
        await page.getByRole('dialog').getByRole('checkbox').press('Space');
        assert.equal(await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).isDisabled(), false);
        await page.screenshot({ timeout: 10000, animations: 'disabled', path: path.join(output, `elevation-${theme}-${width}.png`), fullPage: false });
        await page.keyboard.press('Escape');
        await page.getByRole('button', { name: 'Install GitHub CLI', exact: true }).click();
        assert.equal(await page.getByRole('dialog').getByRole('checkbox').isChecked(), false);
        await page.keyboard.press('Escape');
        installation = 'Installed'; await refresh();
      }
    }
    await page.setViewportSize({ width: 1280, height: 1000 });
    await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).click();
    await page.getByRole('dialog').getByRole('checkbox').press('Space');
    await page.getByRole('link', { name: 'Home', exact: true }).first().evaluate(element => element.click());
    await page.getByRole('heading', { name: 'Home', exact: true }).waitFor();
    await page.getByRole('link', { name: 'Settings', exact: true }).first().click();
    await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).click();
    assert.equal(await page.getByRole('dialog').getByRole('checkbox').isChecked(), false);
    await page.keyboard.press('Escape');
    await page.clock.install({ time: epoch });
    // Preparation requires explicit consent; device login is a separate explicit mutation.
    await page.getByRole('button', { name: 'Connect · prepare authentication', exact: true }).click();
    await page.getByRole('dialog').getByRole('checkbox').press('Space');
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).click();
    await page.getByRole('button', { name: 'Start device login', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Start device login', exact: true }).click();
    await page.getByRole('dialog').getByRole('checkbox').press('Space');
    lost = true;
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).click();
    await page.getByRole('dialog').getByText('Result unavailable.', { exact: false }).waitFor();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Check Server authentication', exact: true }).isDisabled(), true);
    await refresh();
    await page.getByRole('button', { name: 'Cancel queued operation', exact: true }).waitFor();
    const count = mutations.length;
    await page.reload();
    await page.getByRole('button', { name: 'Cancel queued operation', exact: true }).waitFor();
    assert.equal(mutations.length, count, 'Reload does not replay a lost write');
    await page.getByRole('button', { name: 'Cancel queued operation', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).click();
    await page.getByRole('button', { name: 'Start device login', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Start device login', exact: true }).click();
    await page.getByRole('dialog').getByRole('checkbox').press('Space');
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    operations[0] = { ...operations[0], status: 'Running', loginInstructions: { verificationUri: 'https://github.com/login/device', userCode: 'TEST-CODE' } };
    await refresh();
    await page.getByText('TEST-CODE', { exact: true }).waitFor();
    assert.equal(await page.getByText('Connected', { exact: true }).count(), 0);
    assert.equal(await page.evaluate(() => JSON.stringify(localStorage).includes('TEST-CODE') || JSON.stringify(sessionStorage).includes('TEST-CODE')), false);
    await page.reload();
    await page.getByText('TEST-CODE', { exact: true }).waitFor();
    assert.equal(mutations.length, count + 2);
    // Expiry is driven by the deadline timer, without waiting for the next poll.
    await page.clock.fastForward(20001);
    await page.waitForFunction(() => !document.body.textContent.includes('TEST-CODE'));
    await page.getByRole('button', { name: 'Reconcile after node quiescence', exact: true }).click();
    assert.equal(await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).isDisabled(), true);
    await page.getByRole('dialog').getByRole('checkbox').press('Space');
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm operation' }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    operations = [{ ...operations[0], status: 'Succeeded' }]; authentication = 'Satisfied';
    await refresh(); await page.getByText('Connected', { exact: true }).waitFor();
    // Credential lifecycle: only request bodies contain transient secret inputs.
    await page.getByRole('button', { name: 'Add credential', exact: true }).click();
    await page.getByLabel('Provider').fill('New provider');
    await page.getByLabel('Credential type').fill('Token');
    await page.getByRole('dialog').getByLabel('Secret').fill('fixture-transient-secret');
    await page.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByText('New provider · Token', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Replace secret', exact: true }).first().click();
    await page.getByRole('dialog').getByLabel('Secret').fill('fixture-replacement-secret');
    lost = true;
    await page.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('dialog').getByText('Result unavailable.', { exact: false }).waitFor();
    assert.equal(await page.getByRole('dialog').getByLabel('Secret').inputValue(), '');
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('link', { name: 'Home', exact: true }).first().click();
    await page.getByRole('heading', { name: 'Home', exact: true }).waitFor();
    await page.getByRole('link', { name: 'Settings', exact: true }).first().click();
    await page.getByRole('button', { name: 'Refresh authoritative credential state', exact: true }).click();
    await page.getByText('Credential outcome confirmed from current metadata. No secret was retrieved.', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Assign', exact: true }).first().click();
    await page.getByRole('dialog').getByRole('button', { name: worker.displayName, exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByText(`Version 3 · Assigned to ${worker.displayName}`, { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Revoke', exact: true }).first().click();
    await page.getByRole('dialog').getByRole('button', { name: 'Revoke', exact: true }).click();
    await page.getByText('Revoked', { exact: true }).waitFor();
    assert.equal(reads.some(endpoint => endpoint.includes('/workers/') && endpoint.includes('/credentials/')), false);
    assert.equal(reads.some(endpoint => endpoint.endsWith('/secret')), false);
    deleted = true;
    await page.getByRole('link', { name: 'Show metadata', exact: true }).first().click();
    await page.getByRole('dialog').getByText('Credential unavailable or deleted.', { exact: false }).waitFor();
    await page.keyboard.press('Escape');
    // Logout destroys open fields and operation challenges.
    await page.getByRole('button', { name: 'Add credential', exact: true }).click();
    await page.getByRole('dialog').getByLabel('Secret').fill('discard-on-logout');
    // Navigation/session action is invoked as an external session change while modal focus is contained.
    await page.getByRole('button', { name: 'Sign out', exact: true }).evaluate(button => button.click());
    await page.getByRole('button', { name: 'Sign in', exact: true }).waitFor();
    assert.equal(await page.getByRole('dialog').count(), 0);
    assert.equal(await page.evaluate(() => JSON.stringify(localStorage).includes('secret') || JSON.stringify(sessionStorage).includes('secret')), false);
    assert.deepEqual(errors, []);
    console.log('Settings browser fixtures passed at desktop/mobile widths and in both themes. Screenshots contain no secrets or device codes.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
