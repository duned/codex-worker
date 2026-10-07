// Canonical production application with deterministic Server contracts; no live nodes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { props } = require('./worker-poc-fixtures.cjs');
const assets = path.resolve('src/CodexServer/obj/worker-poc/preview');
const output = path.resolve(process.argv[2] || '/tmp/workers-migration-review');
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage(), errors = [], writes = [];
    page.setDefaultTimeout(10000);
    await page.clock.install({ time: props.now });
    page.on('pageerror', error => { errors.push(error.message); console.error('Browser error:', error.message); });
    page.on('dialog', () => errors.push('Native browser dialog is forbidden.'));
    let policy = 'Disabled', canActivate = true, delivery = 'active', authentication = 'active';
    let commands = [], loseProvisioning = false, loseDelivery = false, stale = false, failReads = false, empty = false, signedIn = true, enrolled = false, quiet = false, failedCapability = false;
    const newId = 'b'.repeat(32);
    const registry = () => ({ ...props.observations[0], activeExecutions: quiet ? 0 : 2, availableCapacity: quiet ? 2 : 0, activeAssignments: quiet ? 0 : 2, schedulingPolicy: policy, authenticationCredentialStatus: authentication, workerVersion: '0.15.0' });
    const inventory = () => empty ? [] : [registry(), ...(enrolled ? [{ ...registry(), workerId: newId, displayName: 'New Worker', authenticationCredentialStatus: 'active' }] : [])];
    const node = () => ({ ...props.nodes[0], observationsStale: stale, capabilities: [{ ...props.nodes[0].capabilities[0], state: failedCapability ? { ...props.nodes[0].capabilities[0].state, health: 'Failed', operation: { state: 'Failed', action: 'Login', diagnosticCode: 'authentication-required' } } : props.nodes[0].capabilities[0].state, availableActions: ['refresh', 'install', 'login', 'checkauthentication'] }] });
    await page.route('https://dashboard.test/**', async route => {
      const request = route.request(), url = new URL(request.url()), endpoint = url.pathname;
      if (endpoint.startsWith('/dashboard-assets/preview/')) return route.fulfill({ path: path.join(assets, endpoint.slice('/dashboard-assets/preview/'.length)), contentType: endpoint.endsWith('.css') ? 'text/css' : 'text/javascript' });
      if (endpoint === '/workers' || /^\/workers\/[^/]+$/.test(endpoint)) return route.fulfill({ path: path.join(assets, 'index.html'), contentType: 'text/html' });
      if (endpoint === '/api/v1/administration/session') {
        if (request.method() === 'DELETE') { signedIn = false; return route.fulfill({ status: 204 }); }
        if (request.method() === 'POST') signedIn = true;
        return route.fulfill({ status: signedIn ? 200 : 401, json: { csrfToken: 'fixture-csrf', expiresAtUtc: '2026-01-01T01:00:00Z' } });
      }
      if (endpoint === '/api/v1/events/stream') return route.fulfill({ status: 503 });
      if (request.method() !== 'GET') {
        assert.equal(request.headers()['x-codex-csrf'], 'fixture-csrf');
        const body = request.postData() ? request.postDataJSON() : undefined;
        writes.push({ endpoint, body });
        if (endpoint.endsWith('/scheduling-policy')) { policy = body.policy; return route.fulfill({ json: registry() }); }
        if (endpoint.endsWith('/authentication/revoke')) { authentication = 'revoked'; return route.fulfill({ json: registry() }); }
        if (endpoint.endsWith('/credential-access/revoke')) { delivery = 'revoked'; return route.fulfill({ status: loseDelivery ? 503 : 200, json: { status: delivery } }); }
        if (endpoint === '/api/v1/provisioning/commands') {
          commands = [{ id: 'retained-command', createdAtUtc: '2026-01-01T00:02:30Z', status: 'Pending', request: body }];
          return route.fulfill({ status: loseProvisioning ? 503 : 201, json: commands[0] });
        }
        if (endpoint.endsWith('/cancel') || endpoint.endsWith('/reconcile')) {
          if (endpoint.endsWith('/reconcile')) assert.equal(url.searchParams.get('nodeQuiescent'), 'true');
          commands = [{ ...commands[0], status: 'Cancelled' }]; return route.fulfill({ json: commands[0] });
        }
        if (endpoint === '/api/v1/workers/onboarding/authorize') {
          assert.equal(body.workerId, newId); return route.fulfill({ json: { authorization: 'transient-fixture-authorization', lifetimeSeconds: 900 } });
        }
        throw Error(`Unexpected mutation ${endpoint}`);
      }
      if (failReads && endpoint === '/api/v1/workers') return route.fulfill({ status: 503 });
      const fixtures = {
        '/api/version': { version: '0.15.0' }, '/api/v1/workers': inventory(), '/api/v1/workers/worker-a': registry(),
        '/api/v1/workers/worker-a/credential-access': { status: delivery }, '/api/v1/nodes': [node()], '/api/v1/nodes/worker-a/commands': commands,
        '/api/v1/workers/worker-a/diagnostics': { ...props.diagnostics, canActivate, activationBlockingReasons: canActivate ? [] : ['Current readiness unavailable'], capabilityObservationsCurrent: !stale, projects: [{ projectId: 'project-a', projectName: 'Sample project', isEligible: true, missingRequirements: [], workerReportedRevision: 1, observationStatus: stale ? 'stale-heartbeat' : 'worker-reported-current-revision', materializationState: 'not-materialized' }] },
        '/api/v1/projects': props.projects.map(item => ({ ...item, revision: 1, enabled: true, requirements: [] })), '/api/v1/provisioning': [],
        '/api/v1/executions': empty || quiet ? [] : [...props.executions, { ...props.executions[0], id: 'second-slot', currentStage: 'Codex', workReference: { type: 'github-issue', id: '28' } }]
      };
      assert.ok(Object.hasOwn(fixtures, endpoint), `Unexpected read ${endpoint}`);
      return route.fulfill({ json: fixtures[endpoint] });
    });
    const detail = 'https://dashboard.test/workers/worker-a?step=preparation&project=project-a';
    for (const width of [1280, 375]) {
      await page.setViewportSize({ width, height: 1000 }); await page.goto(detail);
      await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
      await page.getByRole('heading', { name: 'Current executions', exact: true }).waitFor();
      await page.getByText('2 / 2 active', { exact: true }).waitFor();
      assert.equal(await page.getByRole('link', { name: 'Issue #28' }).getAttribute('href'), 'https://github.com/owner/repo/issues/28');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      assert.equal(await page.locator('#poc-legacy-owner').count(), 0);
      assert.equal(await page.evaluate(() => !!window.codexWorkerPoc), false);
      await page.getByRole('button', { name: 'Activate scheduling' }).click();
      await page.getByRole('dialog').waitFor(); await page.keyboard.press('Escape'); await page.clock.runFor(20);
      assert.equal(writes.length, 0);
      assert.equal(await page.getByRole('button', { name: 'Activate scheduling' }).evaluate(element => element === document.activeElement), true);
      await page.screenshot({ path: path.join(output, `worker-${width}.png`), fullPage: true });
      await page.reload(); await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
      assert.equal(new URL(page.url()).search, '?step=preparation&project=project-a');
    }
    await page.setViewportSize({ width: 1280, height: 1000 });
    // Activation is revalidated immediately before submission, despite cached permission.
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent === 'Activate scheduling' && !button.disabled));
    canActivate = false;
    await page.getByRole('button', { name: 'Activate scheduling' }).click(); await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Activation blocked: Current readiness unavailable', { exact: true }).waitFor(); assert.equal(writes.length, 0);
    canActivate = true; await page.getByRole('button', { name: 'Refresh authoritative state', exact: true }).click();
    await page.getByRole('button', { name: 'Activate scheduling' }).click(); await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Operation accepted. Current Server observations are refreshing.', { exact: true }).waitFor(); assert.equal(writes[0].body.policy, 'Enabled');
    // Separate delivery revocation with lost response, then an explicit read-only recovery.
    loseDelivery = true;
    await page.getByRole('button', { name: 'Revoke delivery authorization' }).click();
    await page.getByText(/Worker API authentication, node login and provider-side authorization are unchanged/).waitFor();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Request failed (HTTP 503). State may be stale; refresh before retrying.', { exact: true }).waitFor();
    const deliveryWrites = writes.length; await page.clock.runFor(5001);
    assert.equal(await page.getByRole('button', { name: 'Drain worker' }).isDisabled(), true);
    await page.evaluate(() => { window.workerNavigationMarker = 'retained'; });
    await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Workers', exact: true }).click();
    await page.getByRole('heading', { name: 'Workers', exact: true }).waitFor();
    await page.getByRole('link', { name: /Build Worker North/ }).click();
    await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    assert.equal(await page.evaluate(() => window.workerNavigationMarker), 'retained');
    assert.equal(await page.getByRole('button', { name: 'Drain worker' }).isDisabled(), true, 'Uncertain fence survives list/detail navigation.');
    await page.getByRole('button', { name: 'Refresh authoritative state', exact: true }).click();
    await page.getByText(/Authoritative state refreshed. Review current policy/).waitFor();
    assert.equal(writes.length, deliveryWrites); assert.equal(authentication, 'active');
    await page.goto(detail); await page.getByRole('heading', { name: 'Build Worker North' }).waitFor();
    // Provisioning consent and lost submission recover the retained command, never replay.
    await page.getByRole('button', { name: 'Install', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Confirm', exact: true }).isDisabled(), true);
    await page.getByRole('checkbox', { name: 'Authorize elevation for this action' }).focus(); await page.keyboard.press('Space');
    loseProvisioning = true; await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByRole('dialog').getByText('Result unavailable. Close this dialog and review authoritative state before another action.').waitFor();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    const provisioningWrites = writes.length;
    assert.equal(await page.getByRole('button', { name: 'Refresh / Re-detect', exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Refresh authoritative operation state', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel queued operation' }).waitFor(); assert.equal(writes.length, provisioningWrites);
    await page.reload(); await page.getByRole('button', { name: 'Cancel queued operation' }).click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText(/Operation accepted. Retained progress/).waitFor();
    assert.equal(writes.at(-1).endpoint, '/api/v1/provisioning/commands/retained-command/cancel');
    const deadline = new Date(await page.evaluate(() => Date.now()) + 1000).toISOString();
    commands = [{ ...commands[0], status: 'Running', deadlineUtc: deadline, loginInstructions: { verificationUri: 'https://auth.openai.com/codex/device', userCode: 'DEVICE-CODE' } }]; await page.reload();
    await page.getByText('DEVICE-CODE', { exact: true }).waitFor();
    await page.clock.runFor(1100);
    await page.getByText('DEVICE-CODE', { exact: true }).waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Reconcile after node quiescence' }).click();
    assert.equal(await page.getByRole('button', { name: 'Confirm', exact: true }).isDisabled(), true);
    await page.getByRole('checkbox', { name: 'I verified the node is quiescent and the mutation has stopped' }).focus(); await page.keyboard.press('Space');
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText(/Operation accepted. Retained progress/).waitFor();
    // URL steps and lazy materialization remain independent from activation.
    await page.getByRole('link', { name: '3 · Project context' }).click();
    await page.getByText(/Missing checkouts are materialized lazily on assignment/).waitFor();
    await page.getByText(/Checkout: not-materialized/).waitFor();
    assert.equal(new URL(page.url()).searchParams.get('project'), 'project-a');
    quiet = true; await page.reload(); await page.getByText(/No active work reported by this Worker/).waitFor();
    await page.getByText('No recent terminal executions for this Worker in this view.', { exact: true }).waitFor();
    quiet = false; failedCapability = true; await page.reload(); await page.getByText('Failed', { exact: true }).first().waitFor();
    failedCapability = false; stale = true; await page.reload(); await page.getByText('Stale', { exact: true }).waitFor();
    failReads = true; await page.reload(); await page.getByText(/Current Worker observations unavailable/).waitFor();
    failReads = false; empty = true; await page.reload(); await page.getByText(/Worker unavailable or deleted/).waitFor();
    empty = false; stale = false;
    await page.goto(detail);
    await page.getByRole('button', { name: 'Revoke Worker API token' }).click();
    await page.getByText(/Calls using this token will be denied and active leases may expire into recovery/).waitFor();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Operation accepted. Current Server observations are refreshing.', { exact: true }).waitFor();
    assert.equal(writes.at(-1).endpoint, '/api/v1/workers/worker-a/authentication/revoke');
    assert.equal(delivery, 'revoked');
    await page.getByRole('button', { name: 'Deactivate', exact: true }).click(); await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await page.getByText('Operation accepted. Current Server observations are refreshing.', { exact: true }).waitFor();
    assert.equal(writes.at(-1).body.policy, 'Disabled');
    // Enrollment reload restores the public request, never authorization or a submitted action.
    await page.goto('https://dashboard.test/workers?prepare=1'); await page.getByRole('dialog', { name: 'Add Worker' }).waitFor();
    await page.getByRole('combobox', { name: 'Machine' }).selectOption('associate');
    await page.getByText(/Drain and reconcile leases with the previous Server/).waitFor();
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await page.getByText(/--operation associate --pair/).waitFor();
    await page.getByRole('button', { name: 'Back', exact: true }).click();
    await page.getByRole('combobox', { name: 'Machine' }).selectOption('enroll');
    await page.getByRole('button', { name: 'Next', exact: true }).click(); await page.getByRole('button', { name: 'Next', exact: true }).click();
    await page.getByRole('textbox', { name: 'Public pairing request' }).fill(JSON.stringify({ contractVersion: 1, workerId: newId, operation: 'enroll', server: 'https://dashboard.test' }));
    await page.getByRole('button', { name: 'Authorize pairing' }).click(); await page.getByLabel('Short-lived authorization').waitFor();
    const enrollmentWrites = writes.length;
    assert.ok(!(await page.evaluate(() => JSON.stringify(sessionStorage))).includes('transient-fixture-authorization'));
    await page.reload(); await page.getByRole('button', { name: 'Check progress' }).waitFor();
    assert.equal(await page.getByLabel('Short-lived authorization').count(), 0);
    assert.equal(await page.getByRole('button', { name: 'Authorize pairing' }).isDisabled(), true);
    assert.equal(writes.length, enrollmentWrites);
    enrolled = true; await page.getByRole('button', { name: 'Check progress' }).click();
    await page.getByRole('link', { name: 'Continue preparation' }).waitFor();
    assert.equal(await page.getByRole('link', { name: 'Continue preparation' }).getAttribute('href'), `/workers/${newId}?step=preparation`);
    assert.equal(writes.length, enrollmentWrites);
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    assert.equal(new URL(page.url()).searchParams.has('prepare'), false);
    await page.getByRole('button', { name: 'Use light mode' }).click(); await page.reload();
    assert.equal(await page.locator('html').evaluate(element => element.classList.contains('dark-mode')), false);
    await page.getByRole('button', { name: 'Sign out', exact: true }).click(); await page.getByText('Administration sign in', { exact: true }).waitFor();
    assert.deepEqual(errors, []);
    console.log('Canonical Workers: responsive hierarchy, concurrent slots, Server revalidation, response-loss recovery, consent, reconciliation, enrollment reload, theme and logout passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
